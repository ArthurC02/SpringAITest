using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

// 六個儲存庫 fake 已升格為 Backend.Api.Data.InMemory.InMemory*Repository(見 FakeRepositoryAliases.cs 的 re-export)。
// 本檔僅保留 Skill 驗證器 fake — 它取代的是對 workflow(:8001)的 HTTP 呼叫,不是資料層,故不升格。

/// <summary>
/// Skill 驗證器 fake(取代真的打 workflow :8001 的 POST /skills/validate)。
/// 記錄每一次呼叫供斷言;definition 含 <see cref="InvalidMarker"/> → valid=false 與兩個引擎錯誤碼,
/// 其餘一律通過並比照引擎回報 skill 中繼資料(以最陽春的逐行掃描取代真 YAML parser — 這是 fake 的工作)。
/// 以「內容觸發」而非可變旗標:fake 為 class fixture 共用,旗標會造成測試互相汙染。
/// </summary>
public sealed class FakeSkillValidator : ISkillValidator
{
    public const string InvalidMarker = "__invalid__";

    /// <summary>引擎不可達(WorkflowSkillValidator 對傳輸失敗/非 200 一律拋 502)。</summary>
    public const string EngineDownMarker = "__engine_down__";

    public sealed record Call(string Definition, string TenantId, string? UserId, string? Role);

    public List<Call> Calls { get; } = new();

    public Task<SkillValidationResult> ValidateAsync(
        string definition, string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(new Call(definition, tenantId, userId, role));
        }

        if (definition.Contains(EngineDownMarker, StringComparison.Ordinal))
        {
            throw new ApiException(502, "Skill 驗證服務呼叫失敗：連線被拒");
        }

        if (definition.Contains(InvalidMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(new SkillValidationResult(
                false,
                new[]
                {
                    new SkillValidationError("unbounded_loop", "loop 缺少 max_iterations", 7),
                    new SkillValidationError("unknown_node", "節點不存在：no_such_node", null),
                },
                null));
        }

        var meta = new SkillMetadata(
            Field(definition, "name") ?? "unnamed",
            Field(definition, "description") ?? string.Empty,
            Field(definition, "required_role") ?? "USER");
        return Task.FromResult(new SkillValidationResult(true, Array.Empty<SkillValidationError>(), meta));
    }

    /// <summary>取 YAML 頂層 `key: value` 的值(fake 專用的粗略掃描,不處理引號/巢狀)。</summary>
    private static string? Field(string definition, string key)
        => definition.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(line => line[(key.Length + 1)..].Trim())
            .FirstOrDefault(v => v.Length > 0);
}
