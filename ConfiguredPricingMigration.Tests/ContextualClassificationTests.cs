using System.Reflection;
using ConfiguredPricingMigration.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;

namespace ConfiguredPricingMigration.Tests;

/// <summary>Checks contextual matrices and their expanded coverage without database access.</summary>
[TestClass]
public sealed class ContextualClassificationTests
{
    [TestMethod]
    public void ProfilesMergeOnlyInEstablishmentsWhereTheirEntireMatricesMatch()
    {
        var rows = Rows();
        Infer(rows);

        Profiles(rows, 1).Should().HaveCount(1);
        Profiles(rows, 2).Should().HaveCount(3);
        Branches(rows).Should().HaveCount(2);
        Build(rows).Should().HaveCount(8);
    }

    [TestMethod]
    public void ExpandedGroupedCellsPreserveEveryOriginalCoordinateAndRate()
    {
        var rows = Rows();
        var expected = rows.Select(OriginalValue).Order().ToArray();
        var ids = rows.Select(row => row["legacyRateId"].ToInt64()).Order().ToArray();
        Infer(rows);
        var cells = Build(rows);
        var branchMembers = Members(rows, "branch", "branchId");
        var profileMembers = Members(rows, "internalRating", "internalRatingLegacyId");
        var expanded = new List<string>();
        foreach (var cell in cells)
        {
            var coordinates = cell["coordinates"];
            foreach (var branch in branchMembers[coordinates["dimensionGroupIds"]["branch"].AsString])
                foreach (var profile in profileMembers[coordinates["dimensionGroupIds"]["internalRating"].AsString])
                    foreach (var rate in cell["rates"].AsBsonArray)
                        expanded.Add(FinancialValue(branch, profile, coordinates, cell["pricingMode"], rate["rateTypeId"], rate));
        }

        expanded.Order().Should().Equal(expected);
        expanded.Should().OnlyHaveUniqueItems();
        cells.SelectMany(cell => cell["rates"].AsBsonArray)
            .SelectMany(rate => rate["legacyRateIds"].AsBsonArray).Select(id => id.ToInt64()).Order()
            .Should().Equal(ids);
    }

    [TestMethod]
    [DataRow("compensatory")]
    [DataRow("compensatoryMax")]
    [DataRow("moratory")]
    public void DifferencesInAnyFinancialFieldPreventContextualMerging(string field)
    {
        var rows = Rows();
        rows.Single(row => row["branchId"] == 1 && row["internalRatingLegacyId"] == 2
            && !row["insurance"].AsBoolean)["rate"][field] = BsonNull.Value;

        Infer(rows);

        Profiles(rows, 1).Should().HaveCount(2);
    }

    [TestMethod]
    public void MissingCoveragePreventsMergingEvenWhenAllPresentValuesMatch()
    {
        var rows = Rows().Where(row => !(row["branchId"] == 1 && row["internalRatingLegacyId"] == 2
            && !row["insurance"].AsBoolean)).ToArray();

        Infer(rows);

        Profiles(rows, 1).Should().HaveCount(2);
    }

    [TestMethod]
    public void EquivalentEstablishmentsStillMergeBeforeContextualProfilesAreInferred()
    {
        var rows = Rows().ToList();
        foreach (var source in rows.Where(row => row["branchId"] == 1).ToArray())
        {
            var copy = source.DeepClone().AsBsonDocument;
            copy["branchId"] = 3;
            copy["legacyRateId"] = rows.Count + 1L;
            rows.Add(copy);
        }

        Infer(rows);

        Branches(rows).Should().HaveCount(2);
        rows.Where(row => row["branchId"] == 3).Select(row => row["dimensionGroupIds"]["branch"])
            .Distinct().Single().Should().Be(rows[0]["dimensionGroupIds"]["branch"]);
        Build(rows).Should().HaveCount(8);
    }

    [TestMethod]
    public void ContextualGroupIdsAndCellsAreIndependentOfSourceOrder()
    {
        var first = Rows();
        var reversed = Rows().Reverse().ToArray();
        Infer(first);
        Infer(reversed);

        Build(first).Select(cell => cell.ToJson()).Order().Should()
            .Equal(Build(reversed).Select(cell => cell.ToJson()).Order());
    }

    private static BsonDocument[] Rows()
    {
        var rows = new List<BsonDocument>();
        for (var branch = 1; branch <= 2; branch++)
            for (var profile = 1; profile <= 3; profile++)
                foreach (var insurance in new[] { true, false })
                {
                    rows.Add(new BsonDocument
                    {
                        ["configuredPricingId"] = "scheme",
                        ["typeSchema"] = "ORDINARY",
                        ["legacyRateId"] = rows.Count + 1L,
                        ["branchId"] = branch,
                        ["internalRatingLegacyId"] = profile,
                        ["dimensionGroupIds"] = new BsonDocument(),
                        ["amountRangeId"] = "amount",
                        ["termRangeId"] = "term",
                        ["insurance"] = insurance,
                        ["currencyId"] = 1,
                        ["personTypeId"] = 1,
                        ["pricingMode"] = "STANDARD",
                        ["rateTypeId"] = 19,
                        ["rate"] = new BsonDocument
                        {
                            ["compensatory"] = new BsonDecimal128(40m + (branch == 1 ? 0 : profile)),
                            ["compensatoryMax"] = new BsonDecimal128(109.83m),
                            ["moratory"] = new BsonDecimal128(14.3m)
                        }
                    });
                }
        return rows.ToArray();
    }

    private static string OriginalValue(BsonDocument row) => FinancialValue(row["branchId"].AsInt32,
        row["internalRatingLegacyId"].AsInt32, row, row["pricingMode"], row["rateTypeId"], row["rate"]);

    private static string FinancialValue(
        int branch, int profile, BsonValue coordinates, BsonValue mode, BsonValue type, BsonValue rate) =>
        $"{branch}|{profile}|{coordinates["amountRangeId"]}|{coordinates["termRangeId"]}|"
        + $"{coordinates["insurance"]}|{coordinates["currencyId"]}|{coordinates["personTypeId"]}|{mode}|{type}|"
        + $"{rate["compensatory"]}|{rate["compensatoryMax"]}|{rate["moratory"]}";

    private static Dictionary<string, int[]> Members(IEnumerable<BsonDocument> rows, string dimension, string field) =>
        rows.GroupBy(row => row["dimensionGroupIds"][dimension].AsString).ToDictionary(
            group => group.Key, group => group.Select(row => row[field].AsInt32).Distinct().Order().ToArray());

    private static string[] Profiles(IEnumerable<BsonDocument> rows, int branch) => rows
        .Where(row => row["branchId"] == branch).Select(row => row["dimensionGroupIds"]["internalRating"].AsString)
        .Distinct().ToArray();

    private static string[] Branches(IEnumerable<BsonDocument> rows) => rows
        .Select(row => row["dimensionGroupIds"]["branch"].AsString).Distinct().ToArray();

    private static void Infer(IReadOnlyList<BsonDocument> rows) =>
        Invoke("InferOrdinaryDimensionPartitions", rows, CancellationToken.None);

    private static List<BsonDocument> Build(IReadOnlyList<BsonDocument> rows) =>
        (List<BsonDocument>)Invoke("BuildDirectRateSets", "scheme", "ORDINARY", rows,
            new List<MigrationRateConflict>())!;

    private static object? Invoke(string name, params object[] arguments) =>
        typeof(ConfiguredPricingMigrationService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments);
}
