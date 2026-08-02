using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Common;
using Backend.Api.OperationsGovernance;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// 可控回應、可捕捉最後一次請求(含 body)的 HttpMessageHandler。三個對 workflow(:8001)出站的
/// client 測試(skill validate / skill validate-package / business-rules validate)共用同一份。
/// </summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    /// <summary>最常見的腳本:固定狀態碼 + JSON body。</summary>
    public static StubHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    public HttpRequestMessage? LastRequest { get; private set; }

    public string LastBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return responder(request);
    }

    public string Header(string name) => LastRequest!.Headers.GetValues(name).Single();
}

// 六個儲存庫 fake 已升格為 Backend.Api.Data.InMemory.InMemory*Repository(見 FakeRepositoryAliases.cs 的 re-export)。
// 本檔僅保留取代對 workflow(:8001)出站 HTTP 呼叫的 fake(Skill 驗證器、Eval runner)——不是資料層,故不升格。

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

    private readonly Lock _gate = new();

    public Task<SkillValidationResult> ValidateAsync(
        string definition, string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (_gate)
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
/// Agent Business Rule validator fake。rule id=invalid-rule 回定位錯誤；engine-down 模擬 request-time
/// dependency 失敗。所有呼叫都記錄 gate/identity，釘住 Agent validate/publish 皆用 pre-action。
/// </summary>
public sealed class FakeBusinessRuleValidator : IBusinessRuleValidator
{
    public sealed record Call(
        string Gate,
        JsonElement RuleSet,
        BusinessRuleReferenceCatalog ReferenceCatalog,
        string TenantId,
        string? UserId,
        string? Role);

    public List<Call> Calls { get; } = new();
    private readonly Lock _gate = new();
    private int _invalidOnPublishCalls;
    private int _invalidOnRestoreCalls;
    private int _engineDownOnRestoreCalls;
    private int _canonicalOnPublishCalls;
    private int _canonicalOnRestoreCalls;
    private ValidationRendezvous? _nextCalls;

    public void CoordinateNextCalls(int participants)
    {
        if (participants < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(participants));
        }

        if (Interlocked.CompareExchange(
                ref _nextCalls, new ValidationRendezvous(participants), null) is not null)
        {
            throw new InvalidOperationException("A Business Rule validation rendezvous is already active.");
        }
    }

    public Task<BusinessRuleValidationResult> ValidateAsync(
        string gate,
        JsonElement ruleSet,
        BusinessRuleReferenceCatalog referenceCatalog,
        string tenantId,
        string? userId,
        string? role,
        CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(new Call(gate, ruleSet.Clone(), referenceCatalog, tenantId, userId, role));
        }

        var raw = ruleSet.GetRawText();
        if (raw.Contains("\"engine-down\"", StringComparison.Ordinal))
        {
            throw new ApiException(502, "Business Rule 驗證服務呼叫失敗：連線被拒");
        }

        if (raw.Contains("\"invalid-rule\"", StringComparison.Ordinal))
        {
            return CompleteAsync(Invalid(), ct);
        }

        if (raw.Contains("\"invalid-on-publish\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _invalidOnPublishCalls) > 1)
        {
            return CompleteAsync(Invalid(), ct);
        }

        if (raw.Contains("\"invalid-on-restore\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _invalidOnRestoreCalls) > 2)
        {
            return CompleteAsync(Invalid(), ct);
        }

        if (raw.Contains("\"engine-down-on-restore\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _engineDownOnRestoreCalls) > 2)
        {
            throw new ApiException(502, "Business Rule 驗證服務呼叫失敗：連線被拒");
        }

        if (raw.Contains("\"canonical-on-publish\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _canonicalOnPublishCalls) > 1)
        {
            var canonical = JsonNode.Parse(raw)!.AsObject();
            canonical["rules"]![0]!["onUnknown"] = new JsonArray(
                new JsonObject { ["action"] = "deny" });
            using var doc = JsonDocument.Parse(canonical.ToJsonString());
            return CompleteAsync(new BusinessRuleValidationResult(
                true,
                doc.RootElement.Clone(),
                Array.Empty<BusinessRuleValidationError>()), ct);
        }

        if (raw.Contains("\"canonical-on-restore\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _canonicalOnRestoreCalls) > 2)
        {
            var canonical = JsonNode.Parse(raw)!.AsObject();
            canonical["rules"]![0]!["onUnknown"] = new JsonArray(
                new JsonObject { ["action"] = "deny" });
            using var doc = JsonDocument.Parse(canonical.ToJsonString());
            return CompleteAsync(new BusinessRuleValidationResult(
                true,
                doc.RootElement.Clone(),
                Array.Empty<BusinessRuleValidationError>()), ct);
        }

        if (raw.Contains("\"needs-canonical-default\"", StringComparison.Ordinal))
        {
            var canonical = JsonNode.Parse(raw)!.AsObject();
            canonical["rules"]![0]!["onUnknown"] = "deny";
            using var doc = JsonDocument.Parse(canonical.ToJsonString());
            return CompleteAsync(new BusinessRuleValidationResult(
                true,
                doc.RootElement.Clone(),
                Array.Empty<BusinessRuleValidationError>()), ct);
        }

        return CompleteAsync(new BusinessRuleValidationResult(
            true,
            ruleSet.Clone(),
            Array.Empty<BusinessRuleValidationError>()), ct);
    }

    private Task<BusinessRuleValidationResult> CompleteAsync(
        BusinessRuleValidationResult result, CancellationToken ct)
    {
        var rendezvous = Volatile.Read(ref _nextCalls);
        if (rendezvous is null)
        {
            return Task.FromResult(result);
        }

        if (rendezvous.Arrive())
        {
            Interlocked.CompareExchange(ref _nextCalls, null, rendezvous);
        }

        return AwaitReleaseAsync(rendezvous, result, ct);
    }

    private static async Task<BusinessRuleValidationResult> AwaitReleaseAsync(
        ValidationRendezvous rendezvous,
        BusinessRuleValidationResult result,
        CancellationToken ct)
    {
        await rendezvous.Released.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        return result;
    }

    private static BusinessRuleValidationResult Invalid()
        => new(
            false,
            null,
            new[]
            {
                new BusinessRuleValidationError(
                    "$.ruleSet.rules[0].when",
                    "operator_type_mismatch",
                    "number fact 不可使用 string operator"),
            });

    private sealed class ValidationRendezvous
    {
        private int _remaining;

        public ValidationRendezvous(int participants) => _remaining = participants;

        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Arrive()
        {
            var remaining = Interlocked.Decrement(ref _remaining);
            if (remaining < 0)
            {
                throw new InvalidOperationException("Too many validation calls reached the rendezvous.");
            }
            if (remaining != 0)
            {
                return false;
            }

            Released.TrySetResult();
            return true;
        }
    }
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
    private readonly Lock _gate = new();
    private const string DerivedNameKey = "\0server-derived";

    /// <summary>腳本化某 name 的驗證結果(依上傳 bytes 動態產生,便於 flow round-trip 回傳解出的 skill.yaml)。</summary>
    public void Setup(string expectedName, Func<byte[], SkillPackageValidationResult> responder)
    {
        lock (_gate)
        {
            _scripts[expectedName] = responder;
        }
    }

    public void SetupDerived(Func<byte[], SkillPackageValidationResult> responder)
        => Setup(DerivedNameKey, responder);

    /// <summary>模擬引擎不可達 / 5xx / timeout:validator 一律拋 ApiException(502)。</summary>
    public void SetupUnreachable(string expectedName)
    {
        lock (_gate)
        {
            _unreachable.Add(expectedName);
        }
    }

    public Task<SkillPackageValidationResult> ValidatePackageAsync(
        byte[] package, string fileName, string? expectedName,
        string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(new Call(expectedName, tenantId, userId, role, package));
        }

        lock (_gate)
        {
            if (_unreachable.Contains(expectedName ?? DerivedNameKey))
            {
                throw new ApiException(502, "Skill 套件驗證服務呼叫失敗：連線被拒");
            }
        }

        Func<byte[], SkillPackageValidationResult>? responder;
        lock (_gate)
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

/// <summary>
/// Eval runner fake(取代真的打 workflow :8001 的 POST /evals/run)。預設對每個轉送過去的 case_id
/// 回一個 PASS(對「required case 全 PASS」的 happy path 最方便);測試需要 FAIL/ERROR 或 502 時呼叫
/// <see cref="Setup"/> / <see cref="SetupUnreachable"/> 覆寫。記錄每次呼叫供斷言。
/// </summary>
public sealed class FakeEvalRunner : IEvalRunner
{
    public sealed record Call(string SuiteId, int Revision, string CandidateKind, string TenantId, int? BudgetMs);

    public List<Call> Calls { get; } = new();

    private readonly Lock _gate = new();
    private Func<string, int, JsonElement, EvalRunResponseWire>? _script;
    private bool _unreachable;

    public void Setup(Func<string, int, JsonElement, EvalRunResponseWire> responder)
    {
        _script = responder;
        _unreachable = false;
    }

    public void SetupUnreachable() => _unreachable = true;

    public void ClearUnreachable() => _unreachable = false;

    public Task<EvalRunResponseWire> RunAsync(
        string suiteId, int revision, JsonElement cases, string candidateKind, JsonElement candidateRef,
        JsonElement? candidatePins, int? budgetMs, string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(new Call(suiteId, revision, candidateKind, tenantId, budgetMs));
        }

        if (_unreachable)
        {
            throw new ApiException(502, "Eval runner 呼叫失敗：連線被拒");
        }

        if (_script is not null)
        {
            return Task.FromResult(_script(suiteId, revision, cases));
        }

        var now = DateTimeOffset.UtcNow;
        var caseIds = cases.ValueKind == JsonValueKind.Array
            ? cases.EnumerateArray().Select(c => c.GetProperty("case_id").GetString()!).ToArray()
            : Array.Empty<string>();
        var results = caseIds
            .Select(id => new EvalCaseResultWire(id, "identity-" + id, "PASS", null, null))
            .ToArray();
        return Task.FromResult(new EvalRunResponseWire("fake-runner-1", suiteId, revision, now, now.AddSeconds(1), results));
    }
}
