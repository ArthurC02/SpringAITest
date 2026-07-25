using System.Text.Json;
using Dapper;
using Npgsql;

namespace Backend.Api.RuntimeDiscovery;

public sealed class RuntimeBindingRepository(NpgsqlDataSource dataSource) : IRuntimeBindingRepository
{
    public async Task<TenantRuntimeBinding?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT enabled Enabled,default_orchestrator_id DefaultOrchestratorId,default_orchestrator_revision DefaultOrchestratorRevision,canary_user_ids::text CanaryUserIds FROM tenant_runtime_binding WHERE tenant_id=@tenantId",
            new { tenantId }, cancellationToken: ct));
        return row is null ? null : new(row.Enabled, row.DefaultOrchestratorId, row.DefaultOrchestratorRevision,
            JsonSerializer.Deserialize<string[]>(row.CanaryUserIds) ?? []);
    }
    public async Task<TenantRuntimeBinding> PutAsync(string tenantId, TenantRuntimeBinding binding, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenant_runtime_binding(tenant_id,enabled,default_orchestrator_id,default_orchestrator_revision,canary_user_ids,updated_at) VALUES(@tenantId,@enabled,@id,@revision,@users::jsonb,clock_timestamp()) ON CONFLICT(tenant_id) DO UPDATE SET enabled=EXCLUDED.enabled,default_orchestrator_id=EXCLUDED.default_orchestrator_id,default_orchestrator_revision=EXCLUDED.default_orchestrator_revision,canary_user_ids=EXCLUDED.canary_user_ids,updated_at=clock_timestamp()",
            new { tenantId, enabled = binding.Enabled, id = binding.DefaultOrchestratorId, revision = binding.DefaultOrchestratorRevision, users = JsonSerializer.Serialize(binding.CanaryUserIds) }, cancellationToken: ct));
        return binding;
    }
    private sealed record Row(bool Enabled, Guid? DefaultOrchestratorId, int? DefaultOrchestratorRevision, string CanaryUserIds);
}
