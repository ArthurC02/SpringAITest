using System.Text.Json;

namespace Backend.Api.Agents;

/// <summary>
/// Workflow-owned Business Rule validator boundary. Backend owns durable Agent drafts/revisions but never
/// duplicates Rule AST semantics. Agent v1 stores one rule set without a gate; D2 validates it at the
/// conservative pre-action gate. A future multi-gate persisted shape requires an AST version change.
/// </summary>
public interface IBusinessRuleValidator
{
    Task<BusinessRuleValidationResult> ValidateAsync(
        string gate,
        JsonElement ruleSet,
        BusinessRuleReferenceCatalog referenceCatalog,
        string tenantId,
        string? userId,
        string? role,
        CancellationToken ct);
}

/// <summary>
/// Agent-owned references that Rule actions may narrow but never expand.
/// Supplying explicit (including empty) collections makes Workflow validation fail closed.
/// </summary>
public sealed record BusinessRuleReferenceCatalog(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Tools);

public sealed record BusinessRuleValidationResult(
    bool Valid,
    JsonElement? CanonicalRuleSet,
    IReadOnlyList<BusinessRuleValidationError> Errors);

public sealed record BusinessRuleValidationError(string Path, string Code, string Message);
