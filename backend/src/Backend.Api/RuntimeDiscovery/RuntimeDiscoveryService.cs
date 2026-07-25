using System.Text.Json;
using Backend.Api.Agents;
using Backend.Api.Common;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Orchestrators;

namespace Backend.Api.RuntimeDiscovery;

public sealed class RuntimeDiscoveryService(
    IOrchestratorRepository orchestrators,
    IRuntimeBindingRepository bindings,
    IOrchestratorRunRepository runs)
{
    public async Task<IReadOnlyList<RuntimeOrchestratorSummary>> ListAsync(string tenant, string role, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        var result = new List<RuntimeOrchestratorSummary>();
        foreach (var candidate in await orchestrators.ListAsync(tenant, ct))
        {
            var active = await ActiveAsync(tenant, candidate.Id, null, role, groups, ct);
            if (active.Status == ActiveStatus.Ready && active.Summary is not null) result.Add(active.Summary);
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
    }

    public async Task<RuntimeResolveResponse> ResolveAsync(string tenant, string user, string role, IReadOnlyCollection<string> groups, Guid? requestedId, CancellationToken ct)
    {
        if (requestedId is Guid id)
        {
            var explicitResult = await ActiveAsync(tenant, id, null, role, groups, ct);
            return explicitResult.Status switch
            {
                ActiveStatus.Ready => new("orchestrator", explicitResult.Summary),
                ActiveStatus.Missing or ActiveStatus.Forbidden => throw new ApiException(404, "Orchestrator not found"),
                _ => throw new ApiException(409, "Orchestrator is not active"),
            };
        }

        var binding = await bindings.GetAsync(tenant, ct);
        if (binding is null || !binding.Enabled || binding.DefaultOrchestratorId is null
            || binding.DefaultOrchestratorRevision is null || !binding.CanaryUserIds.Contains(user, StringComparer.Ordinal))
            return new("legacy");

        var selected = await ActiveAsync(tenant, binding.DefaultOrchestratorId.Value,
            binding.DefaultOrchestratorRevision, role, groups, ct);
        return selected.Status == ActiveStatus.Ready
            ? new("orchestrator", selected.Summary)
            : throw new ApiException(409, "Tenant default Orchestrator is not active");
    }

    public async Task<ChatRunResponse> StartAsync(string tenant, string user, string role, IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilities, ChatRunStartRequest request, string key, CancellationToken ct)
    {
        var message = Required(request.Message, "message", 16_384);
        var conversation = Required(request.ConversationId, "conversation_id", 128);
        var resolved = await ResolveAsync(tenant, user, role, groups, request.OrchestratorId, ct);
        if (resolved.Mode != "orchestrator" || resolved.Orchestrator is null)
            throw new ApiException(409, "No active tenant Orchestrator is selected");
        var created = await runs.CreateAsync(tenant, user, role, groups, capabilities, resolved.Orchestrator.Id,
            conversation, message, key, ct);
        if (created.Status == OrchestratorRunWriteStatus.NotFound) throw new ApiException(404, "Orchestrator not found");
        if (created.Status is OrchestratorRunWriteStatus.Conflict or OrchestratorRunWriteStatus.InvalidState)
            throw new ApiException(409, created.Message ?? "Chat root run state conflict");
        var run = created.Run ?? throw new InvalidOperationException("Root run write returned no run");
        return new("orchestrator", run with { CommandId = created.Dispatch?.CommandId ?? run.CommandId }, created.Dispatch?.CommandId ?? run.CommandId, created.Replayed);
    }

    public async Task<TenantRuntimeBinding> PutBindingAsync(string tenant, TenantRuntimeBindingUpsert request, CancellationToken ct)
        => await bindings.PutAsync(tenant, ValidateBinding(request), ct);

    public TenantRuntimeBinding ValidateBinding(TenantRuntimeBindingUpsert request)
    {
        var users = (request.CanaryUserIds ?? []).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 128 && !x.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (users.Length > 1024) throw new ApiException(400, "canary_user_ids is too large");
        if (request.Enabled && (request.DefaultOrchestratorId is null || request.DefaultOrchestratorRevision is null))
            throw new ApiException(400, "enabled binding requires a pinned default Orchestrator revision");
        if (request.DefaultOrchestratorRevision is <= 0) throw new ApiException(400, "default_orchestrator_revision is invalid");
        return new(request.Enabled, request.DefaultOrchestratorId, request.DefaultOrchestratorRevision, users);
    }

    private async Task<Active> ActiveAsync(string tenant, Guid id, int? requiredRevision, string role, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        var aggregate = await orchestrators.GetAsync(tenant, id, ct);
        if (aggregate is null) return new(ActiveStatus.Missing);
        if (!aggregate.Enabled || aggregate.PublishedRevision is not int revision || (requiredRevision is not null && requiredRevision != revision)) return new(ActiveStatus.Inactive);
        var definition = await orchestrators.RevisionAsync(tenant, id, revision, ct);
        if (definition is null) return new(ActiveStatus.Inactive);
        try
        {
            using var document = JsonDocument.Parse(definition);
            var root = document.RootElement;
            var audience = root.GetProperty("audience").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (!AgentAudience.Matches(audience, role, groups, allowLegacyPublishedRoles: true)) return new(ActiveStatus.Forbidden);
            var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal).ToArray();
            return new(ActiveStatus.Ready, new(id, aggregate.Name, aggregate.Description, revision, capabilities));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(ActiveStatus.Inactive);
        }
    }

    private static string Required(string? value, string field, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > max || value.Any(char.IsControl)) throw new ApiException(400, $"{field} is required");
        return value;
    }
    private enum ActiveStatus { Ready, Missing, Inactive, Forbidden }
    private sealed record Active(ActiveStatus Status, RuntimeOrchestratorSummary? Summary = null);
}
