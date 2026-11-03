using System.Reflection;
using ConfiguredPricingMigration.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;

namespace ConfiguredPricingMigration.Tests;

/// <summary>Verifies dimension inference without reading or modifying a database.</summary>
[TestClass]
public sealed class DimensionConsolidationTests
{
    [TestMethod]
    public void EquivalentRowsInTheSameContextHaveTheSameSignatureAsASingleRow()
    {
        var row = Row(1, 1, 1, true);
        var duplicate = row.DeepClone().AsBsonDocument;
        duplicate["legacyRateId"] = 2L;
        AssignSingletons([row, duplicate]);

        Signature([row, duplicate], "internalRating").Should().Be(Signature([row], "internalRating"));
    }

    [TestMethod]
    public void EquivalentDecimalValuesHaveTheSameSignatureRegardlessOfScale()
    {
        var first = Row(1, 1, 1, true);
        var second = Row(2, 2, 1, true);
        second["rate"]["compensatory"] = new BsonDecimal128(9.50m);
        AssignSingletons([first, second]);

        Signature([first], "internalRating").Should().Be(Signature([second], "internalRating"));
    }

    [TestMethod]
    [DataRow("compensatory")]
    [DataRow("compensatoryMax")]
    [DataRow("moratory")]
    public void ConflictingRowsInTheSameContextHaveNoValidSignature(string rateField)
    {
        var first = Row(1, 1, 1, true);
        var conflicting = first.DeepClone().AsBsonDocument;
        conflicting["rate"][rateField] = new BsonDecimal128(99m);
        AssignSingletons([first, conflicting]);

        Signature([first, conflicting], "internalRating").Should().BeNull();
    }

    [TestMethod]
    public void NineEquivalentClassificationsAnd128BranchesProduceTwoCellsWithoutRemovingSourceRows()
    {
        var rows = new List<BsonDocument>();
        for (var profile = 1; profile <= 9; profile++)
        {
            for (var branch = 1; branch <= 128; branch++)
            {
                rows.Add(Row(rows.Count + 1, profile, branch, true));
                rows.Add(Row(rows.Count + 1, profile, branch, false));
            }
        }
        var sourceIds = rows.Select(row => row["legacyRateId"].ToInt64()).ToArray();

        InferPartitions(rows);

        GroupIds(rows, "branch").Should().HaveCount(1);
        GroupIds(rows, "internalRating").Should().HaveCount(1);
        rows.Select(row => row["applicabilityKey"].AsString).Distinct().Should().HaveCount(2);
        rows.Should().HaveCount(2304);
        rows.Select(row => row["legacyRateId"].ToInt64()).Should().Equal(sourceIds);
        rows.Where(row => row["insurance"].AsBoolean)
            .Should().OnlyContain(row => row["rate"]["compensatory"].ToDecimal() == 9.5m);
        rows.Where(row => !row["insurance"].AsBoolean)
            .Should().OnlyContain(row => row["rate"]["compensatory"].ToDecimal() == 16.5m);
    }

    [TestMethod]
    [DataRow("compensatory")]
    [DataRow("compensatoryMax")]
    [DataRow("moratory")]
    public void ClassificationsWithDifferentRatesAreNotMerged(string rateField)
    {
        var first = Row(1, 1, 1, true);
        var second = Row(2, 2, 1, true);
        second["rate"][rateField] = new BsonDecimal128(99m);

        InferPartitions([first, second]);

        GroupIds([first, second], "internalRating").Should().HaveCount(2);
    }

    [TestMethod]
    [DataRow("amountRangeId")]
    [DataRow("termRangeId")]
    [DataRow("insurance")]
    [DataRow("currencyId")]
    [DataRow("personTypeId")]
    [DataRow("pricingMode")]
    [DataRow("rateTypeId")]
    public void ClassificationsWithDifferentContextCoverageAreNotMerged(string contextField)
    {
        var first = Row(1, 1, 1, true);
        var second = Row(2, 2, 1, true);
        second[contextField] = contextField switch
        {
            "insurance" => BsonBoolean.False,
            "currencyId" or "personTypeId" or "rateTypeId" => new BsonInt32(2),
            _ => new BsonString("other")
        };

        InferPartitions([first, second]);

        GroupIds([first, second], "internalRating").Should().HaveCount(2);
    }

    [TestMethod]
    public void DuplicateSourceRowsDoNotPreventEquivalentClassificationsFromMerging()
    {
        var first = Row(1, 1, 1, true);
        var duplicate = Row(2, 1, 1, true);
        var secondProfile = Row(3, 2, 1, true);
        var rows = new[] { first, duplicate, secondProfile };

        InferPartitions(rows);

        GroupIds(rows, "internalRating").Should().HaveCount(1);
        rows.Select(row => row["legacyRateId"].ToInt64()).Should().Equal(1, 2, 3);
    }

    [TestMethod]
    public void InferenceIsIndependentOfInputRowOrder()
    {
        var rows = new[]
        {
            Row(1, 1, 1, true), Row(2, 1, 2, true),
            Row(3, 2, 1, true), Row(4, 2, 2, true)
        };
        var reversed = rows.Reverse().Select(row => row.DeepClone().AsBsonDocument).ToArray();

        InferPartitions(rows);
        InferPartitions(reversed);

        GroupIds(rows, "internalRating").Should().Equal(GroupIds(reversed, "internalRating"));
        GroupIds(rows, "branch").Should().Equal(GroupIds(reversed, "branch"));
    }

    private static BsonDocument Row(long legacyId, int profile, int branch, bool insurance) => new()
    {
        ["legacyRateId"] = legacyId,
        ["typeSchema"] = "ORDINARY",
        ["amountRangeId"] = "amount-001",
        ["termRangeId"] = "term-001",
        ["dimensionGroupIds"] = new BsonDocument(),
        ["internalRatingLegacyId"] = profile,
        ["branchId"] = branch,
        ["insurance"] = insurance,
        ["currencyId"] = 1,
        ["personTypeId"] = 1,
        ["pricingMode"] = "STANDARD",
        ["rateTypeId"] = 19,
        ["rate"] = new BsonDocument
        {
            ["compensatory"] = new BsonDecimal128(insurance ? 9.5000m : 16.5000m),
            ["compensatoryMax"] = new BsonDecimal128(insurance ? 109.8300m : 109.0000m),
            ["moratory"] = new BsonDecimal128(14.3000m)
        }
    };

    private static void AssignSingletons(IReadOnlyList<BsonDocument> rows)
    {
        Invoke("AssignSingletonGroups", rows, "branch");
        Invoke("AssignSingletonGroups", rows, "internalRating");
    }

    private static void InferPartitions(IReadOnlyList<BsonDocument> rows)
    {
        Invoke("InferOrdinaryDimensionPartitions", rows, CancellationToken.None);
    }

    private static string[] GroupIds(IEnumerable<BsonDocument> rows, string dimension) => rows
        .Select(row => row["dimensionGroupIds"][dimension].AsString)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string? Signature(IEnumerable<BsonDocument> rows, string dimension) =>
        (string?)Invoke("GlobalRateSignature", rows, dimension);

    private static object? Invoke(string name, params object[] arguments) =>
        typeof(ConfiguredPricingMigrationService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments);
}
