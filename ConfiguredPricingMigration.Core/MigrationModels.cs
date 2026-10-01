namespace ConfiguredPricingMigration.Core;

public sealed class MigrationOptions
{
    public string SqlConnectionString { get; init; } = string.Empty;
    public string MigrationMongoConnectionString { get; init; } = string.Empty;
    public string MigrationMongoDatabase { get; init; } = "ConfiguredPricingMigration";
    public string TargetMongoConnectionString { get; init; } = string.Empty;
    public string TargetMongoDatabase { get; init; } = "ConfiguredPricing";
    public string TypeSchema { get; init; } = "ORDINARY";
    public int BatchSize { get; init; } = 10_000;
    public IReadOnlyList<int> ProductIds { get; init; } = Array.Empty<int>();
}

public sealed record MigrationProgress(
    string Stage,
    long Processed,
    long Loaded,
    long Rejected,
    string Message
);

public sealed class MigrationStoppedException(string nextRunId)
    : OperationCanceledException("Migración detenida.")
{
    public string NextRunId { get; } = nextRunId;
}

public sealed record MigrationSummary(
    string RunId,
    long Extracted,
    long Loaded,
    long Rejected,
    long RateSets,
    IReadOnlyList<string> VersionIds
);

public sealed record ValidationSummary(
    bool IsValid,
    long SourceRows,
    long RateSets,
    long DuplicateRateTypes,
    long AmbiguousCells,
    long GroupConflicts,
    string Message
);

public sealed record MigrationPreview(
    IReadOnlyList<MigrationPreviewProduct> Products,
    IReadOnlyList<MigrationPreviewRange> AmountRanges,
    IReadOnlyList<MigrationPreviewRange> TermRanges,
    IReadOnlyList<MigrationPreviewGroup> Groups,
    IReadOnlyList<MigrationPreviewRateType> RateTypes,
    IReadOnlyList<MigrationPreviewRateSet> RateSets,
    string TypeSchema = "ORDINARY"
);

public sealed record MigrationPreviewProduct(string Id, int ProductId, string Display);

public sealed record MigrationPreviewRun(
    string Id,
    string Status,
    DateTime? CompletedAt,
    long RateSets,
    string TypeSchema = "ORDINARY"
);

public sealed record MigrationPreviewRange(string Id, decimal? Min, decimal? Max, string Label);

public sealed record MigrationPreviewGroup(
    string Id,
    string DimensionKey,
    string Code,
    IReadOnlyList<int> MemberIds
);

public sealed record MigrationPreviewRateType(int Id, string Code);

public sealed record MigrationPreviewRate(
    int RateTypeId,
    decimal? Compensatory,
    decimal? CompensatoryMax,
    decimal? Moratory
);

public sealed record MigrationPreviewRateSet(
    string ProductId,
    string AmountRangeId,
    string TermRangeId,
    string BranchGroupId,
    string InternalRatingGroupId,
    bool Insurance,
    int CurrencyId,
    int PersonTypeId,
    string PricingMode,
    IReadOnlyList<MigrationPreviewRate> Rates,
    int ExceptionCount
);
