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

        // 忠實鏡射真 workflow 的 definition-only /skills/validate:宣告 `kind: agentic` 的定義一律 invalid
        // (agentic 只能走 import);此端點不會把 agentic 當成合法 metadata 回傳。
        if (string.Equals(Field(definition, "kind"), "agentic", StringComparison.Ordinal))
        {
            return Task.FromResult(new SkillValidationResult(
                false,
                new[] { new SkillValidationError("agentic_requires_import", "agentic Skill 僅能透過匯入(import)建立", null) },
                null));
        }

        // 真 /skills/validate 不回 kind → 合法結果一律 flow。
        var meta = new SkillMetadata(
            Field(definition, "name") ?? "unnamed",
            Field(definition, "description") ?? string.Empty,
            Field(definition, "required_role") ?? "USER",
            "flow");
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

/// <summary>
/// Agent Skill package 驗證器 fake(取代真的打 workflow :8001 的 POST /skills/validate-package)。
/// 以 expected_name 為鍵腳本化回應(每個測試用自己的 skill 名 → class fixture 共用不互汙染)。
/// 記錄每次呼叫(bytes/expected_name/身分)供斷言。未腳本化的 name 一律拋(逼測試顯式 Setup)。
/// </summary>
public sealed class FakeSkillPackageValidator : ISkillPackageValidator
{
    public sealed record Call(
        string? ExpectedName, string TenantId, string? UserId, string? Role, byte[] Package);

    public List<Call> Calls { get; } = new();

    private readonly Dictionary<string, Func<byte[], SkillPackageValidationResult>> _scripts = new();
    private readonly HashSet<string> _unreachable = new();
    private const string DerivedNameKey = "\0server-derived";

    /// <summary>腳本化某 name 的驗證結果(依上傳 bytes 動態產生,便於 flow round-trip 回傳解出的 skill.yaml)。</summary>
    public void Setup(string expectedName, Func<byte[], SkillPackageValidationResult> responder)
    {
        lock (_scripts)
        {
            _scripts[expectedName] = responder;
        }
    }

    public void SetupDerived(Func<byte[], SkillPackageValidationResult> responder)
        => Setup(DerivedNameKey, responder);

    /// <summary>模擬引擎不可達 / 5xx / timeout:validator 一律拋 ApiException(502)。</summary>
    public void SetupUnreachable(string expectedName)
    {
        lock (_unreachable)
        {
            _unreachable.Add(expectedName);
        }
    }

    public Task<SkillPackageValidationResult> ValidatePackageAsync(
        byte[] package, string fileName, string? expectedName,
        string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(new Call(expectedName, tenantId, userId, role, package));
        }

        lock (_unreachable)
        {
            if (_unreachable.Contains(expectedName ?? DerivedNameKey))
            {
                throw new ApiException(502, "Skill 套件驗證服務呼叫失敗：連線被拒");
            }
        }

        Func<byte[], SkillPackageValidationResult>? responder;
        lock (_scripts)
        {
            _scripts.TryGetValue(expectedName ?? DerivedNameKey, out responder);
        }

        if (responder is null)
        {
            throw new InvalidOperationException(
                $"FakeSkillPackageValidator 未腳本化 expected_name={expectedName};請先呼叫 Setup/SetupUnreachable。");
        }

        return Task.FromResult(responder(package));
    }
}
