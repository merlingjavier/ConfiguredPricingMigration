using ConfiguredPricingMigration.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfiguredPricingMigration.Tests;

/// <summary>Validates SQL timeout configuration without opening database connections.</summary>
[TestClass]
public sealed class SqlExtractionTimeoutTests
{
    [TestMethod]
    public void ExtractionTimeoutDefaultsTo180Seconds()
    {
        new MigrationOptions().SqlCommandTimeoutSeconds.Should().Be(180);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void MissingEnvironmentConfigurationUsesTheDefault(string? value)
    {
        MigrationOptions.ParseSqlCommandTimeoutSeconds(value).Should().Be(180);
    }

    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("180", 180)]
    [DataRow("300", 300)]
    [DataRow(" 240 ", 240)]
    public void PositiveTimeoutsAreAccepted(string value, int expected)
    {
        MigrationOptions.ParseSqlCommandTimeoutSeconds(value).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("invalid")]
    [DataRow("1.5")]
    [DataRow("2147483648")]
    public void InfiniteNegativeMalformedAndOverflowingTimeoutsAreRejected(string value)
    {
        Action parse = () => MigrationOptions.ParseSqlCommandTimeoutSeconds(value);

        parse.Should().Throw<ArgumentException>().WithMessage("*MIGRATION_SQL_COMMAND_TIMEOUT_SECONDS*");
    }

    [TestMethod]
    public void InvalidConfigurationDoesNotEchoTheConfiguredValue()
    {
        const string configuredValue = "synthetic-private-value";
        Action parse = () => MigrationOptions.ParseSqlCommandTimeoutSeconds(configuredValue);

        parse.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(configuredValue);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void ProgrammaticOptionsRejectInvalidTimeoutsBeforeCreatingDatabaseClients(int seconds)
    {
        var options = new MigrationOptions
        {
            SqlConnectionString = "synthetic-source",
            TargetMongoConnectionString = "synthetic-target",
            TargetMongoDatabase = "synthetic-database",
            SqlCommandTimeoutSeconds = seconds
        };
        Action create = () => new ConfiguredPricingMigrationService(options);

        create.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(MigrationOptions.SqlCommandTimeoutSeconds));
    }
}
