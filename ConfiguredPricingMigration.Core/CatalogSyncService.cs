using System.Data;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ConfiguredPricingMigration.Core;

public sealed record CatalogSyncOptions(
    string SqlConnectionString,
    string MongoConnectionString,
    string MongoDatabase
);

public sealed record CatalogSyncResult(long Loaded, long Deactivated);

public sealed record CatalogSyncSummary(
    string RunId,
    IReadOnlyDictionary<string, CatalogSyncResult> Catalogs
);

public sealed class CatalogSyncService(CatalogSyncOptions options)
{
    private const string Runs = "ConfiguredPricingCatalogSyncRuns";

    public async Task<CatalogSyncSummary> SyncAsync(
        string runId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SqlConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MongoConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MongoDatabase);

        var database = new MongoClient(options.MongoConnectionString).GetDatabase(
            options.MongoDatabase
        );
        var runs = database.GetCollection<BsonDocument>(Runs);
        await runs.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", runId),
            new BsonDocument
            {
                { "_id", runId },
                { "status", "RUNNING" },
                { "mode", "SNAPSHOT_ONLY" },
                { "startedAt", DateTime.UtcNow },
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken
        );

        try
        {
            await using var connection = new SqlConnection(options.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)
                await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshots = new List<CatalogSnapshot>();
            foreach (var definition in CatalogDefinition.All)
            {
                snapshots.Add(
                    await ReadSnapshotAsync(
                        connection,
                        transaction,
                        definition,
                        runId,
                        cancellationToken
                    )
                );
            }
            await transaction.CommitAsync(cancellationToken);

            var results = new Dictionary<string, CatalogSyncResult>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                results[snapshot.Definition.CollectionName] = await ApplySnapshotAsync(
                    database,
                    snapshot,
                    runId,
                    cancellationToken
                );
            }

            await runs.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                Builders<BsonDocument>
                    .Update.Set("status", "SUCCESS")
                    .Set("completedAt", DateTime.UtcNow)
                    .Set(
                        "catalogs",
                        new BsonArray(results.Select(result => new BsonDocument
                        {
                            { "collection", result.Key },
                            { "loaded", result.Value.Loaded },
                            { "deactivated", result.Value.Deactivated },
                        }))
                    ),
                cancellationToken: cancellationToken
            );

            return new CatalogSyncSummary(runId, results);
        }
        catch (Exception exception)
        {
            await runs.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runId),
                Builders<BsonDocument>
                    .Update.Set("status", "FAILED")
                    .Set("failedAt", DateTime.UtcNow)
                    .Set("error", exception.Message),
                cancellationToken: CancellationToken.None
            );
            throw;
        }
    }

    private static async Task<CatalogSnapshot> ReadSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CatalogDefinition definition,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var documents = new List<BsonDocument>();
        var moduleFilter = definition.ModuleColumn is null
            ? string.Empty
            : $" AND {definition.ModuleColumn} = 1";
        await using var command = new SqlCommand(
            $"SELECT * FROM {definition.SourceTable} WHERE lVigente = 1{moduleFilter};",
            connection,
            transaction
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var keyOrdinal = reader.GetOrdinal(definition.KeyColumn);
            if (reader.IsDBNull(keyOrdinal))
            {
                continue;
            }

            var document = new BsonDocument
            {
                { "_id", ToBsonValue(reader.GetValue(keyOrdinal)) },
                { "active", true },
                {
                    "source",
                    new BsonDocument
                    {
                        { "system", "CoreBank" },
                        { "table", definition.SourceTable },
                        { "syncMode", "SNAPSHOT_ONLY" },
                        { "snapshotRunId", runId },
                        { "syncedAt", DateTime.UtcNow },
                    }
                },
            };
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!reader.IsDBNull(index))
                {
                    document[reader.GetName(index)] = ToBsonValue(reader.GetValue(index));
                }
            }
            documents.Add(document);
        }

        return new CatalogSnapshot(definition, documents);
    }

    private static async Task<CatalogSyncResult> ApplySnapshotAsync(
        IMongoDatabase database,
        CatalogSnapshot snapshot,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var collection = database.GetCollection<BsonDocument>(snapshot.Definition.CollectionName);
        var writes = snapshot.Documents.Select(document =>
                (WriteModel<BsonDocument>)new ReplaceOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                    document
                )
                {
                    IsUpsert = true,
                }
            )
            .ToList();
        if (writes.Count > 0)
        {
            await collection.BulkWriteAsync(
                writes,
                new BulkWriteOptions { IsOrdered = false },
                cancellationToken
            );
        }

        var deactivated = await collection.UpdateManyAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("active", true),
                Builders<BsonDocument>.Filter.Ne("source.snapshotRunId", runId)
            ),
            Builders<BsonDocument>
                .Update.Set("active", false)
                .Set("source.deactivatedAt", DateTime.UtcNow)
                .Set("source.deactivationReason", "NOT_VIGENTE_IN_SNAPSHOT"),
            cancellationToken: cancellationToken
        );

        return new CatalogSyncResult(snapshot.Documents.Count, deactivated.ModifiedCount);
    }

    private static BsonValue ToBsonValue(object value) => value switch
    {
        int integer => integer,
        long integer => integer,
        short integer => (int)integer,
        bool boolean => boolean,
        decimal number => new BsonDecimal128(
            Decimal128.Parse(number.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ),
        DateTime date => date,
        byte[] bytes => new BsonBinaryData(bytes),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed record CatalogDefinition(
        string CollectionName,
        string SourceTable,
        string KeyColumn,
        string? ModuleColumn = null
    )
    {
        public static IReadOnlyList<CatalogDefinition> All { get; } =
        [
            new("CatalogProducts", "dbo.SI_FinProducto", "idProducto", "idModulo"),
            new("CatalogInternalRatings", "dbo.SI_FinClasifInterna", "idClasifInterna"),
            new("CatalogBranches", "dbo.SI_FinEstablecimiento", "idEstablecimiento"),
            new("CatalogRateTypes", "dbo.SI_FinTipoTasa", "idTipoTasa", "idModulo"),
            new("CatalogCurrencies", "dbo.SI_FinMoneda", "idMoneda"),
            new("CatalogPersonTypes", "dbo.SI_FinTipoPersona", "idTipoPersona"),
        ];
    }

    private sealed record CatalogSnapshot(
        CatalogDefinition Definition,
        IReadOnlyList<BsonDocument> Documents
    );
}
