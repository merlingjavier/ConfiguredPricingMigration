using System.Reflection;
using ConfiguredPricingMigration.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;

namespace ConfiguredPricingMigration.Tests;

/// <summary>Checks index preparation through mocks; no MongoDB server is contacted.</summary>
[TestClass]
public sealed class TargetIndexTests
{
    [TestMethod]
    public async Task PreparationCreatesTheVersionLookupAndCellUniquenessIndexAndPropagatesCancellation()
    {
        var indexes = new Mock<IMongoIndexManager<BsonDocument>>(MockBehavior.Strict);
        var collection = new Mock<IMongoCollection<BsonDocument>>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        CreateIndexModel<BsonDocument>? requestedIndex = null;
        indexes.Setup(manager => manager.CreateOneAsync(
                It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(), cancellation.Token))
            .Callback<CreateIndexModel<BsonDocument>, CreateOneIndexOptions, CancellationToken>(
                (model, _, _) => requestedIndex = model)
            .ReturnsAsync("configuredPricingVersionId_1_configuredPricingId_1_applicabilityKey_1");
        collection.SetupGet(value => value.Indexes).Returns(indexes.Object);
        database.Setup(value => value.GetCollection<BsonDocument>("ConfiguredPricingRates", null))
            .Returns(collection.Object);

        await Prepare(database.Object, cancellation.Token);
        await Prepare(database.Object, cancellation.Token);

        requestedIndex.Should().NotBeNull();
        requestedIndex!.Options.Unique.Should().BeTrue();
        var registry = BsonSerializer.SerializerRegistry;
        var keys = requestedIndex.Keys.Render(new RenderArgs<BsonDocument>(
            registry.GetSerializer<BsonDocument>(), registry));
        keys.Should().Equal(new BsonDocument
        {
            ["configuredPricingVersionId"] = 1,
            ["configuredPricingId"] = 1,
            ["applicabilityKey"] = 1
        });
        indexes.Verify(manager => manager.CreateOneAsync(
            It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(), cancellation.Token),
            Times.Exactly(2));
        indexes.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task PreparationPropagatesIndexDefinitionFailuresWithoutDroppingExistingIndexes()
    {
        var indexes = new Mock<IMongoIndexManager<BsonDocument>>(MockBehavior.Strict);
        var collection = new Mock<IMongoCollection<BsonDocument>>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        var failure = new InvalidOperationException("Synthetic incompatible index definition");
        indexes.Setup(manager => manager.CreateOneAsync(
                It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        collection.SetupGet(value => value.Indexes).Returns(indexes.Object);
        database.Setup(value => value.GetCollection<BsonDocument>("ConfiguredPricingRates", null))
            .Returns(collection.Object);

        Func<Task> prepare = () => Prepare(database.Object, CancellationToken.None);

        (await prepare.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        indexes.Verify(manager => manager.CreateOneAsync(
            It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
        indexes.VerifyNoOtherCalls();
    }

    private static Task Prepare(IMongoDatabase database, CancellationToken cancellationToken) =>
        (Task)typeof(ConfiguredPricingMigrationService)
            .GetMethod("EnsureTargetIndexesAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [database, cancellationToken])!;
}
