using System.Reflection;
using System.Runtime.ExceptionServices;
using ConfiguredPricingMigration.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;

namespace ConfiguredPricingMigration.Tests;

/// <summary>Checks conflict resolution before dimension inference using synthetic rows only.</summary>
[TestClass]
public sealed class MigrationConflictNormalizationTests
{
    [TestMethod]
    public void ConflictingHistoryDoesNotPreventBranchesWithEqualEffectiveRatesFromMerging()
    {
        var rows = new[] { Row(100, 1, 51, 40m), Row(200, 1, 51, 45m), Row(300, 1, 43, 45m) };
        var original = rows.Select(row => row.ToJson()).ToArray();

        var normalized = Normalize(rows);
        var cells = Consolidate(normalized);

        normalized.Rows.Should().HaveCount(2);
        var conflict = normalized.Conflicts.Should().ContainSingle().Which;
        conflict.SelectedLegacyRateId.Should().Be(200);
        conflict.Rates.Select(rate => rate.LegacyRateId).Should().Equal(100, 200);
        var rate = cells.Should().ContainSingle().Which["rates"].AsBsonArray.Single();
        rate["compensatory"].ToDecimal().Should().Be(45m);
        Ids(rate).Should().Equal(200, 300);
        GroupIds(normalized.Rows, "branch").Should().HaveCount(1);
        rows.Select(row => row.ToJson()).Should().Equal(original);
    }

    [TestMethod]
    public void ClassificationsAreComparedUsingTheirSelectedRatesNotTheirDiscardedHistory()
    {
        var normalized = Normalize([Row(100, 1, 51, 40m), Row(200, 1, 51, 45m), Row(300, 2, 51, 45m)]);

        var cells = Consolidate(normalized);

        GroupIds(normalized.Rows, "internalRating").Should().HaveCount(1);
        Ids(cells.Single()["rates"].AsBsonArray.Single()).Should().Equal(200, 300);
    }

    [TestMethod]
    public void DifferentEffectiveRatesRemainInSeparateGroups()
    {
        var normalized = Normalize([Row(100, 1, 51, 40m), Row(200, 1, 51, 45m), Row(300, 2, 51, 47m)]);

        var cells = Consolidate(normalized);

        GroupIds(normalized.Rows, "internalRating").Should().HaveCount(2);
        cells.Should().HaveCount(2);
    }

    [TestMethod]
    public void EquivalentDuplicatesRetainEveryLegacyIdAfterGroupsAreMerged()
    {
        var normalized = Normalize([Row(100, 1, 51, 45m), Row(200, 1, 51, 45m), Row(300, 1, 43, 45m)]);

        var cells = Consolidate(normalized);

        normalized.Conflicts.Should().BeEmpty();
        normalized.Rows.Should().HaveCount(2);
        Ids(cells.Single()["rates"].AsBsonArray.Single()).Should().Equal(100, 200, 300);
    }

    [TestMethod]
    public void AConflictStillKeepsOnlyTheLatestIdEvenIfAnOlderRowMatchesItsValue()
    {
        var normalized = Normalize([Row(100, 1, 51, 45m), Row(150, 1, 51, 40m), Row(200, 1, 51, 45m)]);

        var cells = Consolidate(normalized);

        Ids(cells.Single()["rates"].AsBsonArray.Single()).Should().Equal(200);
        var conflict = normalized.Conflicts.Single();
        conflict.Rates.Select(rate => rate.LegacyRateId).Should().Equal(100, 150, 200);
        conflict.Rates.Select(rate => rate.Compensatory).Should().Equal(45m, 40m, 45m);
        conflict.SelectedLegacyRateId.Should().Be(200);
        normalized.Conflicts.Sum(item => item.Rates.Count - 1).Should().Be(2);
    }

    [TestMethod]
    [DataRow("amountRangeId")]
    [DataRow("termRangeId")]
    [DataRow("branchId")]
    [DataRow("internalRatingLegacyId")]
    [DataRow("insurance")]
    [DataRow("currencyId")]
    [DataRow("personTypeId")]
    [DataRow("pricingMode")]
    [DataRow("rateTypeId")]
    [DataRow("configuredPricingId")]
    public void DifferentOriginalContextsDoNotCompeteForTheLatestId(string field)
    {
        var first = Row(100, 1, 51, 40m);
        var second = Row(200, 1, 51, 45m);
        second[field] = field switch
        {
            "branchId" or "internalRatingLegacyId" or "currencyId" or "personTypeId" or "rateTypeId" =>
                new BsonInt32(2),
            "insurance" => BsonBoolean.False,
            _ => new BsonString("other")
        };
        SetOriginalKey(second);

        var normalized = Normalize([first, second]);

        normalized.Rows.Should().HaveCount(2);
        normalized.Conflicts.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("compensatory")]
    [DataRow("compensatoryMax")]
    [DataRow("moratory")]
    public void ConflictsInAnyRateFieldAreResolvedBeforeInference(string field)
    {
        var old = Row(100, 1, 51, 45m);
        old["rate"][field] = new BsonDecimal128(99m);
        var normalized = Normalize([old, Row(200, 1, 51, 45m), Row(300, 1, 43, 45m)]);

        var cells = Consolidate(normalized);

        cells.Should().HaveCount(1);
        normalized.Conflicts.Single().SelectedLegacyRateId.Should().Be(200);
        Ids(cells.Single()["rates"].AsBsonArray.Single()).Should().Equal(200, 300);
    }

    [TestMethod]
    public void ResolvingOneRateTypeDoesNotDiscardAnotherRateType()
    {
        var otherType = Row(300, 1, 51, 55m);
        otherType["rateTypeId"] = 30;
        var normalized = Normalize([Row(100, 1, 51, 40m), Row(200, 1, 51, 45m), otherType]);

        var rates = Consolidate(normalized).Single()["rates"].AsBsonArray;

        rates.Should().HaveCount(2);
        Ids(rates.Single(rate => rate["rateTypeId"].AsInt32 == 19)).Should().Equal(200);
        Ids(rates.Single(rate => rate["rateTypeId"].AsInt32 == 30)).Should().Equal(300);
    }

    [TestMethod]
    [DataRow("compensatory")]
    [DataRow("compensatoryMax")]
    [DataRow("moratory")]
    public void NullValuesSelectedFromTheLatestRowArePreserved(string field)
    {
        var latest = Row(200, 1, 51, 45m);
        var otherBranch = Row(300, 1, 43, 45m);
        latest["rate"][field] = BsonNull.Value;
        otherBranch["rate"][field] = BsonNull.Value;
        var normalized = Normalize([Row(100, 1, 51, 45m), latest, otherBranch]);

        var cells = Consolidate(normalized);

        var rate = cells.Single()["rates"].AsBsonArray.Single();
        rate[field].IsBsonNull.Should().BeTrue();
        Ids(rate).Should().Equal(200, 300);
        normalized.Conflicts.Single().SelectedLegacyRateId.Should().Be(200);
    }

    [TestMethod]
    public void NormalizationAndConsolidationDoNotDependOnInputOrder()
    {
        var rows = new[]
        {
            Row(100, 1, 51, 40m), Row(200, 1, 51, 45m),
            Row(300, 1, 43, 45m), Row(400, 1, 43, 45m)
        };

        var first = Normalize(rows);
        var reversed = Normalize(rows.Reverse().ToArray());

        Consolidate(first).Select(cell => cell.ToJson()).Should()
            .Equal(Consolidate(reversed).Select(cell => cell.ToJson()));
        first.Conflicts.Should().BeEquivalentTo(reversed.Conflicts);
    }

    [TestMethod]
    public void NormalizationHonorsCancellationBeforeProcessingRows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Action normalize = () => Normalize([Row(100, 1, 51, 40m)], cancellation.Token);

        normalize.Should().Throw<OperationCanceledException>();
    }

    [TestMethod]
    public void NineClassificationsAnd128BranchesMergeAfterResolvingTheirHistoricalConflicts()
    {
        var rows = new List<BsonDocument>();
        for (var profile = 1; profile <= 9; profile++)
        {
            for (var branch = 1; branch <= 128; branch++)
            {
                foreach (var insurance in new[] { false, true })
                {
                    rows.Add(Row(rows.Count + 1, profile, branch, 99m, insurance));
                    rows.Add(Row(rows.Count + 1, profile, branch, insurance ? 9.5m : 16.5m, insurance));
                }
            }
        }

        var normalized = Normalize(rows);
        var cells = Consolidate(normalized);

        rows.Should().HaveCount(4608);
        normalized.Rows.Should().HaveCount(2304);
        normalized.Conflicts.Should().HaveCount(2304);
        cells.Should().HaveCount(2);
        GroupIds(normalized.Rows, "branch").Should().HaveCount(1);
        GroupIds(normalized.Rows, "internalRating").Should().HaveCount(1);
        var retained = cells.SelectMany(cell => cell["rates"].AsBsonArray).SelectMany(Ids).Order().ToArray();
        retained.Should().Equal(Enumerable.Range(1, 2304).Select(index => 2L * index));
    }

    private static NormalizedRows Normalize(IReadOnlyList<BsonDocument> rows, CancellationToken token = default)
    {
        var result = Invoke("NormalizeOrdinaryRateRows", rows, token)!;
        return new NormalizedRows(
            (IReadOnlyList<BsonDocument>)result.GetType().GetProperty("Rows")!.GetValue(result)!,
            (IReadOnlyList<MigrationRateConflict>)result.GetType().GetProperty("Conflicts")!.GetValue(result)!);
    }

    private static IReadOnlyList<BsonDocument> Consolidate(NormalizedRows normalized)
    {
        Invoke("InferOrdinaryDimensionPartitions", normalized.Rows, CancellationToken.None);
        var conflicts = normalized.Conflicts.ToList();
        var cells = (List<BsonDocument>)Invoke("BuildDirectRateSets", "scheme", "ORDINARY", normalized.Rows,
            conflicts)!;
        conflicts.Should().BeEquivalentTo(normalized.Conflicts);
        return cells;
    }

    private static BsonDocument Row(long id, int profile, int branch, decimal rate, bool insurance = true)
    {
        var row = new BsonDocument
        {
            ["configuredPricingId"] = "scheme",
            ["legacyRateId"] = id,
            ["typeSchema"] = "ORDINARY",
            ["amountRangeId"] = "amount",
            ["termRangeId"] = "term",
            ["branchId"] = branch,
            ["internalRatingLegacyId"] = profile,
            ["insurance"] = insurance,
            ["currencyId"] = 1,
            ["personTypeId"] = 1,
            ["pricingMode"] = "STANDARD",
            ["rateTypeId"] = 19,
            ["dimensionGroupIds"] = new BsonDocument(),
            ["rate"] = new BsonDocument
            {
                ["compensatory"] = new BsonDecimal128(rate),
                ["compensatoryMax"] = new BsonDecimal128(109.83m),
                ["moratory"] = new BsonDecimal128(14.3m)
            }
        };
        SetOriginalKey(row);
        return row;
    }

    private static void SetOriginalKey(BsonDocument row)
    {
        Invoke("AssignSingletonGroups", new[] { row }, "branch");
        Invoke("AssignSingletonGroups", new[] { row }, "internalRating");
    }

    private static long[] Ids(BsonValue rate) => rate["legacyRateIds"].AsBsonArray
        .Select(value => value.ToInt64()).ToArray();

    private static string[] GroupIds(IEnumerable<BsonDocument> rows, string dimension) => rows
        .Select(row => row["dimensionGroupIds"][dimension].AsString).Distinct().ToArray();

    private static object? Invoke(string name, params object[] arguments)
    {
        try
        {
            return typeof(ConfiguredPricingMigrationService)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed record NormalizedRows(
        IReadOnlyList<BsonDocument> Rows,
        IReadOnlyList<MigrationRateConflict> Conflicts);
}
