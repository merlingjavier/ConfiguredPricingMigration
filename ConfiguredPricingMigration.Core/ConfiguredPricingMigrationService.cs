using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ConfiguredPricingMigration.Core;

public sealed class ConfiguredPricingMigrationService
{
    private const string Runs = "ConfiguredPricingMigrationRuns";
    private const string Rows = "ConfiguredPricingMigrationRows";
    private const string Schemas = "ConfiguredPricingSchemas";
    private const string Versions = "ConfiguredPricingVersions";
    private const string AmountRanges = "ConfiguredPricingAmountRanges";
    private const string TermRanges = "ConfiguredPricingTermRanges";
    private const string DimensionGroups = "ConfiguredPricingDimensionGroups";
    private const string LegacyQuadrants = "ConfiguredPricingOfficeQuadrants";
    private const string RateSets = "ConfiguredPricingRates";
    private const string Audit = "ConfiguredPricingAudit";
    private const int ModelVersion = 16;
    private const string Ordinary = "ORDINARY";
    private const string Baseline = "BASELINE";
    private const string Special = "SPECIAL";

    private readonly MigrationOptions _options;
    private readonly IMongoDatabase _migrationDatabase;
    private readonly IReadOnlyList<int> _productIds;

    public ConfiguredPricingMigrationService(MigrationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SqlConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MigrationMongoConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MigrationMongoDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetMongoConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetMongoDatabase);

        var usesSameMongoServer = string.Equals(
            options.MigrationMongoConnectionString,
            options.TargetMongoConnectionString,
            StringComparison.OrdinalIgnoreCase
        );
        var usesSameMongoDatabase = string.Equals(
            options.MigrationMongoDatabase,
            options.TargetMongoDatabase,
            StringComparison.OrdinalIgnoreCase
        );

        if (usesSameMongoServer && usesSameMongoDatabase)
        {
            throw new ArgumentException(
                "La base de staging no puede ser la misma que la base operativa."
            );
        }

        if (options.BatchSize is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BatchSize));
        }

        if (options.ProductIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProductIds));
        }

        if (options.TypeSchema is not (Ordinary or Baseline or Special))
        {
            throw new ArgumentException(
                "TypeSchema debe ser ORDINARY, BASELINE o SPECIAL.",
                nameof(options.TypeSchema)
            );
        }

        if (options.TypeSchema != Ordinary && options.ProductIds.Count > 0)
        {
            throw new ArgumentException(
                "BASELINE y SPECIAL son esquemas globales y no admiten filtro por producto.",
                nameof(options.ProductIds)
            );
        }

        _options = options;
        _productIds = options.ProductIds.Distinct().Order().ToArray();
        _migrationDatabase = new MongoClient(options.MigrationMongoConnectionString).GetDatabase(
            options.MigrationMongoDatabase
        );
    }

    public async Task TestConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(_options.SqlConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT 1", sql);
        await command.ExecuteScalarAsync(cancellationToken);
        await _migrationDatabase.RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1),
            cancellationToken: cancellationToken
        );

        if (!string.IsNullOrWhiteSpace(_options.TargetMongoConnectionString))
        {
            await TargetDatabase()
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1),
                    cancellationToken: cancellationToken
                );
        }
    }

    public async Task<MigrationSummary> MigrateAsync(
        string runId,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureIndexesAsync(cancellationToken);

        var runCollection = _migrationDatabase.GetCollection<BsonDocument>(Runs);
        var runFilter = Builders<BsonDocument>.Filter.Eq("_id", runId);
        var run = await runCollection.Find(runFilter).FirstOrDefaultAsync(cancellationToken);
        const long initialRateId = 0;

        if (run is not null && run.GetValue("modelVersion", 1).ToInt32() != ModelVersion)
        {
            await ResetLegacyRunAsync(runId, run, initialRateId, cancellationToken);
            progress?.Report(
                new MigrationProgress(
                    "RESET",
                    0,
                    0,
                    0,
                    "El Run ID usa el modelo anterior; se regenerara desde SQL Server."
                )
            );
            run = await runCollection.Find(runFilter).FirstOrDefaultAsync(cancellationToken);
        }

        if (
            run is not null
            && run.GetValue("modelVersion", ModelVersion).ToInt32() == ModelVersion
            && !string.Equals(
                run.GetValue("typeSchema", Ordinary).AsString,
                _options.TypeSchema,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                "El Run ID ya está asociado a otro tipo de esquema. Use un Run ID distinto para cada tipo."
            );
        }

        if (_options.TypeSchema is Baseline or Special)
        {
            return await MigrateGlobalAsync(runId, run, progress, cancellationToken);
        }

        var productIds = run is null
            ? await ResolveProductIdsAsync(cancellationToken)
            : run["productIds"].AsBsonArray.Select(value => value.ToInt32()).ToArray();

        if (productIds.Count == 0)
        {
            throw new InvalidOperationException(
                "No se encontraron productos vigentes del módulo de créditos con filas SI_FinTasa "
                    + $"para el esquema {_options.TypeSchema}."
            );
        }

        EnsureRunProductsMatch(run, productIds);
        var extracted = run?.GetValue("extracted", 0).ToInt64() ?? 0;
        var loaded = run?.GetValue("loaded", 0).ToInt64() ?? 0;
        var rejected = run?.GetValue("rejected", 0).ToInt64() ?? 0;

        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.SetOnInsert("startedAt", DateTime.UtcNow)
                .SetOnInsert("productIds", new BsonArray(productIds.Select(id => (BsonValue)id)))
                .SetOnInsert(
                    "requestedProductIds",
                    new BsonArray(productIds.Select(id => (BsonValue)id))
                )
                .SetOnInsert("rootRunId", runId)
                .SetOnInsert(
                    "productStates",
                    new BsonDocument(
                        productIds.Select(id => new BsonElement(
                            id.ToString(),
                            new BsonDocument
                            {
                                { "status", "PENDING" },
                                { "lastRateId", initialRateId },
                            }
                        ))
                    )
                )
                .Set("modelVersion", ModelVersion)
                .Set("typeSchema", _options.TypeSchema)
                .Set("status", "RUNNING")
                .Set("updatedAt", DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true },
            cancellationToken
        );

        CatalogSyncSummary catalogSync;
        try
        {
            catalogSync = await new CatalogSyncService(
                new CatalogSyncOptions(
                    _options.SqlConnectionString,
                    _options.TargetMongoConnectionString,
                    _options.TargetMongoDatabase
                )
            ).SyncAsync($"catalog-snapshot:{runId}", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new MigrationStoppedException(
                await CloseStoppedRunAsync(runId, null, CancellationToken.None)
            );
        }
        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>.Update.Set(
                "catalogSync",
                new BsonDocument
                {
                    { "runId", catalogSync.RunId },
                    { "completedAt", DateTime.UtcNow },
                    {
                        "catalogs",
                        new BsonArray(
                            catalogSync.Catalogs.Select(catalog => new BsonDocument
                            {
                                { "collection", catalog.Key },
                                { "loaded", catalog.Value.Loaded },
                                { "deactivated", catalog.Value.Deactivated },
                            })
                        )
                    },
                }
            ),
            cancellationToken: cancellationToken
        );
        progress?.Report(
            new MigrationProgress(
                "CATALOG_SYNC",
                catalogSync.Catalogs.Sum(catalog => catalog.Value.Loaded),
                0,
                0,
                "Catalogos operativos sincronizados."
            )
        );
        run = await runCollection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
            .FirstAsync(cancellationToken);
        int? currentProductId = null;
        try
        {
            foreach (var productId in productIds)
            {
                if (ProductState(run, productId).GetValue("status", "PENDING").AsString == "LOADED")
                    continue;
                currentProductId = productId;
                var state = ProductState(run, productId);
                var lastRateId = state.GetValue("lastRateId", initialRateId).ToInt64();
                var productExtracted = 0L;
                var productLoaded = 0L;
                var productRejected = 0L;
                await UpdateProductStateAsync(
                    runId,
                    productId,
                    Builders<BsonDocument>
                        .Update.Set($"productStates.{productId}.status", "RUNNING")
                        .Set($"productStates.{productId}.startedAt", DateTime.UtcNow),
                    cancellationToken
                );
                progress?.Report(
                    new MigrationProgress(
                        "PRODUCT_START",
                        extracted,
                        loaded,
                        rejected,
                        $"Producto {productId}: iniciando migración."
                    )
                );
                while (true)
                {
                    var batch = await ReadBatchAsync(
                        runId,
                        productId,
                        lastRateId,
                        _options.BatchSize,
                        _options.TypeSchema,
                        cancellationToken
                    );
                    if (batch.Count == 0)
                        break;
                    var writes = batch
                        .Select(row =>
                            (WriteModel<BsonDocument>)
                                new ReplaceOneModel<BsonDocument>(
                                    Builders<BsonDocument>.Filter.Eq("_id", row["_id"]),
                                    row
                                )
                                {
                                    IsUpsert = true,
                                }
                        )
                        .ToList();
                    await _migrationDatabase
                        .GetCollection<BsonDocument>(Rows)
                        .BulkWriteAsync(
                            writes,
                            new BulkWriteOptions { IsOrdered = false },
                            cancellationToken
                        );
                    lastRateId = batch[^1]["legacyRateId"].ToInt64();
                    productExtracted += batch.Count;
                    productLoaded += writes.Count;
                    await UpdateProductStateAsync(
                        runId,
                        productId,
                        Builders<BsonDocument>
                            .Update.Set($"productStates.{productId}.lastRateId", lastRateId)
                            .Set($"productStates.{productId}.extracted", productExtracted)
                            .Set($"productStates.{productId}.loaded", productLoaded)
                            .Set($"productStates.{productId}.rejected", productRejected)
                            .Set($"productStates.{productId}.updatedAt", DateTime.UtcNow),
                        cancellationToken
                    );
                    progress?.Report(
                        new MigrationProgress(
                            "EXTRACT",
                            extracted + productExtracted,
                            loaded + productLoaded,
                            rejected + productRejected,
                            $"Producto {productId}: procesado hasta idTasa {lastRateId}."
                        )
                    );
                }
                if (productExtracted == 0 && state.GetValue("extracted", 0).ToInt64() == 0)
                    throw new InvalidOperationException(
                        $"El producto {productId} no tiene tasas vigentes elegibles del módulo de créditos (módulo 1)."
                    );
                await BackfillCoordinatesAsync(runId, productId, cancellationToken);
                await InferDimensionGroupsAsync(runId, productId, cancellationToken);
                var pricingId = PricingId(productId, Ordinary);
                var versionIds = await BuildConfiguredPricingAsync(
                    runId,
                    productId,
                    pricingId,
                    progress,
                    cancellationToken
                );
                var rateSetCount = await _migrationDatabase
                    .GetCollection<BsonDocument>(RateSets)
                    .CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.And(
                            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                            Builders<BsonDocument>.Filter.Eq("configuredPricingId", pricingId)
                        ),
                        cancellationToken: cancellationToken
                    );
                extracted += productExtracted;
                loaded += productLoaded;
                rejected += productRejected;
                await UpdateProductStateAsync(
                    runId,
                    productId,
                    Builders<BsonDocument>
                        .Update.Set($"productStates.{productId}.status", "LOADED")
                        .Set($"productStates.{productId}.completedAt", DateTime.UtcNow)
                        .Set($"productStates.{productId}.versionIds", new BsonArray(versionIds))
                        .Set($"productStates.{productId}.rateSets", rateSetCount),
                    cancellationToken
                );
                await runCollection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", runId),
                    Builders<BsonDocument>
                        .Update.Set("extracted", extracted)
                        .Set("loaded", loaded)
                        .Set("rejected", rejected)
                        .Set("updatedAt", DateTime.UtcNow),
                    cancellationToken: cancellationToken
                );
                progress?.Report(
                    new MigrationProgress(
                        "PRODUCT_COMPLETED",
                        extracted,
                        loaded,
                        rejected,
                        $"Producto {productId}: terminado; conjuntos={rateSetCount}."
                    )
                );
                currentProductId = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (currentProductId is not null)
                await DiscardProductAsync(runId, currentProductId.Value, CancellationToken.None);
            throw new MigrationStoppedException(
                await CloseStoppedRunAsync(runId, currentProductId, CancellationToken.None)
            );
        }

        var allVersionIds = (
            await runCollection
                .Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
                .FirstAsync(cancellationToken)
        )["productStates"]
            .AsBsonDocument.Elements.SelectMany(element =>
                element.Value.AsBsonDocument.GetValue("versionIds", new BsonArray()).AsBsonArray
            )
            .Select(value => value.AsString)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var allRateSetCount = await _migrationDatabase
            .GetCollection<BsonDocument>(RateSets)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                cancellationToken: cancellationToken
            );
        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.Set("status", "LOADED")
                .Set("completedAt", DateTime.UtcNow)
                .Set("configuredPricingVersionIds", new BsonArray(allVersionIds))
                .Set("rateSets", allRateSetCount),
            cancellationToken: cancellationToken
        );

        return new MigrationSummary(
            runId,
            extracted,
            loaded,
            rejected,
            allRateSetCount,
            allVersionIds
        );
    }

    private async Task<MigrationSummary> MigrateGlobalAsync(
        string runId,
        BsonDocument? existingRun,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var runs = _migrationDatabase.GetCollection<BsonDocument>(Runs);
        var state =
            existingRun?.GetValue("globalState", new BsonDocument()).AsBsonDocument
            ?? new BsonDocument();
        var lastRateId = state.GetValue("lastRateId", 0).ToInt64();
        var extracted = existingRun?.GetValue("extracted", 0).ToInt64() ?? 0;
        var loaded = existingRun?.GetValue("loaded", 0).ToInt64() ?? 0;
        var runFilter = Builders<BsonDocument>.Filter.Eq("_id", runId);
        var startUpdate = Builders<BsonDocument>
            .Update.SetOnInsert("startedAt", DateTime.UtcNow)
            .SetOnInsert("rootRunId", runId)
            .SetOnInsert("productIds", new BsonArray())
            .SetOnInsert("requestedProductIds", new BsonArray())
            .Set("typeSchema", _options.TypeSchema)
            .Set("modelVersion", ModelVersion)
            .Set("status", "RUNNING")
            .Set("globalState.status", "RUNNING")
            .Set("updatedAt", DateTime.UtcNow);

        await runs.UpdateOneAsync(
            runFilter,
            startUpdate,
            new UpdateOptions { IsUpsert = true },
            cancellationToken
        );

        try
        {
            var catalogOptions = new CatalogSyncOptions(
                _options.SqlConnectionString,
                _options.TargetMongoConnectionString,
                _options.TargetMongoDatabase
            );
            var snapshot = await new CatalogSyncService(catalogOptions).SyncAsync(
                $"catalog-snapshot:{runId}",
                cancellationToken
            );

            await UpdateCatalogSnapshotAsync(runs, runFilter, snapshot, cancellationToken);

            while (true)
            {
                var batch = await ReadBatchAsync(
                    runId,
                    null,
                    lastRateId,
                    _options.BatchSize,
                    _options.TypeSchema,
                    cancellationToken
                );
                if (batch.Count == 0)
                {
                    break;
                }

                var writes = CreateReplaceWrites(batch);
                await _migrationDatabase
                    .GetCollection<BsonDocument>(Rows)
                    .BulkWriteAsync(
                        writes,
                        new BulkWriteOptions { IsOrdered = false },
                        cancellationToken
                    );

                lastRateId = batch[^1]["legacyRateId"].ToInt64();
                extracted += batch.Count;
                loaded += writes.Count;

                var checkpointUpdate = Builders<BsonDocument>
                    .Update.Set("globalState.lastRateId", lastRateId)
                    .Set("extracted", extracted)
                    .Set("loaded", loaded)
                    .Set("updatedAt", DateTime.UtcNow);
                await runs.UpdateOneAsync(
                    runFilter,
                    checkpointUpdate,
                    cancellationToken: cancellationToken
                );

                progress?.Report(
                    new MigrationProgress(
                        "EXTRACT",
                        extracted,
                        loaded,
                        0,
                        $"{_options.TypeSchema}: procesado hasta idTasa {lastRateId}."
                    )
                );
            }

            if (extracted == 0)
            {
                throw new InvalidOperationException(
                    "No se encontraron filas SI_FinTasa vigentes para el esquema "
                        + $"{_options.TypeSchema} que cumplan los catálogos obligatorios del módulo de créditos."
                );
            }

            if (_options.TypeSchema == Special)
            {
                await InferSpecialGroupsAsync(runId, cancellationToken);
            }

            var configuredPricingId = PricingId(0, _options.TypeSchema);
            var versionIds = await BuildConfiguredPricingAsync(
                runId,
                null,
                configuredPricingId,
                progress,
                cancellationToken
            );
            var rateSetFilter = Builders<BsonDocument>.Filter.Eq("migrationRunId", runId);
            var rateSets = await _migrationDatabase
                .GetCollection<BsonDocument>(RateSets)
                .CountDocumentsAsync(rateSetFilter, cancellationToken: cancellationToken);
            var completeUpdate = Builders<BsonDocument>
                .Update.Set("status", "LOADED")
                .Set("globalState.status", "LOADED")
                .Set("completedAt", DateTime.UtcNow)
                .Set("rateSets", rateSets)
                .Set("configuredPricingVersionIds", new BsonArray(versionIds));
            await runs.UpdateOneAsync(
                runFilter,
                completeUpdate,
                cancellationToken: cancellationToken
            );

            return new MigrationSummary(runId, extracted, loaded, 0, rateSets, versionIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new MigrationStoppedException(
                await CloseStoppedRunAsync(runId, null, CancellationToken.None)
            );
        }
    }

    private static List<WriteModel<BsonDocument>> CreateReplaceWrites(
        IEnumerable<BsonDocument> rows
    )
    {
        return rows.Select(row => new ReplaceOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", row["_id"]),
                row
            )
            {
                IsUpsert = true,
            })
            .Cast<WriteModel<BsonDocument>>()
            .ToList();
    }

    private static async Task UpdateCatalogSnapshotAsync(
        IMongoCollection<BsonDocument> runs,
        FilterDefinition<BsonDocument> runFilter,
        CatalogSyncSummary snapshot,
        CancellationToken cancellationToken
    )
    {
        var catalogs = new BsonArray(
            snapshot.Catalogs.Select(catalog => new BsonDocument
            {
                { "collection", catalog.Key },
                { "loaded", catalog.Value.Loaded },
                { "deactivated", catalog.Value.Deactivated },
            })
        );
        var catalogSnapshot = new BsonDocument
        {
            { "runId", snapshot.RunId },
            { "completedAt", DateTime.UtcNow },
            { "catalogs", catalogs },
        };

        await runs.UpdateOneAsync(
            runFilter,
            Builders<BsonDocument>.Update.Set("catalogSync", catalogSnapshot),
            cancellationToken: cancellationToken
        );
    }

    private async Task InferSpecialGroupsAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            Builders<BsonDocument>.Filter.Eq("typeSchema", Special)
        );
        await rows.UpdateManyAsync(
            filter,
            Builders<BsonDocument>
                .Update.Set("includedInRateSet", true)
                .Unset("consolidatedLegacyRateIds"),
            cancellationToken: cancellationToken
        );
        var members = new List<DimensionMember>();
        using var cursor = await rows.Find(filter)
            .Sort(
                Builders<BsonDocument>.Sort.Combine(
                    Builders<BsonDocument>.Sort.Ascending("configuredPricingVersionId"),
                    Builders<BsonDocument>.Sort.Ascending("branchId"),
                    Builders<BsonDocument>.Sort.Ascending("amountRangeId"),
                    Builders<BsonDocument>.Sort.Ascending("legacyRateId")
                )
            )
            .ToCursorAsync(cancellationToken);
        var memberRows = new List<BsonDocument>();
        string? versionId = null;
        int? branchId = null;
        void CompleteMember()
        {
            if (versionId is not null && branchId is not null && memberRows.Count > 0)
                members.Add(
                    new DimensionMember(
                        versionId,
                        branchId.Value,
                        SpecialBranchSignature(memberRows)
                    )
                );
            memberRows.Clear();
        }
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var rowVersionId = row["configuredPricingVersionId"].AsString;
                var rowBranchId = row["branchId"].ToInt32();
                if (versionId is not null && (versionId != rowVersionId || branchId != rowBranchId))
                    CompleteMember();
                versionId = rowVersionId;
                branchId = rowBranchId;
                memberRows.Add(row);
            }
        CompleteMember();

        var activeBranchIds = await LoadActiveBranchIdsAsync(cancellationToken);
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            var versionMembers in members.GroupBy(
                member => member.VersionId,
                StringComparer.Ordinal
            )
        )
        foreach (
            var equivalentMembers in versionMembers.GroupBy(
                member => member.Signature ?? $"invalid:{member.MemberId}",
                StringComparer.Ordinal
            )
        )
        {
            var memberIds = equivalentMembers.Select(member => member.MemberId).Order().ToArray();
            var groupId = await EnsureInferredDimensionGroupAsync(
                "branch",
                memberIds,
                memberIds.SequenceEqual(activeBranchIds),
                cancellationToken
            );
            foreach (var id in memberIds)
                assignments[$"{versionMembers.Key}:{id}"] = groupId;
        }
        await AssignGlobalDimensionGroupsAsync(
            filter,
            "branch",
            "branchId",
            assignments,
            cancellationToken
        );
        await ConsolidateGlobalDimensionRowsAsync(filter, "branchId", cancellationToken);
    }

    private static string? SpecialBranchSignature(IEnumerable<BsonDocument> rows)
    {
        var entries = rows.GroupBy(row => row["amountRangeId"].ToString(), StringComparer.Ordinal)
            .OrderBy(group => group.Key)
            .ToList();
        if (entries.Any(group => group.Count() != 1))
            return null;
        return string.Join(
            "|",
            entries.Select(group =>
            {
                var rate = group.Single()["rate"].AsBsonDocument;
                return $"amount={group.Key}:compensatory={rate["compensatory"]}";
            })
        );
    }

    public async Task<ValidationSummary> ValidateAsync(
        string runId,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var rateSets = _migrationDatabase.GetCollection<BsonDocument>(RateSets);
        var sourceRows = await rows.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            cancellationToken: cancellationToken
        );
        var configuredSets = await rateSets.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            cancellationToken: cancellationToken
        );
        var run = await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
            .FirstOrDefaultAsync(cancellationToken);
        var incompleteProducts = run is null
            ? 1
            : run.GetValue("productStates", new BsonDocument())
                .AsBsonDocument.Elements.Count(element =>
                    element.Value.AsBsonDocument.GetValue("status", "PENDING").AsString != "LOADED"
                );
        var duplicateRateTypes = 0L;
        var ambiguousCells = 0L;
        var groupConflicts = 0L;

        using var cursor = await rateSets
            .Find(Builders<BsonDocument>.Filter.Eq("migrationRunId", runId))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var set in cursor.Current)
            {
                var exceptions = set.GetValue("migrationExceptions", new BsonArray()).AsBsonArray;
                ambiguousCells += exceptions.Count;
                duplicateRateTypes += exceptions.Sum(x =>
                    x.AsBsonDocument["legacyRateIds"].AsBsonArray.Count - 1
                );
            }
        }

        var runSets = await rateSets
            .Find(Builders<BsonDocument>.Filter.Eq("migrationRunId", runId))
            .ToListAsync(cancellationToken);
        var groupReferences = runSets
            .SelectMany(set =>
                DimensionGroupReferences(set)
                    .Select(reference =>
                        (
                            VersionId: set["configuredPricingVersionId"].AsString,
                            Dimension: reference.Dimension,
                            GroupId: reference.GroupId
                        )
                    )
            )
            .Distinct()
            .ToList();
        var groupIds = groupReferences.Select(reference => reference.GroupId).Distinct().ToArray();
        var groups =
            groupIds.Length == 0
                ? new List<BsonDocument>()
                : await _migrationDatabase
                    .GetCollection<BsonDocument>(DimensionGroups)
                    .Find(Builders<BsonDocument>.Filter.In("_id", groupIds))
                    .ToListAsync(cancellationToken);
        var groupsById = groups.ToDictionary(
            group => group["_id"].AsString,
            StringComparer.Ordinal
        );
        var groupMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in groupReferences)
        {
            if (
                !groupsById.TryGetValue(reference.GroupId, out var group)
                || group.GetValue("dimensionKey", string.Empty).AsString != reference.Dimension
            )
            {
                groupConflicts++;
                continue;
            }
            var members = group.GetValue("memberIds", new BsonArray()).AsBsonArray;
            if (members.Count == 0)
                groupConflicts++;
            foreach (var member in members)
                if (!groupMembers.Add($"{reference.VersionId}:{reference.Dimension}:{member}"))
                    groupConflicts++;
        }
        var valid =
            sourceRows > 0
            && configuredSets > 0
            && duplicateRateTypes == 0
            && groupConflicts == 0
            && incompleteProducts == 0;
        var message = valid
            ? "Carga estructural valida."
            : "Se detectaron filas, conjuntos, grupos, productos pendientes o celdas ambiguas.";
        await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                Builders<BsonDocument>.Update.Set(
                    "validation",
                    new BsonDocument
                    {
                        { "isValid", valid },
                        { "sourceRows", sourceRows },
                        { "rateSets", configuredSets },
                        { "duplicateRateTypes", duplicateRateTypes },
                        { "ambiguousCells", ambiguousCells },
                        { "groupConflicts", groupConflicts },
                        { "incompleteProducts", incompleteProducts },
                        { "validatedAt", DateTime.UtcNow },
                    }
                ),
                cancellationToken: cancellationToken
            );
        return new ValidationSummary(
            valid,
            sourceRows,
            configuredSets,
            duplicateRateTypes,
            ambiguousCells,
            groupConflicts,
            message
        );
    }

    public async Task<MigrationPreview> GetPreviewAsync(
        string runId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var rateSets = _migrationDatabase.GetCollection<BsonDocument>(RateSets);
        var sets = await rateSets
            .Find(Builders<BsonDocument>.Filter.Eq("migrationRunId", runId))
            .ToListAsync(cancellationToken);
        var run = await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
            .FirstOrDefaultAsync(cancellationToken);
        var typeSchema = run?.GetValue("typeSchema", Ordinary).AsString ?? Ordinary;
        if (sets.Count == 0)
            return new MigrationPreview(
                Array.Empty<MigrationPreviewProduct>(),
                Array.Empty<MigrationPreviewRange>(),
                Array.Empty<MigrationPreviewRange>(),
                Array.Empty<MigrationPreviewGroup>(),
                Array.Empty<MigrationPreviewRateType>(),
                Array.Empty<MigrationPreviewRateSet>(),
                typeSchema
            );

        var pricingIds = sets.Select(set => set["configuredPricingId"].AsString)
            .Distinct()
            .ToArray();
        var amountRangeIds = sets.Select(set =>
                set["coordinates"].AsBsonDocument.GetValue("amountRangeId", BsonNull.Value)
            )
            .Where(value => value.IsString)
            .Select(value => value.AsString)
            .Distinct()
            .ToArray();
        var termRangeIds = sets.Select(set =>
                set["coordinates"].AsBsonDocument.GetValue("termRangeId", BsonNull.Value)
            )
            .Where(value => value.IsString)
            .Select(value => value.AsString)
            .Distinct()
            .ToArray();
        var amountRanges = await _migrationDatabase
            .GetCollection<BsonDocument>(AmountRanges)
            .Find(Builders<BsonDocument>.Filter.In("_id", amountRangeIds))
            .ToListAsync(cancellationToken);
        var termRanges = await _migrationDatabase
            .GetCollection<BsonDocument>(TermRanges)
            .Find(Builders<BsonDocument>.Filter.In("_id", termRangeIds))
            .ToListAsync(cancellationToken);
        var groupIds = sets.SelectMany(DimensionGroupReferences)
            .Select(reference => reference.GroupId)
            .Distinct()
            .ToArray();
        var groups =
            groupIds.Length == 0
                ? new List<BsonDocument>()
                : await _migrationDatabase
                    .GetCollection<BsonDocument>(DimensionGroups)
                    .Find(Builders<BsonDocument>.Filter.In("_id", groupIds))
                    .ToListAsync(cancellationToken);
        var schemas = await _migrationDatabase
            .GetCollection<BsonDocument>(Schemas)
            .Find(Builders<BsonDocument>.Filter.In("_id", pricingIds))
            .ToListAsync(cancellationToken);
        var rateTypeIds = sets.SelectMany(set => set["rates"].AsBsonArray)
            .Select(rate => rate.AsBsonDocument["rateTypeId"].ToInt32())
            .Distinct()
            .ToArray();
        var targetDatabase = TargetDatabase();
        var rateTypes = await targetDatabase
            .GetCollection<BsonDocument>("CatalogRateTypes")
            .Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.In("_id", rateTypeIds),
                    Builders<BsonDocument>.Filter.Eq("active", true),
                    Builders<BsonDocument>.Filter.Eq("lVigente", true),
                    Builders<BsonDocument>.Filter.Eq("idModulo", 1)
                )
            )
            .ToListAsync(cancellationToken);
        var catalogProducts = await targetDatabase
            .GetCollection<BsonDocument>("CatalogProducts")
            .Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("active", true),
                    Builders<BsonDocument>.Filter.Eq("lVigente", true),
                    Builders<BsonDocument>.Filter.Eq("idModulo", 1)
                )
            )
            .ToListAsync(cancellationToken);
        var productsById = catalogProducts
            .Where(product => product.Contains("_id"))
            .ToDictionary(product => product["_id"].ToInt32());

        string ProductDisplay(BsonDocument schema)
        {
            var schemaType = schema.GetValue("typeSchema", typeSchema).AsString;
            if (schema.GetValue("global", false).ToBoolean() || schemaType is Baseline or Special)
                return schemaType switch
                {
                    Baseline => "Línea Base (global)",
                    Special => "Especial (global)",
                    _ => "Esquema global",
                };
            var productId = schema.GetValue("productId", 0).ToInt32();
            var names = new List<string>();
            var visited = new HashSet<int>();
            var currentId = productId;
            for (var level = 0; level < 3 && visited.Add(currentId); level++)
            {
                if (!productsById.TryGetValue(currentId, out var product))
                {
                    if (level == 0)
                        names.Add(currentId.ToString());
                    break;
                }
                names.Add(product.GetValue("cProducto", currentId.ToString()).AsString);
                if (!product.TryGetValue("IdProductoPadre", out var parent) || parent.IsBsonNull)
                    break;
                currentId = parent.ToInt32();
            }
            return $"{productId} - {string.Join(" / ", names)}";
        }

        var previewProducts = schemas
            .Select(schema => new MigrationPreviewProduct(
                schema["_id"].AsString,
                schema.GetValue("productId", 0).ToInt32(),
                ProductDisplay(schema)
            ))
            .OrderBy(product => product.ProductId)
            .ToList();
        if (previewProducts.Count == 0 && typeSchema is Baseline or Special)
            previewProducts.Add(
                new MigrationPreviewProduct(
                    pricingIds[0],
                    0,
                    typeSchema == Baseline ? "Línea Base (global)" : "Especial (global)"
                )
            );

        return new MigrationPreview(
            previewProducts,
            amountRanges.Select(ToPreviewRange).ToList(),
            termRanges.Select(ToPreviewRange).ToList(),
            groups
                .Select(group => new MigrationPreviewGroup(
                    group["_id"].AsString,
                    group["dimensionKey"].AsString,
                    group.GetValue("code", string.Empty).AsString,
                    group
                        .GetValue("memberIds", new BsonArray())
                        .AsBsonArray.Select(member => member.ToInt32())
                        .Order()
                        .ToList()
                ))
                .ToList(),
            rateTypes
                .Select(rateType => new MigrationPreviewRateType(
                    rateType["_id"].ToInt32(),
                    rateType.GetValue("cTipoTasa", rateType["_id"].ToString()).AsString
                ))
                .ToList(),
            sets.Select(set =>
                {
                    var coordinates = set["coordinates"].AsBsonDocument;
                    var dimensionGroups = coordinates
                        .GetValue("dimensionGroupIds", new BsonDocument())
                        .AsBsonDocument;
                    return new MigrationPreviewRateSet(
                        set["configuredPricingId"].AsString,
                        coordinates.GetValue("amountRangeId", string.Empty).ToString()
                            ?? string.Empty,
                        coordinates.GetValue("termRangeId", string.Empty).ToString()
                            ?? string.Empty,
                        dimensionGroups.GetValue("branch", string.Empty).ToString() ?? string.Empty,
                        dimensionGroups.GetValue("internalRating", string.Empty).ToString()
                            ?? string.Empty,
                        coordinates.GetValue("insurance", false).ToBoolean(),
                        coordinates.GetValue("currencyId", 0).ToInt32(),
                        coordinates.GetValue("personTypeId", 0).ToInt32(),
                        set["pricingMode"].AsString,
                        set["rates"]
                            .AsBsonArray.Select(rate => rate.AsBsonDocument)
                            .Select(rate => new MigrationPreviewRate(
                                rate["rateTypeId"].ToInt32(),
                                PreviewDecimal(rate.GetValue("compensatory", BsonNull.Value)),
                                PreviewDecimal(rate.GetValue("compensatoryMax", BsonNull.Value)),
                                PreviewDecimal(rate.GetValue("moratory", BsonNull.Value))
                            ))
                            .ToList(),
                        set.GetValue("migrationExceptions", new BsonArray()).AsBsonArray.Count
                    );
                })
                .ToList(),
            typeSchema
        );
    }

    public async Task<IReadOnlyList<MigrationPreviewRun>> GetPreviewRunsAsync(
        CancellationToken cancellationToken
    )
    {
        var runs = await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .Find(Builders<BsonDocument>.Filter.Gt("rateSets", 0))
            .Sort(Builders<BsonDocument>.Sort.Descending("completedAt").Descending("startedAt"))
            .ToListAsync(cancellationToken);
        return runs.Select(run => new MigrationPreviewRun(
                run["_id"].AsString,
                run.GetValue("status", "UNKNOWN").AsString,
                run.TryGetValue("completedAt", out var completedAt) && !completedAt.IsBsonNull
                    ? completedAt.ToUniversalTime()
                    : null,
                run.GetValue("rateSets", 0).ToInt64(),
                run.GetValue("typeSchema", Ordinary).AsString
            ))
            .ToList();
    }

    public async Task<IReadOnlyList<MigrationPreviewRun>> GetRunsAsync(
        CancellationToken cancellationToken
    )
    {
        var runs = await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("updatedAt").Descending("startedAt"))
            .ToListAsync(cancellationToken);
        return runs.Select(run => new MigrationPreviewRun(
                run["_id"].AsString,
                run.GetValue("status", "UNKNOWN").AsString,
                run.TryGetValue("completedAt", out var completedAt) && !completedAt.IsBsonNull
                    ? completedAt.ToUniversalTime()
                    : null,
                run.GetValue("rateSets", 0).ToInt64(),
                run.GetValue("typeSchema", Ordinary).AsString
            ))
            .ToList();
    }

    public async Task TransferAndPublishAsync(
        string runId,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var targetDatabase = TargetDatabase();
        var runCollection = _migrationDatabase.GetCollection<BsonDocument>(Runs);
        var run =
            await runCollection
                .Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No existe la ejecucion de migracion.");
        if (run.GetValue("modelVersion", 1).ToInt32() != ModelVersion)
            throw new InvalidOperationException(
                "El Run ID usa el modelo anterior. Ejecute o reanude el ETL antes de transferirlo."
            );
        var productIds = run["productIds"].AsBsonArray.Select(value => value.ToInt32()).ToArray();
        var typeSchema = run.GetValue("typeSchema", Ordinary).AsString;
        if (typeSchema != _options.TypeSchema)
            throw new InvalidOperationException(
                "El tipo de esquema de las opciones no coincide con el Run seleccionado."
            );
        if (
            typeSchema is Baseline or Special
            && run.GetValue("status", string.Empty).AsString
                is not ("LOADED" or "VALIDATED" or "PUBLISHED")
        )
            throw new InvalidOperationException(
                "El esquema global debe terminar la carga completa antes de transferirse."
            );
        if (
            productIds.Any(productId =>
                ProductState(run, productId).GetValue("status", "PENDING").AsString != "LOADED"
            )
        )
            throw new InvalidOperationException(
                "Todos los productos del Run deben terminar antes de transferir."
            );
        var versionIds = new List<string>();
        foreach (var productId in productIds)
        {
            if (typeSchema == Ordinary)
            {
                await BackfillCoordinatesAsync(runId, productId, cancellationToken);
                await InferDimensionGroupsAsync(runId, productId, cancellationToken);
                versionIds.AddRange(
                    await BuildConfiguredPricingAsync(
                        runId,
                        productId,
                        PricingId(productId, typeSchema),
                        progress,
                        cancellationToken
                    )
                );
            }
            else if (typeSchema == Special)
                await InferDimensionGroupsAsync(runId, productId, cancellationToken);
        }
        if (typeSchema is Baseline or Special)
            versionIds.AddRange(
                await BuildConfiguredPricingAsync(
                    runId,
                    null,
                    PricingId(0, typeSchema),
                    progress,
                    cancellationToken
                )
            );
        versionIds = versionIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.Set("configuredPricingVersionIds", new BsonArray(versionIds))
                .Set(
                    "rateSets",
                    await _migrationDatabase
                        .GetCollection<BsonDocument>(RateSets)
                        .CountDocumentsAsync(
                            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                            cancellationToken: cancellationToken
                        )
                ),
            cancellationToken: cancellationToken
        );
        var validation = await ValidateAsync(runId, cancellationToken);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Message);
        if (versionIds.Count == 0)
            throw new InvalidOperationException("La ejecucion no tiene versiones para transferir.");

        var versionFilter = Builders<BsonDocument>.Filter.In("_id", versionIds);
        var versions = await _migrationDatabase
            .GetCollection<BsonDocument>(Versions)
            .Find(versionFilter)
            .ToListAsync(cancellationToken);
        if (versions.Count != versionIds.Count)
            throw new InvalidOperationException("Faltan versiones de migracion en staging.");

        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.Set("status", "TRANSFERRING")
                .Set("transferStartedAt", DateTime.UtcNow),
            cancellationToken: cancellationToken
        );
        await EnsureTargetIndexesAsync(targetDatabase, cancellationToken);
        await EnsureTargetSchemasAsync(targetDatabase, versions, cancellationToken);

        var versionIdFilter = Builders<BsonDocument>.Filter.In(
            "configuredPricingVersionId",
            versionIds
        );
        var runRateSetFilter = Builders<BsonDocument>.Filter.Eq("migrationRunId", runId);
        var rangeIds = (
            await _migrationDatabase
                .GetCollection<BsonDocument>(RateSets)
                .Distinct<string>("coordinates.amountRangeId", runRateSetFilter)
                .ToListAsync(cancellationToken)
        )
            .Concat(
                await _migrationDatabase
                    .GetCollection<BsonDocument>(RateSets)
                    .Distinct<string>("coordinates.termRangeId", runRateSetFilter)
                    .ToListAsync(cancellationToken)
            )
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await CopyCollectionAsync(
            targetDatabase,
            Versions,
            versionFilter,
            runId,
            cancellationToken
        );
        if (rangeIds.Length > 0)
        {
            var rangesFilter = Builders<BsonDocument>.Filter.In("_id", rangeIds);
            await CopyCollectionAsync(
                targetDatabase,
                AmountRanges,
                rangesFilter,
                runId,
                cancellationToken
            );
            await CopyCollectionAsync(
                targetDatabase,
                TermRanges,
                rangesFilter,
                runId,
                cancellationToken
            );
        }
        var rateSetsCollection = _migrationDatabase.GetCollection<BsonDocument>(RateSets);
        var referencedGroupIds = (
            await rateSetsCollection
                .Distinct<string>("coordinates.dimensionGroupIds.branch", runRateSetFilter)
                .ToListAsync(cancellationToken)
        )
            .Concat(
                await rateSetsCollection
                    .Distinct<string>(
                        "coordinates.dimensionGroupIds.internalRating",
                        runRateSetFilter
                    )
                    .ToListAsync(cancellationToken)
            )
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (referencedGroupIds.Length > 0)
            await CopyCollectionAsync(
                targetDatabase,
                DimensionGroups,
                Builders<BsonDocument>.Filter.In("_id", referencedGroupIds),
                runId,
                cancellationToken
            );
        await targetDatabase
            .GetCollection<BsonDocument>(LegacyQuadrants)
            .DeleteManyAsync(versionIdFilter, cancellationToken);
        await CopyCollectionAsync(
            targetDatabase,
            RateSets,
            runRateSetFilter,
            runId,
            cancellationToken
        );

        var published = 0;
        foreach (var versionId in versionIds)
        {
            var version = versions.Single(x => x["_id"].AsString == versionId);
            var schemaId = version["configuredPricingId"].AsString;
            await targetDatabase
                .GetCollection<BsonDocument>(Versions)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", versionId),
                    Builders<BsonDocument>
                        .Update.Set("status", "PUBLISHED")
                        .Set("publishedAt", DateTime.UtcNow)
                        .Set("active", true),
                    cancellationToken: cancellationToken
                );
            await targetDatabase
                .GetCollection<BsonDocument>(Schemas)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", schemaId),
                    Builders<BsonDocument>
                        .Update.Set("activeVersionId", versionId)
                        .Set("active", true)
                        .Set("updatedAt", DateTime.UtcNow),
                    cancellationToken: cancellationToken
                );
            published++;
            progress?.Report(
                new MigrationProgress(
                    "TRANSFER",
                    published,
                    validation.SourceRows,
                    0,
                    $"Transferida y publicada version {versionId}"
                )
            );
        }

        await targetDatabase
            .GetCollection<BsonDocument>(Audit)
            .ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", $"configured-pricing-migration:{runId}"),
                new BsonDocument
                {
                    { "_id", $"configured-pricing-migration:{runId}" },
                    { "eventType", "INITIAL_MIGRATION_PUBLISHED" },
                    { "migrationRunId", runId },
                    { "versionIds", new BsonArray(versionIds) },
                    { "occurredAt", DateTime.UtcNow },
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken
            );
        await runCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.Set("status", "PUBLISHED")
                .Set("publishedAt", DateTime.UtcNow)
                .Set("targetMongoDatabase", _options.TargetMongoDatabase)
                .Set("publishedVersionIds", new BsonArray(versionIds)),
            cancellationToken: cancellationToken
        );
    }

    private IMongoDatabase TargetDatabase()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TargetMongoConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TargetMongoDatabase);
        return new MongoClient(_options.TargetMongoConnectionString).GetDatabase(
            _options.TargetMongoDatabase
        );
    }

    private async Task EnsureTargetSchemasAsync(
        IMongoDatabase targetDatabase,
        IEnumerable<BsonDocument> versions,
        CancellationToken cancellationToken
    )
    {
        foreach (var version in versions)
        {
            var schemaId = version["configuredPricingId"].AsString;
            var schema =
                await _migrationDatabase
                    .GetCollection<BsonDocument>(Schemas)
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", schemaId))
                    .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No existe el esquema {schemaId} en staging."
                );
            var update = Builders<BsonDocument>
                .Update.SetOnInsert("_id", schemaId)
                .SetOnInsert("typeSchema", schema.GetValue("typeSchema", Ordinary))
                .SetOnInsert("pricingModes", schema.GetValue("pricingModes", new BsonArray()))
                .SetOnInsert("active", false)
                .SetOnInsert("createdAt", DateTime.UtcNow);
            if (schema.TryGetValue("productId", out var productId))
                update = Builders<BsonDocument>.Update.Combine(
                    update,
                    Builders<BsonDocument>.Update.SetOnInsert("productId", productId)
                );
            if (schema.GetValue("global", false).ToBoolean())
                update = Builders<BsonDocument>.Update.Combine(
                    update,
                    Builders<BsonDocument>.Update.SetOnInsert("global", true)
                );
            await targetDatabase
                .GetCollection<BsonDocument>(Schemas)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", schemaId),
                    update,
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        }
    }

    private async Task CopyCollectionAsync(
        IMongoDatabase targetDatabase,
        string collectionName,
        FilterDefinition<BsonDocument> filter,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var source = _migrationDatabase.GetCollection<BsonDocument>(collectionName);
        var target = targetDatabase.GetCollection<BsonDocument>(collectionName);
        using var cursor = await source.Find(filter).ToCursorAsync(cancellationToken);
        var writes = new List<WriteModel<BsonDocument>>(1_000);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var sourceDocument in cursor.Current)
            {
                var document = sourceDocument.DeepClone().AsBsonDocument;
                document.Remove("migrationExceptions");
                document.Remove("migrationRunId");
                document["migration"] = new BsonDocument
                {
                    { "runId", runId },
                    { "transferredAt", DateTime.UtcNow },
                };
                if (collectionName == Versions)
                    document["status"] = "DRAFT";
                writes.Add(
                    new ReplaceOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                        document
                    )
                    {
                        IsUpsert = true,
                    }
                );
                if (writes.Count == 1_000)
                {
                    await target.BulkWriteAsync(
                        writes,
                        new BulkWriteOptions { IsOrdered = false },
                        cancellationToken
                    );
                    writes.Clear();
                }
            }
        }
        if (writes.Count > 0)
            await target.BulkWriteAsync(
                writes,
                new BulkWriteOptions { IsOrdered = false },
                cancellationToken
            );
    }

    private async Task<List<BsonDocument>> ReadBatchAsync(
        string runId,
        int? productId,
        long lastRateId,
        int batchSize,
        string typeSchema,
        CancellationToken cancellationToken
    )
    {
        var scopeJoins = typeSchema switch
        {
            Ordinary => "INNER JOIN dbo.SI_FinMonto m ON m.idMonto = t.idMonto AND m.lVigente = 1 "
                + "AND m.idModulo = 1 INNER JOIN dbo.SI_FinPlazo p ON p.idPlazo = t.idPlazo "
                + "AND p.lVigente = 1 AND p.idModulo = 1 INNER JOIN dbo.SI_FinClasifInterna ci "
                + "ON ci.idClasifInterna = t.idClasificacionInterna AND ci.lVigente = 1 "
                + "INNER JOIN dbo.SI_FinMoneda mo ON mo.idMoneda = t.idMoneda AND mo.lVigente = 1 "
                + "INNER JOIN dbo.SI_FinTipoPersona tp ON tp.idTipoPersona = t.idTipoPersona "
                + "AND tp.lVigente = 1 INNER JOIN dbo.SI_FinEstablecimiento b ON "
                + "b.idEstablecimiento = t.idEstablecimiento AND b.lVigente = 1",
            Baseline => string.Empty,
            Special => "INNER JOIN dbo.SI_FinMonto m ON m.idMonto = t.idMonto AND m.lVigente = 1 "
                + "AND m.idModulo = 1 INNER JOIN dbo.SI_FinEstablecimiento b ON "
                + "b.idEstablecimiento = t.idEstablecimiento AND b.lVigente = 1",
            _ => throw new InvalidOperationException("Tipo de esquema no reconocido."),
        };
        var rangeProjection = typeSchema switch
        {
            Ordinary =>
                "m.nMontoMinimo, m.nMontoMaximo, m.cMonto, p.nMinPlazo, p.nMaxPlazo, p.cPlazo",
            Baseline => "CAST(NULL AS decimal(14,2)) AS nMontoMinimo, "
                + "CAST(NULL AS decimal(14,2)) AS nMontoMaximo, "
                + "CAST(NULL AS varchar(100)) AS cMonto, CAST(NULL AS int) AS nMinPlazo, "
                + "CAST(NULL AS int) AS nMaxPlazo, CAST(NULL AS varchar(50)) AS cPlazo",
            Special => "m.nMontoMinimo, m.nMontoMaximo, m.cMonto, "
                + "CAST(NULL AS int) AS nMinPlazo, CAST(NULL AS int) AS nMaxPlazo, "
                + "CAST(NULL AS varchar(50)) AS cPlazo",
            _ => throw new InvalidOperationException("Tipo de esquema no reconocido."),
        };
        var scopeFilter = typeSchema switch
        {
            Ordinary => "t.idTipoTasa NOT IN (35, 60, 62, 64) AND t.idTipoPersona = 1 "
                + "AND t.lSeguroDesgravamen IS NOT NULL AND t.idMoneda = 1",
            Baseline => "t.idTipoTasa IN (35, 60, 62)",
            Special => "t.idTipoTasa = 64 AND t.idEstablecimiento > 0",
            _ => throw new InvalidOperationException("Tipo de esquema no reconocido."),
        };
        var productJoin =
            typeSchema == Ordinary
                ? "INNER JOIN dbo.SI_FinProducto pr ON pr.IdProducto = t.idProducto "
                    + "AND pr.lVigente = 1 AND pr.idModulo = 1"
                : string.Empty;
        var productFilter = productId is null ? string.Empty : "AND t.idProducto = @productId";
        var sql = $"""
SELECT TOP (@batchSize)
       t.idTasa, t.idProducto, t.idMonto, t.idPlazo, t.idClasificacionInterna,
       t.idTipoTasa, t.idEstablecimiento, t.idMoneda, t.idTipoPersona,
       t.lSeguroDesgravamen, t.lNegociable, t.lVigente,
        t.nTasaCompensatoria, t.nTasaCompensatoriaMax, t.nTasaMoratoria, tt.cTipoTasa AS rateTypeCode,
        {rangeProjection}
 FROM dbo.SI_FinTasa t
 {productJoin}
INNER JOIN dbo.SI_FinTipoTasa tt ON tt.idTipoTasa = t.idTipoTasa AND tt.lVigente = 1 AND tt.idModulo = 1
{scopeJoins}
WHERE t.lVigente = 1
  AND {scopeFilter}
    {productFilter}
   AND t.idTasa > @lastRateId
ORDER BY t.idTasa;
""";
        var result = new List<BsonDocument>();
        await using var connection = new SqlConnection(_options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@batchSize", batchSize);
        if (productId is not null)
            command.Parameters.AddWithValue("@productId", productId.Value);
        command.Parameters.AddWithValue("@lastRateId", lastRateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ToMigrationRow(reader, runId, typeSchema));
        return result;
    }

    private static BsonDocument ToMigrationRow(
        SqlDataReader reader,
        string runId,
        string typeSchema
    )
    {
        var id = reader.GetInt32(reader.GetOrdinal("idTasa"));
        var productId = NullableInt(reader, "idProducto") ?? 0;
        var amountId = NullableInt(reader, "idMonto") ?? 0;
        var termId = NullableInt(reader, "idPlazo") ?? 0;
        var ratingId = NullableInt(reader, "idClasificacionInterna") ?? 0;
        var rateTypeId = NullableInt(reader, "idTipoTasa") ?? 0;
        var branchId = NullableInt(reader, "idEstablecimiento");
        var negotiable = NullableBool(reader, "lNegociable");
        var rateTypeCode = StringValue(reader, "rateTypeCode");
        var isNegotiableRateType = rateTypeCode.StartsWith(
            "NEG",
            StringComparison.OrdinalIgnoreCase
        );
        if (typeSchema == Ordinary && isNegotiableRateType != negotiable)
        {
            throw new InvalidOperationException(
                $"La tasa {id} tiene lNegociable={negotiable}, incompatible con el tipo {rateTypeCode}."
            );
        }

        var insurance = typeSchema == Ordinary && NullableBool(reader, "lSeguroDesgravamen");
        var currencyId = typeSchema == Ordinary ? NullableInt(reader, "idMoneda") ?? 0 : 0;
        var personTypeId = typeSchema == Ordinary ? NullableInt(reader, "idTipoPersona") ?? 0 : 0;
        var mode = typeSchema is Ordinary or Baseline && !negotiable ? "STANDARD" : "NEGOTIABLE";
        var configuredPricingId = PricingId(productId, typeSchema);
        var versionId = VersionId(productId, typeSchema, runId);
        var amountMin = DecimalValue(reader, "nMontoMinimo");
        var amountMax = DecimalValue(reader, "nMontoMaximo");
        var termMin = new BsonInt32(NullableInt(reader, "nMinPlazo") ?? 0);
        var termMax = new BsonInt32(NullableInt(reader, "nMaxPlazo") ?? 0);
        var amountRangeId = typeSchema is Ordinary or Special
            ? AmountRangeId(amountMin, amountMax)
            : null;
        var termRangeId = typeSchema == Ordinary ? TermRangeId(termMin, termMax) : null;
        var dimensionGroupIds = new BsonDocument();

        if (typeSchema == Ordinary)
        {
            dimensionGroupIds["internalRating"] =
                $"configured-pricing-dimension-group:{versionId}:internalRating:{ratingId}";
            dimensionGroupIds["branch"] = branchId is null
                ? $"configured-pricing-dimension-group:{versionId}:branch:default"
                : $"configured-pricing-dimension-group:{versionId}:branch:{branchId.Value}";
        }
        else if (typeSchema == Special && branchId is not null)
        {
            dimensionGroupIds["branch"] =
                $"configured-pricing-dimension-group:{versionId}:branch:{branchId.Value}";
        }

        var key = typeSchema switch
        {
            Ordinary =>
                $"amount={amountRangeId}|term={termRangeId}|dimensionGroups={dimensionGroupIds}|"
                    + $"insurance={insurance.ToString().ToLowerInvariant()}|currency={currencyId}|"
                    + $"personType={personTypeId}|pricingMode={mode}",
            Baseline => $"schema=BASELINE|rateType={rateTypeId}|pricingMode={mode}",
            _ => $"amount={amountRangeId}|branch={branchId}|pricingMode=NEGOTIABLE",
        };

        var branchInferenceKey =
            typeSchema == Ordinary
                ? $"version={versionId}|amount={amountRangeId}|term={termRangeId}|"
                    + $"internalRating={dimensionGroupIds.GetValue("internalRating", string.Empty)}|"
                    + $"insurance={insurance.ToString().ToLowerInvariant()}|currency={currencyId}|"
                    + $"personType={personTypeId}|pricingMode={mode}"
                : string.Empty;

        var amount = new BsonDocument
        {
            { "legacyAmountId", amountId },
            { "min", amountMin },
            { "max", amountMax },
            { "label", StringValue(reader, "cMonto") },
        };
        var term = new BsonDocument
        {
            { "legacyTermId", termId },
            { "min", termMin },
            { "max", termMax },
            { "label", StringValue(reader, "cPlazo") },
        };
        var rate = new BsonDocument
        {
            { "compensatory", DecimalValue(reader, "nTasaCompensatoria") },
            { "compensatoryMax", DecimalValue(reader, "nTasaCompensatoriaMax") },
            { "moratory", DecimalValue(reader, "nTasaMoratoria") },
        };

        return new BsonDocument
        {
            { "_id", $"migration-row:{runId}:{id}" },
            { "legacyRateId", id },
            { "migrationRunId", runId },
            { "configuredPricingId", configuredPricingId },
            { "configuredPricingVersionId", versionId },
            { "typeSchema", typeSchema },
            { "amountRangeId", amountRangeId is null ? (BsonValue)BsonNull.Value : amountRangeId },
            { "termRangeId", termRangeId is null ? (BsonValue)BsonNull.Value : termRangeId },
            { "dimensionGroupIds", dimensionGroupIds },
            { "branchId", branchId is null ? BsonNull.Value : branchId.Value },
            { "rateTypeId", rateTypeId },
            { "pricingMode", mode },
            { "insurance", insurance },
            { "currencyId", currencyId },
            { "personTypeId", personTypeId },
            { "applicabilityKey", key },
            { "branchInferenceKey", branchInferenceKey },
            { "productId", productId },
            { "amount", amount },
            { "term", term },
            { "internalRatingLegacyId", ratingId },
            { "active", NullableBool(reader, "lVigente") },
            { "rate", rate },
        };
    }

    private async Task BackfillCoordinatesAsync(
        string runId,
        int productId,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            Builders<BsonDocument>.Filter.Eq("productId", productId),
            Builders<BsonDocument>.Filter.Exists("insurance", false)
        );
        using var cursor = await rows.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("_id").Include("applicabilityKey"))
            .ToCursorAsync(cancellationToken);
        var writes = new List<WriteModel<BsonDocument>>(1_000);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var row in cursor.Current)
            {
                var coordinates = CoordinatesFromApplicabilityKey(row["applicabilityKey"].AsString);
                writes.Add(
                    new UpdateOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", row["_id"]),
                        Builders<BsonDocument>
                            .Update.Set("insurance", coordinates["insurance"])
                            .Set("currencyId", coordinates["currencyId"])
                            .Set("personTypeId", coordinates["personTypeId"])
                    )
                );
                if (writes.Count == 1_000)
                {
                    await rows.BulkWriteAsync(
                        writes,
                        new BulkWriteOptions { IsOrdered = false },
                        cancellationToken
                    );
                    writes.Clear();
                }
            }
        }
        if (writes.Count > 0)
            await rows.BulkWriteAsync(
                writes,
                new BulkWriteOptions { IsOrdered = false },
                cancellationToken
            );
    }

    private static BsonDocument CoordinatesFromApplicabilityKey(string key)
    {
        var values = key.Split('|')
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (
            !bool.TryParse(values.GetValueOrDefault("insurance"), out var insurance)
            || !int.TryParse(values.GetValueOrDefault("currency"), out var currencyId)
            || !int.TryParse(values.GetValueOrDefault("personType"), out var personTypeId)
        )
            throw new InvalidOperationException(
                $"La clave de aplicabilidad no contiene dimensiones validas: {key}"
            );

        return new BsonDocument
        {
            { "insurance", insurance },
            { "currencyId", currencyId },
            { "personTypeId", personTypeId },
        };
    }

    private async Task InferDimensionGroupsAsync(
        string runId,
        int productId,
        CancellationToken cancellationToken
    )
    {
        var activeBranchIds = await LoadActiveBranchIdsAsync(cancellationToken);
        var runFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            Builders<BsonDocument>.Filter.Eq("productId", productId)
        );
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var versionIds = await rows.Distinct<string>("configuredPricingVersionId", runFilter)
            .ToListAsync(cancellationToken);
        if (versionIds.Count == 0)
            return;
        await rows.UpdateManyAsync(
            runFilter,
            Builders<BsonDocument>
                .Update.Set("includedInRateSet", true)
                .Unset("consolidatedLegacyRateIds"),
            cancellationToken: cancellationToken
        );
        var includedFilter = Builders<BsonDocument>.Filter.And(
            runFilter,
            Builders<BsonDocument>.Filter.Eq("includedInRateSet", true)
        );
        if (_options.TypeSchema == Special)
        {
            var branchIds = await rows.Distinct<int>("branchId", includedFilter)
                .ToListAsync(cancellationToken);
            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var branchId in branchIds)
            {
                if (!activeBranchIds.Contains(branchId))
                    continue;
                var groupId = await EnsureInferredDimensionGroupAsync(
                    "branch",
                    [branchId],
                    false,
                    cancellationToken
                );
                foreach (var versionId in versionIds)
                    assignments[$"{versionId}:{branchId}"] = groupId;
            }
            await AssignGlobalDimensionGroupsAsync(
                includedFilter,
                "branch",
                "branchId",
                assignments,
                cancellationToken
            );
            await ConsolidateGlobalDimensionRowsAsync(
                includedFilter,
                "branchId",
                cancellationToken
            );
            return;
        }

        var activeInternalRatingIds = await LoadActiveInternalRatingIdsAsync(cancellationToken);
        await InferGlobalDimensionGroupsAsync(
            runFilter,
            "branch",
            activeBranchIds,
            "branchId",
            cancellationToken
        );
        await InferGlobalDimensionGroupsAsync(
            includedFilter,
            "internalRating",
            activeInternalRatingIds,
            "internalRatingLegacyId",
            cancellationToken
        );
    }

    // A member is assigned once per version, using its complete tariff rather than a single cell.
    private async Task InferGlobalDimensionGroupsAsync(
        FilterDefinition<BsonDocument> filter,
        string dimensionKey,
        IReadOnlyList<int> activeMemberIds,
        string memberField,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var members = new List<DimensionMember>();
        using var cursor = await rows.Find(filter)
            .Sort(
                Builders<BsonDocument>.Sort.Combine(
                    Builders<BsonDocument>.Sort.Ascending("configuredPricingVersionId"),
                    Builders<BsonDocument>.Sort.Ascending(memberField),
                    Builders<BsonDocument>.Sort.Ascending("applicabilityKey"),
                    Builders<BsonDocument>.Sort.Ascending("rateTypeId"),
                    Builders<BsonDocument>.Sort.Ascending("legacyRateId")
                )
            )
            .ToCursorAsync(cancellationToken);
        var memberRows = new List<BsonDocument>();
        string? versionId = null;
        int? memberId = null;
        void CompleteMember()
        {
            if (versionId is not null && memberId is not null && memberRows.Count > 0)
                members.Add(
                    new DimensionMember(
                        versionId,
                        memberId.Value,
                        GlobalRateSignature(memberRows, dimensionKey)
                    )
                );
            memberRows.Clear();
        }
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var rowVersionId = row["configuredPricingVersionId"].AsString;
                var rowMemberId = row[memberField].ToInt32();
                if (versionId is not null && (versionId != rowVersionId || memberId != rowMemberId))
                    CompleteMember();
                versionId = rowVersionId;
                memberId = rowMemberId;
                memberRows.Add(row);
            }
        CompleteMember();

        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            var versionMembers in members.GroupBy(
                member => member.VersionId,
                StringComparer.Ordinal
            )
        )
        foreach (
            var equivalentMembers in versionMembers.GroupBy(
                member => member.Signature ?? $"invalid:{member.MemberId}",
                StringComparer.Ordinal
            )
        )
        {
            var memberIds = equivalentMembers.Select(member => member.MemberId).Order().ToArray();
            var isDefault = memberIds.SequenceEqual(activeMemberIds);
            var groupId = await EnsureInferredDimensionGroupAsync(
                dimensionKey,
                memberIds,
                isDefault,
                cancellationToken
            );
            foreach (var id in memberIds)
                assignments[$"{versionMembers.Key}:{id}"] = groupId;
        }

        await AssignGlobalDimensionGroupsAsync(
            filter,
            dimensionKey,
            memberField,
            assignments,
            cancellationToken
        );
        await ConsolidateGlobalDimensionRowsAsync(filter, memberField, cancellationToken);
    }

    private static string? GlobalRateSignature(IEnumerable<BsonDocument> rows, string dimensionKey)
    {
        var entries = rows.GroupBy(row => RateContextKey(row, dimensionKey), StringComparer.Ordinal)
            .OrderBy(group => group.Key)
            .ToList();
        if (entries.Any(group => group.Count() != 1))
            return null;
        return string.Join(
            "|",
            entries.Select(group =>
            {
                var rate = group.Single()["rate"].AsBsonDocument;
                return $"{group.Key}:{rate["compensatory"]}:{rate["compensatoryMax"]}:{rate["moratory"]}";
            })
        );
    }

    private static string RateContextKey(BsonDocument row, string dimensionKey)
    {
        var groups = row["dimensionGroupIds"].AsBsonDocument.DeepClone().AsBsonDocument;
        groups.Remove(dimensionKey);
        return $"amount={row["amountRangeId"]}|term={row["termRangeId"]}|"
            + $"dimensionGroups={groups}|insurance={BooleanText(row["insurance"])}|"
            + $"currency={row["currencyId"]}|personType={row["personTypeId"]}|"
            + $"pricingMode={row["pricingMode"]}|rateType={row["rateTypeId"]}";
    }

    private async Task AssignGlobalDimensionGroupsAsync(
        FilterDefinition<BsonDocument> filter,
        string dimensionKey,
        string memberField,
        IReadOnlyDictionary<string, string> assignments,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        using var cursor = await rows.Find(filter).ToCursorAsync(cancellationToken);
        var writes = new List<WriteModel<BsonDocument>>(1_000);
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var assignmentKey =
                    $"{row["configuredPricingVersionId"].AsString}:{row[memberField].ToInt32()}";
                if (!assignments.TryGetValue(assignmentKey, out var groupId))
                    throw new InvalidOperationException(
                        $"No se encontro grupo {dimensionKey} para {assignmentKey}."
                    );
                writes.Add(
                    new UpdateOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", row["_id"]),
                        Builders<BsonDocument>
                            .Update.Set($"dimensionGroupIds.{dimensionKey}", groupId)
                            .Set("applicabilityKey", ApplicabilityKey(row, dimensionKey, groupId))
                    )
                );
                if (writes.Count == 1_000)
                    await FlushInferenceWritesAsync(rows, writes, cancellationToken);
            }
        await FlushInferenceWritesAsync(rows, writes, cancellationToken);
    }

    private async Task ConsolidateGlobalDimensionRowsAsync(
        FilterDefinition<BsonDocument> filter,
        string memberField,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        using var cursor = await rows.Find(filter)
            .Sort(
                Builders<BsonDocument>.Sort.Combine(
                    Builders<BsonDocument>.Sort.Ascending("applicabilityKey"),
                    Builders<BsonDocument>.Sort.Ascending("rateTypeId"),
                    Builders<BsonDocument>.Sort.Ascending(memberField),
                    Builders<BsonDocument>.Sort.Ascending("legacyRateId")
                )
            )
            .ToCursorAsync(cancellationToken);
        var currentRows = new List<BsonDocument>();
        string? currentKey = null;
        var writes = new List<WriteModel<BsonDocument>>(1_000);
        async Task CompleteRateAsync()
        {
            if (currentRows.Count == 0)
                return;
            if (
                currentRows.Select(row => row[memberField].ToInt32()).Distinct().Count()
                == currentRows.Count
            )
            {
                var canonical = currentRows[0];
                var legacyIds = currentRows
                    .SelectMany(LegacyRateIds)
                    .Distinct()
                    .Order()
                    .Select(id => (BsonValue)id)
                    .ToArray();
                foreach (var row in currentRows)
                    writes.Add(
                        new UpdateOneModel<BsonDocument>(
                            Builders<BsonDocument>.Filter.Eq("_id", row["_id"]),
                            Builders<BsonDocument>
                                .Update.Set(
                                    "includedInRateSet",
                                    row["_id"].Equals(canonical["_id"])
                                )
                                .Set("consolidatedLegacyRateIds", new BsonArray(legacyIds))
                        )
                    );
            }
            currentRows.Clear();
            if (writes.Count >= 1_000)
                await FlushInferenceWritesAsync(rows, writes, cancellationToken);
        }
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var key = $"{row["applicabilityKey"]}:{row["rateTypeId"]}";
                if (currentKey is not null && currentKey != key)
                    await CompleteRateAsync();
                currentKey = key;
                currentRows.Add(row);
            }
        await CompleteRateAsync();
        await FlushInferenceWritesAsync(rows, writes, cancellationToken);
    }

    private sealed record DimensionMember(string VersionId, int MemberId, string? Signature);

    private async Task<string> EnsureInferredDimensionGroupAsync(
        string dimensionKey,
        IReadOnlyList<int> memberIds,
        bool isDefault,
        CancellationToken cancellationToken
    )
    {
        var memberKey = string.Join(",", memberIds);
        var hash = Hash(memberKey);
        var groupId = DimensionGroupId(dimensionKey, memberIds);
        var code = isDefault ? "DEFAULT" : "AUTO-" + hash[..12];
        var displayLabel = memberKey;
        await _migrationDatabase
            .GetCollection<BsonDocument>(DimensionGroups)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", groupId),
                Builders<BsonDocument>
                    .Update.SetOnInsert("_id", groupId)
                    .SetOnInsert("dimensionKey", dimensionKey)
                    .SetOnInsert("code", code)
                    .SetOnInsert("displayLabel", displayLabel)
                    .SetOnInsert("memberIds", new BsonArray(memberIds.Select(id => (BsonValue)id)))
                    .SetOnInsert("active", true),
                new UpdateOptions { IsUpsert = true },
                cancellationToken
            );
        return groupId;
    }

    private static async Task FlushInferenceWritesAsync(
        IMongoCollection<BsonDocument> rows,
        List<WriteModel<BsonDocument>> writes,
        CancellationToken cancellationToken
    )
    {
        if (writes.Count == 0)
            return;
        await rows.BulkWriteAsync(
            writes,
            new BulkWriteOptions { IsOrdered = false },
            cancellationToken
        );
        writes.Clear();
    }

    private static IEnumerable<long> LegacyRateIds(BsonDocument row) =>
        row.GetValue("consolidatedLegacyRateIds", new BsonArray { row["legacyRateId"] })
            .AsBsonArray.Select(value => value.ToInt64());

    private static string ApplicabilityKey(BsonDocument row, string dimensionKey, string groupId)
    {
        var dimensionGroupIds = row["dimensionGroupIds"].AsBsonDocument.DeepClone().AsBsonDocument;
        dimensionGroupIds[dimensionKey] = groupId;
        if (row.GetValue("typeSchema", Ordinary).AsString == Baseline)
            return "schema=BASELINE|pricingMode=NEGOTIABLE";
        if (row.GetValue("typeSchema", Ordinary).AsString == Special)
            return $"amount={row["amountRangeId"]}|branch={groupId}|pricingMode=NEGOTIABLE";
        return $"amount={row["amountRangeId"]}|term={row["termRangeId"]}|"
            + $"dimensionGroups={dimensionGroupIds}|insurance={BooleanText(row["insurance"])}|"
            + $"currency={row["currencyId"]}|personType={row["personTypeId"]}|"
            + $"pricingMode={row["pricingMode"]}";
    }

    private static string BooleanText(BsonValue value) => value.ToBoolean() ? "true" : "false";

    private async Task ResetLegacyRunAsync(
        string runId,
        BsonDocument run,
        long initialRateId,
        CancellationToken cancellationToken
    )
    {
        var versionIds = run.GetValue("configuredPricingVersionIds", new BsonArray())
            .AsBsonArray.Select(x => x.AsString)
            .ToList();
        var runFilter = Builders<BsonDocument>.Filter.Eq("migrationRunId", runId);
        await _migrationDatabase
            .GetCollection<BsonDocument>(Rows)
            .DeleteManyAsync(runFilter, cancellationToken);
        await _migrationDatabase
            .GetCollection<BsonDocument>(RateSets)
            .DeleteManyAsync(runFilter, cancellationToken);
        if (versionIds.Count > 0)
        {
            var versionFilter = Builders<BsonDocument>.Filter.In(
                "configuredPricingVersionId",
                versionIds
            );
            await _migrationDatabase
                .GetCollection<BsonDocument>(LegacyQuadrants)
                .DeleteManyAsync(versionFilter, cancellationToken);
            await _migrationDatabase
                .GetCollection<BsonDocument>(Versions)
                .DeleteManyAsync(
                    Builders<BsonDocument>.Filter.In("_id", versionIds),
                    cancellationToken
                );
        }
        await _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                Builders<BsonDocument>
                    .Update.Set("extracted", 0)
                    .Set("loaded", 0)
                    .Set("rejected", 0)
                    .Set("rateSets", 0)
                    .Unset("productIds")
                    .Unset("productStates")
                    .Unset("globalState")
                    .Set("modelVersion", ModelVersion)
                    .Set("typeSchema", _options.TypeSchema)
                    .Set("status", "PENDING")
                    .Set("resetAt", DateTime.UtcNow)
                    .Unset("validation")
                    .Unset("configuredPricingVersionIds"),
                cancellationToken: cancellationToken
            );
    }

    private async Task<List<string>> BuildConfiguredPricingAsync(
        string runId,
        int? productId,
        string configuredPricingId,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var rows = _migrationDatabase.GetCollection<BsonDocument>(Rows);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            Builders<BsonDocument>.Filter.Eq("configuredPricingId", configuredPricingId),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("includedInRateSet", true),
                Builders<BsonDocument>.Filter.Exists("includedInRateSet", false)
            )
        );

        if (productId is not null)
        {
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Eq("productId", productId.Value)
            );
        }

        var versions = new HashSet<string>();
        var initialized = new HashSet<string>(StringComparer.Ordinal);
        var rateSets = _migrationDatabase.GetCollection<BsonDocument>(RateSets);
        var rateSetFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
            Builders<BsonDocument>.Filter.Eq("configuredPricingId", configuredPricingId)
        );
        await rateSets.DeleteManyAsync(rateSetFilter, cancellationToken);

        var sort = Builders<BsonDocument>.Sort.Combine(
            Builders<BsonDocument>.Sort.Ascending("applicabilityKey"),
            Builders<BsonDocument>.Sort.Ascending("legacyRateId")
        );
        using var cursor = await rows.Find(filter).Sort(sort).ToCursorAsync(cancellationToken);
        BsonDocument? current = null;
        var currentKey = string.Empty;
        var currentRates = new Dictionary<int, BsonDocument>();
        var currentDuplicateRateIds = new Dictionary<int, BsonArray>();
        var currentDuplicateCodes = new Dictionary<int, string>();
        long sets = 0;
        long duplicates = 0;
        async Task FlushAsync()
        {
            if (current is null || currentRates.Count == 0)
            {
                return;
            }

            var typeSchema = current.GetValue("typeSchema", Ordinary).AsString;
            var coordinates = typeSchema switch
            {
                Ordinary => new BsonDocument
                {
                    { "amountRangeId", current["amountRangeId"] },
                    { "termRangeId", current["termRangeId"] },
                    { "dimensionGroupIds", current["dimensionGroupIds"] },
                    { "insurance", current["insurance"] },
                    { "currencyId", current["currencyId"] },
                    { "personTypeId", current["personTypeId"] },
                },
                Baseline => new BsonDocument { { "rateTypeId", current["rateTypeId"] } },
                _ => new BsonDocument
                {
                    { "amountRangeId", current["amountRangeId"] },
                    { "dimensionGroupIds", current["dimensionGroupIds"] },
                },
            };
            var sourceProductIds = currentRates
                .Values.SelectMany(rate =>
                    rate.GetValue("sourceProductIds", new BsonArray()).AsBsonArray
                )
                .Select(value => value.ToInt32())
                .Distinct()
                .Order()
                .ToArray();
            var exceptions = new BsonArray(
                currentDuplicateRateIds
                    .OrderBy(entry => entry.Key)
                    .Select(entry => new BsonDocument
                    {
                        {
                            "code",
                            currentDuplicateCodes.GetValueOrDefault(
                                entry.Key,
                                "DUPLICATE_RATE_TYPE"
                            )
                        },
                        { "rateTypeId", entry.Key },
                        { "legacyRateIds", entry.Value },
                        {
                            "message",
                            currentDuplicateCodes.GetValueOrDefault(entry.Key)
                            == "SCHEMA_SCOPE_CONFLICT"
                                ? "Filas de distintos productos colapsan en la misma celda global "
                                    + "con valores de tasa diferentes."
                                : "Mas de una fila SI_FinTasa aplica al mismo tipo de tasa y coordenadas."
                        },
                    })
            );
            var document = new BsonDocument
            {
                {
                    "_id",
                    "configured-pricing-rate-set:"
                        + Hash(current["configuredPricingVersionId"].AsString + "|" + currentKey)
                },
                { "migrationRunId", runId },
                { "configuredPricingVersionId", current["configuredPricingVersionId"] },
                { "configuredPricingId", current["configuredPricingId"] },
                { "typeSchema", typeSchema },
                { "pricingMode", current["pricingMode"] },
                { "applicabilityKey", currentKey },
                { "coordinates", coordinates },
                {
                    "rates",
                    new BsonArray(currentRates.Values.OrderBy(x => x["rateTypeId"].ToInt32()))
                },
                { "sourceProductIds", new BsonArray(sourceProductIds) },
                { "migrationExceptions", exceptions },
                { "active", true },
            };
            await rateSets.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                document,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken
            );
            sets++;
        }

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var row in cursor.Current)
            {
                var key = row["applicabilityKey"].AsString;
                if (current is not null && key != currentKey)
                {
                    await FlushAsync();
                    currentRates.Clear();
                    currentDuplicateRateIds.Clear();
                    currentDuplicateCodes.Clear();
                }
                current ??= row;
                if (key != currentKey)
                    current = row;
                currentKey = key;
                var type = row["rateTypeId"].ToInt32();
                if (currentRates.TryGetValue(type, out var existingRate))
                {
                    if (_options.TypeSchema is Baseline or Special)
                    {
                        var productIds = existingRate
                            .GetValue("sourceProductIds", new BsonArray())
                            .AsBsonArray.Select(value => value.ToInt32())
                            .Append(row["productId"].ToInt32())
                            .Where(id => id > 0)
                            .Distinct()
                            .Order()
                            .ToArray();
                        existingRate["sourceProductIds"] = new BsonArray(productIds);
                        if (RatesEquivalent(existingRate, row["rate"].AsBsonDocument))
                        {
                            existingRate["legacyRateIds"] = new BsonArray(
                                existingRate
                                    .GetValue("legacyRateIds", new BsonArray())
                                    .AsBsonArray.Concat(
                                        row.GetValue(
                                            "consolidatedLegacyRateIds",
                                            new BsonArray { row["legacyRateId"] }
                                        ).AsBsonArray
                                    )
                                    .Distinct()
                                    .OrderBy(value => value.ToInt64())
                            );
                            continue;
                        }
                    }
                    if (!currentDuplicateRateIds.TryGetValue(type, out var legacyRateIds))
                    {
                        legacyRateIds = new BsonArray(
                            existingRate
                                .GetValue(
                                    "legacyRateIds",
                                    new BsonArray { existingRate["legacyRateId"] }
                                )
                                .AsBsonArray
                        );
                        currentDuplicateRateIds[type] = legacyRateIds;
                        if (_options.TypeSchema is Baseline or Special)
                            currentDuplicateCodes[type] = "SCHEMA_SCOPE_CONFLICT";
                    }
                    legacyRateIds.AddRange(
                        row.GetValue(
                            "consolidatedLegacyRateIds",
                            new BsonArray { row["legacyRateId"] }
                        ).AsBsonArray
                    );
                    duplicates++;
                    continue;
                }
                currentRates[type] = new BsonDocument
                {
                    { "rateTypeId", type },
                    { "compensatory", row["rate"].AsBsonDocument["compensatory"] },
                    { "compensatoryMax", row["rate"].AsBsonDocument["compensatoryMax"] },
                    { "moratory", row["rate"].AsBsonDocument["moratory"] },
                    { "legacyRateId", row["legacyRateId"] },
                    {
                        "legacyRateIds",
                        row.GetValue(
                            "consolidatedLegacyRateIds",
                            new BsonArray { row["legacyRateId"] }
                        )
                    },
                    {
                        "sourceProductIds",
                        row["productId"].ToInt32() > 0
                            ? new BsonArray { row["productId"] }
                            : new BsonArray()
                    },
                };
                versions.Add(row["configuredPricingVersionId"].AsString);
                await EnsureConfigurationDocumentsAsync(row, initialized, cancellationToken);
            }
        }
        await FlushAsync();
        progress?.Report(
            new MigrationProgress(
                "AGGREGATE",
                sets,
                0,
                duplicates,
                duplicates == 0
                    ? "Conjuntos de tasas creados."
                    : $"Conjuntos creados con {duplicates} fila(s) duplicada(s) pendiente(s) de resolver."
            )
        );
        return versions.Order().ToList();
    }

    private async Task EnsureConfigurationDocumentsAsync(
        BsonDocument row,
        HashSet<string> initialized,
        CancellationToken cancellationToken
    )
    {
        var versionId = row["configuredPricingVersionId"].AsString;
        var pricingId = row["configuredPricingId"].AsString;
        var typeSchema = row.GetValue("typeSchema", Ordinary).AsString;
        if (initialized.Add($"schema:{pricingId}"))
        {
            var schemaUpdate = Builders<BsonDocument>
                .Update.SetOnInsert("_id", pricingId)
                .SetOnInsert("typeSchema", typeSchema)
                .SetOnInsert("active", false)
                .AddToSet("pricingModes", row["pricingMode"]);
            if (typeSchema == Ordinary)
                schemaUpdate = Builders<BsonDocument>.Update.Combine(
                    schemaUpdate,
                    Builders<BsonDocument>.Update.SetOnInsert("productId", row["productId"])
                );
            else
                schemaUpdate = Builders<BsonDocument>.Update.Combine(
                    schemaUpdate,
                    Builders<BsonDocument>.Update.SetOnInsert("global", true)
                );
            await _migrationDatabase
                .GetCollection<BsonDocument>(Schemas)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", pricingId),
                    schemaUpdate,
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        }
        else
            await _migrationDatabase
                .GetCollection<BsonDocument>(Schemas)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", pricingId),
                    Builders<BsonDocument>.Update.AddToSet("pricingModes", row["pricingMode"]),
                    cancellationToken: cancellationToken
                );
        if (initialized.Add($"version:{versionId}"))
            await _migrationDatabase
                .GetCollection<BsonDocument>(Versions)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", versionId),
                    Builders<BsonDocument>
                        .Update.SetOnInsert("_id", versionId)
                        .SetOnInsert("configuredPricingId", pricingId)
                        .SetOnInsert("typeSchema", typeSchema)
                        .SetOnInsert("status", "DRAFT")
                        .SetOnInsert("revision", 1),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        var amountRangeId = row.GetValue("amountRangeId", BsonNull.Value);
        if (!amountRangeId.IsBsonNull && initialized.Add($"amount:{amountRangeId.AsString}"))
            await _migrationDatabase
                .GetCollection<BsonDocument>(AmountRanges)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", amountRangeId),
                    Builders<BsonDocument>
                        .Update.SetOnInsert("_id", amountRangeId)
                        .SetOnInsert("min", row["amount"].AsBsonDocument["min"])
                        .SetOnInsert("max", row["amount"].AsBsonDocument["max"])
                        .SetOnInsert("displayLabel", row["amount"].AsBsonDocument["label"])
                        .AddToSet(
                            "legacyAmountIds",
                            row["amount"].AsBsonDocument["legacyAmountId"]
                        ),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        var termRangeId = row.GetValue("termRangeId", BsonNull.Value);
        if (!termRangeId.IsBsonNull && initialized.Add($"term:{termRangeId.AsString}"))
            await _migrationDatabase
                .GetCollection<BsonDocument>(TermRanges)
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", termRangeId),
                    Builders<BsonDocument>
                        .Update.SetOnInsert("_id", termRangeId)
                        .SetOnInsert("min", row["term"].AsBsonDocument["min"])
                        .SetOnInsert("max", row["term"].AsBsonDocument["max"])
                        .SetOnInsert("displayLabel", row["term"].AsBsonDocument["label"])
                        .AddToSet("legacyTermIds", row["term"].AsBsonDocument["legacyTermId"]),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        if (typeSchema is Ordinary or Special && row["branchId"].IsBsonNull)
            throw new InvalidOperationException(
                "La carga del esquema requiere idEstablecimiento vigente."
            );
    }

    private async Task<IReadOnlyList<int>> LoadActiveBranchIdsAsync(
        CancellationToken cancellationToken
    )
    {
        return await LoadActiveIdsAsync(
            "SELECT idEstablecimiento FROM dbo.SI_FinEstablecimiento WHERE lVigente = 1 ORDER BY idEstablecimiento;",
            cancellationToken
        );
    }

    private async Task<IReadOnlyList<int>> LoadActiveInternalRatingIdsAsync(
        CancellationToken cancellationToken
    )
    {
        return await LoadActiveIdsAsync(
            "SELECT idClasifInterna FROM dbo.SI_FinClasifInterna WHERE lVigente = 1 ORDER BY idClasifInterna;",
            cancellationToken
        );
    }

    private async Task<IReadOnlyList<int>> LoadActiveIdsAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var ids = new List<int>();
        await using var connection = new SqlConnection(_options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        await _migrationDatabase
            .GetCollection<BsonDocument>(Rows)
            .Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>
                        .IndexKeys.Ascending("migrationRunId")
                        .Ascending("applicabilityKey")
                ),
                cancellationToken: cancellationToken
            );
        await _migrationDatabase
            .GetCollection<BsonDocument>(RateSets)
            .Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>
                        .IndexKeys.Ascending("configuredPricingVersionId")
                        .Ascending("configuredPricingId")
                        .Ascending("applicabilityKey"),
                    new CreateIndexOptions { Unique = true }
                ),
                cancellationToken: cancellationToken
            );
    }

    private static async Task EnsureTargetIndexesAsync(
        IMongoDatabase targetDatabase,
        CancellationToken cancellationToken
    )
    {
        await targetDatabase
            .GetCollection<BsonDocument>(RateSets)
            .Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>
                        .IndexKeys.Ascending("configuredPricingVersionId")
                        .Ascending("configuredPricingId")
                        .Ascending("applicabilityKey"),
                    new CreateIndexOptions { Unique = true }
                ),
                cancellationToken: cancellationToken
            );
    }

    private void EnsureRunProductsMatch(BsonDocument? run, IReadOnlyList<int> productIds)
    {
        if (run is null)
            return;
        if (_productIds.Count == 0)
            return;
        var savedProductIds = run.GetValue("productIds", new BsonArray())
            .AsBsonArray.Select(value => value.ToInt32())
            .Order()
            .ToArray();
        if (savedProductIds.Any(productId => !_productIds.Contains(productId)))
            throw new InvalidOperationException(
                "La lista de productos no coincide con la registrada para este Run ID."
            );
    }

    private async Task<IReadOnlyList<int>> ResolveProductIdsAsync(
        CancellationToken cancellationToken
    )
    {
        var requestedIds = _productIds.Distinct().OrderDescending().ToArray();
        if (requestedIds.Length > 0)
        {
            var sql = $"""
SELECT p.IdProducto
FROM dbo.SI_FinProducto p
WHERE p.lVigente = 1 AND p.idModulo = 1
  AND p.IdProducto IN ({string.Join(",", requestedIds.Select((_, index) => $"@productId{index}"))})
ORDER BY p.IdProducto DESC;
""";
            var resolved = await LoadProductIdsAsync(sql, requestedIds, cancellationToken);
            var resolvedSet = resolved.ToHashSet();
            var invalidIds = requestedIds.Where(id => !resolvedSet.Contains(id)).ToArray();
            if (invalidIds.Length > 0)
                throw new InvalidOperationException(
                    "Solo se permiten productos vigentes del módulo de créditos (idModulo = 1). "
                        + $"IDs rechazados: {string.Join(", ", invalidIds)}."
                );
            return resolved;
        }

        const string sqlForEligibleCreditProducts = """
SELECT p.IdProducto
FROM dbo.SI_FinProducto p
WHERE p.lVigente = 1 AND p.idModulo = 1
  AND EXISTS (
      SELECT 1
      FROM dbo.SI_FinTasa t
      INNER JOIN dbo.SI_FinMonto m ON m.idMonto = t.idMonto AND m.lVigente = 1 AND m.idModulo = 1
      INNER JOIN dbo.SI_FinPlazo z ON z.idPlazo = t.idPlazo AND z.lVigente = 1 AND z.idModulo = 1
      INNER JOIN dbo.SI_FinClasifInterna ci ON ci.idClasifInterna = t.idClasificacionInterna AND ci.lVigente = 1
      INNER JOIN dbo.SI_FinTipoTasa tt ON tt.idTipoTasa = t.idTipoTasa AND tt.lVigente = 1 AND tt.idModulo = 1
      INNER JOIN dbo.SI_FinMoneda mo ON mo.idMoneda = t.idMoneda AND mo.lVigente = 1
      INNER JOIN dbo.SI_FinTipoPersona tp ON tp.idTipoPersona = t.idTipoPersona AND tp.lVigente = 1
      INNER JOIN dbo.SI_FinEstablecimiento b ON b.idEstablecimiento = t.idEstablecimiento AND b.lVigente = 1
      WHERE t.idProducto = p.IdProducto AND t.lVigente = 1
        AND t.idTipoTasa NOT IN (35, 62, 64)
        AND t.idTipoPersona = 1 AND t.idMoneda = 1 AND t.lSeguroDesgravamen IS NOT NULL
  )
ORDER BY p.IdProducto DESC;
""";
        return await LoadProductIdsAsync(
            sqlForEligibleCreditProducts,
            Array.Empty<int>(),
            cancellationToken
        );
    }

    private async Task<IReadOnlyList<int>> LoadProductIdsAsync(
        string sql,
        IReadOnlyList<int> requestedIds,
        CancellationToken cancellationToken
    )
    {
        var ids = new List<int>();
        await using var connection = new SqlConnection(_options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        for (var index = 0; index < requestedIds.Count; index++)
            command.Parameters.AddWithValue($"@productId{index}", requestedIds[index]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static BsonDocument ProductState(BsonDocument run, int productId) =>
        run.GetValue("productStates", new BsonDocument())
            .AsBsonDocument.GetValue(
                productId.ToString(),
                new BsonDocument { { "status", "PENDING" }, { "lastRateId", 0 } }
            )
            .AsBsonDocument;

    private Task UpdateProductStateAsync(
        string runId,
        int productId,
        UpdateDefinition<BsonDocument> update,
        CancellationToken cancellationToken
    ) =>
        _migrationDatabase
            .GetCollection<BsonDocument>(Runs)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                update,
                cancellationToken: cancellationToken
            );

    private async Task DiscardProductAsync(
        string runId,
        int productId,
        CancellationToken cancellationToken
    )
    {
        var versionId = VersionId(productId, _options.TypeSchema, runId);
        var pricingId = PricingId(productId, _options.TypeSchema);
        await _migrationDatabase
            .GetCollection<BsonDocument>(Rows)
            .DeleteManyAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                    Builders<BsonDocument>.Filter.Eq("productId", productId)
                ),
                cancellationToken
            );
        await _migrationDatabase
            .GetCollection<BsonDocument>(RateSets)
            .DeleteManyAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                    Builders<BsonDocument>.Filter.Eq("configuredPricingId", pricingId)
                ),
                cancellationToken
            );
        var versionFilter = Builders<BsonDocument>.Filter.Eq(
            "configuredPricingVersionId",
            versionId
        );
        await _migrationDatabase
            .GetCollection<BsonDocument>(Versions)
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", versionId), cancellationToken);
        await UpdateProductStateAsync(
            runId,
            productId,
            Builders<BsonDocument>.Update.Set(
                $"productStates.{productId}",
                new BsonDocument
                {
                    { "status", "PENDING" },
                    { "lastRateId", 0 },
                    { "discardedAt", DateTime.UtcNow },
                }
            ),
            cancellationToken
        );
    }

    private async Task<string> CloseStoppedRunAsync(
        string runId,
        int? discardedProductId,
        CancellationToken cancellationToken
    )
    {
        var runs = _migrationDatabase.GetCollection<BsonDocument>(Runs);
        var run = await runs.Find(Builders<BsonDocument>.Filter.Eq("_id", runId))
            .FirstAsync(cancellationToken);
        var productIds = run["productIds"].AsBsonArray.Select(value => value.ToInt32()).ToArray();
        var typeSchema = run.GetValue("typeSchema", Ordinary).AsString;
        if (typeSchema is Baseline or Special)
        {
            var globalNextRunId = $"{runId}:next:{Guid.NewGuid():N}";
            var globalRootRunId = run.GetValue("rootRunId", runId).AsString;
            await runs.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                Builders<BsonDocument>
                    .Update.Set("status", "CANCELLED")
                    .Set("globalState.status", "CANCELLED")
                    .Set("stoppedAt", DateTime.UtcNow)
                    .Set(
                        "discardedProductId",
                        discardedProductId is null
                            ? (BsonValue)BsonNull.Value
                            : new BsonInt32(discardedProductId.Value)
                    )
                    .Set("nextRunId", globalNextRunId)
                    .Set("updatedAt", DateTime.UtcNow),
                cancellationToken: cancellationToken
            );
            await runs.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", globalNextRunId },
                    { "startedAt", DateTime.UtcNow },
                    { "status", "PENDING" },
                    { "modelVersion", ModelVersion },
                    { "typeSchema", typeSchema },
                    { "productIds", new BsonArray() },
                    { "requestedProductIds", new BsonArray() },
                    {
                        "globalState",
                        new BsonDocument { { "status", "PENDING" }, { "lastRateId", 0 } }
                    },
                    { "rootRunId", globalRootRunId },
                    { "splitFromRunId", runId },
                    { "createdFromStopAt", DateTime.UtcNow },
                },
                cancellationToken: cancellationToken
            );
            return globalNextRunId;
        }
        var states = run.GetValue("productStates", new BsonDocument()).AsBsonDocument;
        var completedProductIds = productIds
            .Where(productId =>
                ProductState(run, productId).GetValue("status", "PENDING").AsString == "LOADED"
            )
            .ToArray();
        var pendingProductIds = productIds.Except(completedProductIds).ToArray();
        var completedStates = new BsonDocument(
            completedProductIds.Select(productId => new BsonElement(
                productId.ToString(),
                ProductState(run, productId).DeepClone()
            ))
        );
        var versionIds = completedStates
            .Elements.SelectMany(element =>
                element.Value.AsBsonDocument.GetValue("versionIds", new BsonArray()).AsBsonArray
            )
            .Select(value => value.AsString)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var completedPricingIds =
            _options.TypeSchema == Ordinary
                ? completedProductIds.Select(productId => PricingId(productId, Ordinary))
                : [PricingId(0, _options.TypeSchema)];
        var rateSetCount = await _migrationDatabase
            .GetCollection<BsonDocument>(RateSets)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("migrationRunId", runId),
                    Builders<BsonDocument>.Filter.In("configuredPricingId", completedPricingIds)
                ),
                cancellationToken: cancellationToken
            );
        var nextRunId = $"{runId}:next:{Guid.NewGuid():N}";
        var rootRunId = run.GetValue("rootRunId", runId).AsString;

        await runs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            Builders<BsonDocument>
                .Update.Set("status", "LOADED")
                .Set("productIds", new BsonArray(completedProductIds.Select(id => (BsonValue)id)))
                .Set("productStates", completedStates)
                .Set("configuredPricingVersionIds", new BsonArray(versionIds))
                .Set("rateSets", rateSetCount)
                .Set("stoppedAt", DateTime.UtcNow)
                .Set(
                    "discardedProductId",
                    discardedProductId is null
                        ? (BsonValue)BsonNull.Value
                        : new BsonInt32(discardedProductId.Value)
                )
                .Set("nextRunId", nextRunId)
                .Set("updatedAt", DateTime.UtcNow),
            cancellationToken: cancellationToken
        );

        var pendingStates = new BsonDocument(
            pendingProductIds.Select(productId => new BsonElement(
                productId.ToString(),
                new BsonDocument { { "status", "PENDING" }, { "lastRateId", 0 } }
            ))
        );
        await runs.InsertOneAsync(
            new BsonDocument
            {
                { "_id", nextRunId },
                { "startedAt", DateTime.UtcNow },
                { "status", "PENDING" },
                { "modelVersion", ModelVersion },
                { "typeSchema", Ordinary },
                { "productIds", new BsonArray(pendingProductIds.Select(id => (BsonValue)id)) },
                {
                    "requestedProductIds",
                    run.GetValue(
                        "requestedProductIds",
                        new BsonArray(productIds.Select(id => (BsonValue)id))
                    )
                },
                { "productStates", pendingStates },
                { "rootRunId", rootRunId },
                { "splitFromRunId", runId },
                { "createdFromStopAt", DateTime.UtcNow },
            },
            cancellationToken: cancellationToken
        );
        return nextRunId;
    }

    private static string PricingId(int productId, string typeSchema) =>
        typeSchema == Ordinary
            ? $"configured-pricing:credit:{productId}:ORDINARY"
            : $"configured-pricing:credit:{typeSchema}";

    private static string VersionId(int productId, string typeSchema, string runId) =>
        typeSchema == Ordinary
            ? $"configured-pricing-version:credit:{productId}:ORDINARY:migration:{runId}"
            : $"configured-pricing-version:credit:{typeSchema}:migration:{runId}";

    private static IEnumerable<(string Dimension, string GroupId)> DimensionGroupReferences(
        BsonDocument rateSet
    )
    {
        if (
            !rateSet.TryGetValue("coordinates", out var coordinatesValue)
            || !coordinatesValue.IsBsonDocument
            || !coordinatesValue.AsBsonDocument.TryGetValue(
                "dimensionGroupIds",
                out var groupsValue
            )
            || !groupsValue.IsBsonDocument
        )
            yield break;
        var groups = groupsValue.AsBsonDocument;
        foreach (var dimension in new[] { "branch", "internalRating" })
            if (groups.TryGetValue(dimension, out var groupId) && groupId.IsString)
                yield return (dimension, groupId.AsString);
    }

    private static string DimensionGroupId(string dimensionKey, IEnumerable<int> memberIds)
    {
        var memberKey = string.Join(",", memberIds.Order());
        return $"configured-pricing-dimension-group:{dimensionKey}:{Hash(memberKey)}";
    }

    private static string AmountRangeId(BsonValue min, BsonValue max) =>
        $"configured-pricing-amount-range:{Hash($"{RangeBoundKey(min)}|{RangeBoundKey(max)}")}";

    private static string TermRangeId(BsonValue min, BsonValue max) =>
        $"configured-pricing-term-range:{Hash($"{RangeBoundKey(min)}|{RangeBoundKey(max)}")}";

    private static string RangeBoundKey(BsonValue value) =>
        value.IsBsonNull
            ? "null"
            : value.ToDecimal().ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    private static bool RatesEquivalent(BsonDocument existing, BsonDocument candidate) =>
        new[] { "compensatory", "compensatoryMax", "moratory" }.All(field =>
            existing
                .GetValue(field, BsonNull.Value)
                .Equals(candidate.GetValue(field, BsonNull.Value))
        );

    private static int? NullableInt(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));

    private static bool NullableBool(SqlDataReader reader, string name) =>
        !reader.IsDBNull(reader.GetOrdinal(name)) && reader.GetBoolean(reader.GetOrdinal(name));

    private static string StringValue(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal(name));

    private static BsonValue DecimalValue(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? BsonNull.Value
            : new BsonDecimal128(reader.GetDecimal(reader.GetOrdinal(name)));

    private static MigrationPreviewRange ToPreviewRange(BsonDocument range) =>
        new(
            range["_id"].AsString,
            PreviewDecimal(range.GetValue("min", BsonNull.Value)),
            PreviewDecimal(range.GetValue("max", BsonNull.Value)),
            range.GetValue("label", string.Empty).AsString
        );

    private static decimal? PreviewDecimal(BsonValue value) =>
        value.IsBsonNull ? null : value.ToDecimal();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
