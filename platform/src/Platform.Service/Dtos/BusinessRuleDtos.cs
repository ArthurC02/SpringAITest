using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Service.Dtos;

/// <summary>Standalone Rule validation request. Gate is explicit because the editor may explore every policy gate.</summary>
public sealed record BusinessRuleValidateRequest(
    [property: JsonPropertyName("gate")] string? Gate,
    [property: JsonPropertyName("ruleSet")] JsonElement? RuleSet);

/// <summary>Dry-run request; facts are caller-supplied simulation data and never grant runtime authority.</summary>
public sealed record BusinessRuleSimulateRequest(
    [property: JsonPropertyName("gate")] string? Gate,
    [property: JsonPropertyName("ruleSet")] JsonElement? RuleSet,
    [property: JsonPropertyName("facts")] JsonElement? Facts);
