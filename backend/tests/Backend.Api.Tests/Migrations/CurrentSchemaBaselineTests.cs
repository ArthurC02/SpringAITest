using Backend.Api.Data.Migrations;
using Dapper;
using Npgsql;

namespace Backend.Api.Tests.Migrations;

/// <summary>P3-R1 current-baseline checks. These do not register or execute production migrations.</summary>
[Collection("Postgres")]
public sealed class CurrentSchemaBaselineTests(PostgresFixture fixture)
{
    [Fact]
    public void ProductionManifestPinsTheCurrentInventoryWithoutShippingSql()
    {
        Assert.Equal(52, MigrationManifest.SpringAITestApplicationTables.Length);
        Assert.Equal(52, MigrationManifest.SpringAITestApplicationTables.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("document_ingest", MigrationManifest.SpringAITestApplicationTables);
        Assert.Contains("checkpoint_retention_ack", MigrationManifest.SpringAITestApplicationTables);

        Assert.Equal(
            ["checkpoint_blobs", "checkpoint_migrations", "checkpoint_writes", "checkpoints", "workflow_root_context_checkpoint"],
            MigrationManifest.OptionalWorkflowCheckpointTables.Order(StringComparer.Ordinal));
        Assert.Equal(57, MigrationManifest.SpringAITestLegacyObjects.Length);
        Assert.Equal(["plpgsql", "vector"], MigrationManifest.Production.AllowedExtensions.Order(StringComparer.Ordinal));

        Assert.Equal(0, MigrationManifest.Production.BundleThroughVersion);
        Assert.Empty(MigrationManifest.Production.Scripts);
        Assert.Empty(MigrationManifest.Production.Postconditions);
    }

    [SkippableFact]
    public async Task CurrentDbBootstrapSchemaMatchesThe52TableBaselineAndRequiredIndex()
    {
        fixture.SkipIfUnavailable();

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        var actual = (await conn.QueryAsync<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename"))
            .Except(MigrationManifest.OptionalWorkflowCheckpointTables, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(MigrationManifest.SpringAITestApplicationTables.Order(StringComparer.Ordinal), actual);
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname='public'"
            + " AND tablename='conversations' AND indexname='conversations_history_page_idx')"));
    }
}
