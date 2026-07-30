using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service;

/// <summary>
/// 兩條聊天鏈路(<c>/api/chat*</c> 與 AG-UI 副駕)共用的 deterministic prompt assembler
/// (plans/agent-architecture-improvements/03-prompt-model-runtime-plan.md §3.2)。四個共享槽位
/// (guard / memory_policy / routing / summary)是「同一份 identity」,persona 依 transport 各自對應:
/// AG-UI 有操作助理 persona,<c>/api/chat*</c> 刻意沒有(<see cref="Persona"/> 為 null)。
///
/// 常數即預設來源:旗標關閉、租戶未加入 canary、或 manifest 解析 fail closed 時都用這一份,
/// 且 <see cref="Instructions"/> 的組法(persona 在前、換行、再 guard、再 mem0 前綴)與改造前
/// 「framework 把 agent instructions 換行接上 <c>AIContext.Instructions</c>」的輸出逐位元相同
/// (實測 Microsoft.Agents.AI 1.13.0:<c>agentInstructions + "\n" + contextInstructions</c>)。
/// </summary>
public sealed record PromptComposition(
    string Guard,
    string MemoryPrefix,
    string Routing,
    string Summary,
    string? Persona)
{
    /// <summary>每輪都注入的固定護欄(manifest kind <c>guard</c>)。</summary>
    public const string GuardDefault =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    /// <summary>mem0 記憶注入的固定前綴(manifest kind <c>memory_policy</c> —— platform 的 memory policy 就是這段前綴)。</summary>
    public const string MemoryPrefixDefault =
        "以下是你先前記住、關於這位使用者的長期記憶，回答時可參考（與當前問題無關者請忽略）：";

    /// <summary>路由指令:LLM 只做「選工具」,只輸出工具名稱或 NONE(manifest kind <c>routing</c>)。</summary>
    public const string RoutingDefault =
        "你是一個路由器。以下是可用工具，每行「名稱: 說明」。判斷使用者訊息最適合哪一個工具，只輸出那個工具的名稱（原樣、不加任何其他字）；若只是閒聊、打招呼、或不需要查資料／計算，只輸出 NONE。"
        + "若清單中有『說明明確對應到這個問題主題』的專門工具，優先選它；通用的知識庫檢索工具（例如一般文件問答）只有在沒有更專門的工具時才選。"
        // 與護欄同一組數字語義:純聊天兜底會把這類問題判成「查無此數據」,所以路由這一關就必須把它們導向工具,否則等於沒答。
        + "特別注意：若使用者問題涉及數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，幾乎都需要專門工具查證,只要清單中有說明相符的工具就選它,不要因為題目像在算數學就輸出 NONE。"
        + "務必只輸出一個工具名稱或 NONE，不要多餘文字。";

    /// <summary>摘要指令:只把工具的確定性結果改寫成自然語言,嚴禁竄改數字(manifest kind <c>summary</c>)。</summary>
    public const string SummaryDefault =
        "把以下『工具結果』改寫成給使用者的自然、完整中文回覆。數字、金額、比率、百分比一字都不得更改、刪除或新增，只做語言潤飾與說明。若工具結果表示查無資料或發生錯誤，如實轉達，不要編造。";

    /// <summary>
    /// AG-UI 副駕的預設 persona(manifest kind <c>persona</c>)——唯一有 persona 的鏈路,<c>/api/chat*</c>
    /// 刻意沒有。Program.cs 與 golden 測試共用這一份常數,不逐字重抄。
    /// </summary>
    public const string CopilotPersonaDefault =
        "你是本系統的操作助理,協助使用者操作這個 AI 資料檢索與分析平台:" +
        "查詢與管理文件、執行工作流(例如檢索式問答)、查看分析摘要、切換視圖。" +
        "請一律以繁體中文回答,簡潔專業。當使用者的請求需要實際操作時," +
        "呼叫前端提供的工具(client tools)來完成;你只需正常回答並在需要時呼叫收到的工具。";

    /// <summary>constants 版組成;<paramref name="transportPersona"/> 為該鏈路的 persona(chat 傳 null)。</summary>
    public static PromptComposition Defaults(string? transportPersona) =>
        new(GuardDefault, MemoryPrefixDefault, RoutingDefault, SummaryDefault, transportPersona);

    /// <summary>
    /// 本輪要注入 <c>AIContext.Instructions</c> 的系統提示:persona(若該鏈路有)+ 護欄 + mem0 記憶。
    /// <paramref name="memories"/> 空白視為無長期記憶(不接空前言)。
    /// </summary>
    public string Instructions(string? memories)
    {
        var instructions = string.IsNullOrEmpty(Persona) ? Guard : Persona + "\n" + Guard;
        return string.IsNullOrWhiteSpace(memories)
            ? instructions
            : instructions + "\n" + MemoryPrefix + "\n" + memories;
    }

    /// <summary>組成 identity(shadow 比較與 log 用;只出現 hash,原文絕不進 log)。</summary>
    public string Sha256()
    {
        // NUL 分隔:槽位邊界不可能與內容混淆(空白或換行會)。
        var material = string.Join('\0', Guard, MemoryPrefix, Routing, Summary, Persona ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// P1 manifest-aware 組成解析(計畫 03 §3/§7)。只在 <c>PROMPT_ARTIFACTS_ENABLED=true</c> 時由
/// Program.cs 註冊成 Singleton —— 旗標關閉時這個服務根本不存在,兩條鏈路 <c>GetService</c> 取到 null
/// 直接用 <see cref="PromptComposition.Defaults"/>,故「旗標關閉逐位元不變」是結構保證而非分支保證。
///
/// 選定機制就是 tenant canary:讀該租戶的 <c>prompt.manifest_revision</c>(ADMIN 撰寫的整數,經
/// backend <c>GET /api/config/runtime/{key}</c> 執行期單鍵讀取,身分不帶角色 —— 見 <see cref="ServerRead"/>),
/// 未設定/空 → 該租戶維持 constants;設定了 → 取 backend 的 resolved manifest 並以對應 kind 取代槽位。
/// resolved manifest 以 <c>(tenant, revision)</c> 快取(上限 <c>MaxCachedManifests</c>,超出後 FIFO 淘汰
/// 最舊一筆),manifest revision 不可變故無失效機制。
///
/// **Fail closed 的方向是「回 constants」而不是「拒絕服務」**:legacy chat 沒有 snapshot pin 的承諾,
/// 可用性優先。失敗依性質分兩類:傳輸例外/backend 5xx 是暫時性,一律 Error 級別記錄且不快取(下一輪
/// 全新重試);404/schema drift/驗證失敗(<see cref="Verify"/> 的每一種拒收)/config 值不是正整數則是
/// 永久性(同一個不可變 revision、或同一個壞掉的設定值不會自己變好)——降為 Warning 級別記錄,並以
/// <c>(tenant, revision)</c> 為鍵短 TTL 負向快取(<c>NegativeCacheTtl</c>),同一輪或短期內的下一輪不重打
/// backend。這與 workflow pinned-run 的語意刻意不同(那邊 pin 不到就必須失敗),差異僅存在於本層。
///
/// 一輪(一個 HTTP request)最多只實際解析一次:呼叫端傳入 <see cref="IChatIdentityAccessor"/> 時,結果
/// (含「解析失敗、已改用 constants」這個結果本身)寫回 <see cref="IChatIdentityAccessor.PromptManifestCache"/>,
/// 同一輪內第二個消費點(<c>SkillRoutingAgent</c> 的路由/摘要、<c>ChatContextProvider</c> 的護欄/persona)
/// 直接複用,不再重讀 config/backend,也避免同一輪內半用 manifest 半用 constants 或混用不同 revision。
///
/// ponytail: <c>manifest_sha256</c> 只驗形狀不重算 —— 重算要在 platform 複製第三套 canonical JSON
/// 序列化(計畫 03 §3.1 明文禁止);真正餵給模型的位元組由逐 component 的 <c>content_sha256</c>
/// 重算保護。升級路徑:backend 若在 resolved 回應附上 canonical manifest 原文,直接 hash 該原文比對。
/// </summary>
public sealed class PromptCompositionResolver
{
    /// <summary>ADMIN 撰寫的 tenant 設定鍵;值是 manifest revision 整數。</summary>
    public const string ManifestRevisionConfigKey = "prompt.manifest_revision";

    private const int MaxCachedManifests = 64;
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);

    // (tenant, revision) → 已驗證的 manifest components;manifest revision 不可變,快取無需失效,
    // 只需上限淘汰。
    private readonly ConcurrentDictionary<(string Tenant, int Revision), ResolvedManifest> _cache = new();
    private readonly ConcurrentQueue<(string Tenant, int Revision)> _cacheOrder = new();
    // (tenant, revision) → 上次判定為永久失敗的時間;TTL 內同一個 revision 不再重打 backend。
    private readonly ConcurrentDictionary<(string Tenant, int Revision), DateTimeOffset> _negativeCache = new();
    private readonly ConcurrentQueue<(string Tenant, int Revision)> _negativeCacheOrder = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _shadow;
    private readonly ILogger<PromptCompositionResolver> _logger;
    private readonly TimeProvider _timeProvider;

    /// <param name="shadow">
    /// <c>PROMPT_ARTIFACTS_SHADOW</c>:兩種組成都算、只比 hash、mismatch 記 warning,實際仍用 constants。
    /// </param>
    /// <param name="timeProvider">負向快取 TTL 的時鐘;省略時用 <see cref="TimeProvider.System"/>,不引入新依賴。</param>
    public PromptCompositionResolver(
        IServiceScopeFactory scopeFactory, bool shadow, ILogger<PromptCompositionResolver> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _shadow = shadow;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 本輪(新 chat turn / 新 AG-UI turn)的有效組成。<paramref name="tenant"/> 取自已驗證身分
    /// (匿名為 null → 沒有租戶設定可讀,直接 constants,連 backend 都不打)。
    /// <paramref name="transportPersona"/> 是該鏈路的 persona 預設值:為 null 的鏈路(chat)永不從 manifest
    /// 取得 persona —— 「chat 沒有 persona」是既有的 transport 差異,manifest 只替換已存在的槽位。
    /// <paramref name="identity"/> 非 null 時,本輪(見類別文件)只實際解析一次;null(如既有單元測試
    /// 直接呼叫本方法)則每次呼叫都全新解析,行為與改動前相同。
    /// 本方法不會拋例外(除了呼叫端自己的取消),任何失敗都退成 constants。
    /// </summary>
    public async Task<PromptComposition> ResolveAsync(
        string? tenant, string? transportPersona, IChatIdentityAccessor? identity = null,
        CancellationToken ct = default)
    {
        var constants = PromptComposition.Defaults(transportPersona);
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return constants;
        }

        var manifest = await ResolveManifestAsync(tenant, identity, ct);
        if (manifest is null)
        {
            return constants;   // 該租戶未設定 manifest revision,或本輪解析已 fail closed 到 constants。
        }

        if (!HasAnyPlatformSlot(manifest))
        {
            // 低10:manifest 驗證通過卻不含任何 platform 使用的槽位(例如只有 workflow 專用的
            // governance_frame)——組成其實逐位元等於 constants;不是錯誤,但值得讓 ops 注意到。
            _logger.LogWarning(
                "prompt manifest 未包含任何 platform 使用的槽位（guard/memory_policy/routing/summary/persona），"
                + "組成實際上等同 constants：tenant={Tenant} revision={Revision}",
                tenant, manifest.Revision);
        }

        var composed = Compose(manifest, constants);
        if (!_shadow)
        {
            return composed;
        }

        // Shadow:只比 hash,不 log 任何 prompt 原文。
        var composedSha = composed.Sha256();
        var constantsSha = constants.Sha256();
        if (!string.Equals(composedSha, constantsSha, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "prompt manifest shadow 組成與 constants 不一致（仍使用 constants）："
                + "revision={Revision} manifest_sha256={ManifestSha256} "
                + "composition_sha256={ComposedSha} constants_sha256={ConstantsSha}",
                manifest.Revision, manifest.ManifestSha256, composedSha, constantsSha);
        }

        return constants;
    }

    /// <summary>
    /// 本輪只解析一次(<paramref name="identity"/> 非 null 時):第一個消費點解析(無論成功或失敗)後
    /// 把結果寫回 <see cref="IChatIdentityAccessor.PromptManifestCache"/>,第二個消費點直接複用,
    /// 不再重讀 config/backend。
    /// </summary>
    private async Task<ResolvedManifest?> ResolveManifestAsync(
        string tenant, IChatIdentityAccessor? identity, CancellationToken ct)
    {
        var cached = identity?.PromptManifestCache;
        if (cached is not null)
        {
            return cached.Manifest;
        }

        ResolvedManifest? manifest;
        try
        {
            manifest = await LoadAsync(tenant, ct);
        }
        catch (PermanentManifestException ex)
        {
            if (ex.Revision is int failedRevision)
            {
                AddNegative((tenant, failedRevision));
            }
            _logger.LogWarning(
                ex,
                "prompt manifest 解析失敗（永久性，{TTLSeconds}s 內暫停重試），本輪改用內建 constants 組成：{訊息}",
                (int)NegativeCacheTtl.TotalSeconds, ex.Message);
            manifest = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // fail closed = 回 constants(見類別註解);error 級別,因為這是暫時性的下游/傳輸異常。
            _logger.LogError(ex, "prompt manifest 解析失敗，本輪改用內建 constants 組成：{訊息}", ex.Message);
            manifest = null;
        }

        if (identity is not null)
        {
            identity.PromptManifestCache = new PromptManifestResolutionCache(manifest);
        }

        return manifest;
    }

    /// <summary>null = 該租戶未設定;拋 <see cref="TransientManifestException"/>/<see cref="PermanentManifestException"/> = fail closed。</summary>
    private async Task<ResolvedManifest?> LoadAsync(string tenant, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var revision = await ReadRevisionAsync(scope.ServiceProvider, tenant, ct);
        if (revision is null)
        {
            return null;
        }

        // 快取命中就不再打 backend(manifest revision 不可變);config 仍每輪讀,canary 開關才能即時生效。
        if (_cache.TryGetValue((tenant, revision.Value), out var cached))
        {
            return cached;
        }

        // 永久失敗負向快取:同一個 (tenant, revision) 短期內已驗證失敗過,不再重打 backend(revision
        // 一旦發布即不可變,不會自己「變好」;TTL 是保險絲,萬一當初的失敗其實是網路瞬斷被誤判)。
        // 中1:這裡的 revision 傳 null(而非 revision.Value)——TTL 是滑動窗口,若帶 revision 會讓
        // ResolveManifestAsync 的 catch 再次 AddNegative,把 failedAt 續期並再 Enqueue 一次;
        // 聊天間隔 < TTL 的租戶就永遠命中負向快取、rollback 補救後還得重啟 platform 才生效,
        // 佇列也隨每次命中無上限成長。revision:null → catch 端不 AddNegative,TTL 只從「真正驗證
        // 失敗那一刻」算起,過期後自然重試。
        var negativeKey = (tenant, revision.Value);
        if (_negativeCache.TryGetValue(negativeKey, out var failedAt)
            && _timeProvider.GetUtcNow() - failedAt < NegativeCacheTtl)
        {
            throw new PermanentManifestException(null, "近期已驗證失敗過，短 TTL 內暫停重試");
        }

        // resolved manifest 路由只要 internal token + X-Tenant-Id(backend PromptManifestResolutionController
        // 刻意不是 ADMIN Builder 路由),故這裡用最小權限的身分:只有租戶,沒有角色。
        var backend = scope.ServiceProvider.GetRequiredService<BackendClient>();
        var dto = await backend.SendForJsonAsync<ResolvedManifestDto>(
            backend.BuildRequest(
                HttpMethod.Get,
                $"/api/prompt-manifests/{revision.Value}/resolved",
                new UserContext(string.Empty, tenant, string.Empty)),
            ex => new TransientManifestException("讀取 resolved prompt manifest 失敗（傳輸層）：" + ex.Message),
            (resp, _) => Task.FromResult<Exception>(
                (int)resp.StatusCode >= 500
                    ? new TransientManifestException($"讀取 resolved prompt manifest 失敗：HTTP {(int)resp.StatusCode}")
                    : new PermanentManifestException(
                        revision.Value, $"讀取 resolved prompt manifest 失敗：HTTP {(int)resp.StatusCode}")),
            () => new PermanentManifestException(revision.Value, "resolved prompt manifest 回應為空"),
            ct);

        var verified = Verify(dto, revision.Value);

        var stored = _cache.GetOrAdd((tenant, revision.Value), verified);
        if (ReferenceEquals(stored, verified))
        {
            // ponytail: ConcurrentQueue 的入列順序未必與淘汰精確對齊(並發下),但快取只是加速,
            // miss 就重新驗證一次,不影響正確性。
            if (_cache.Count > MaxCachedManifests && _cacheOrder.TryDequeue(out var oldest))
            {
                _cache.TryRemove(oldest, out _);
            }
            _cacheOrder.Enqueue((tenant, revision.Value));

            // 唯一的「正常路徑」log,每個 (tenant, revision) 只會出現一次(GetOrAdd 命中既有值時不重複):
            // rollout 時要看得出 canary 生效。shadow 模式下 manifest 沒有真的套用(仍用 constants),
            // 措辭不得說「生效」。只帶 identity,不帶任何 component 原文。
            _logger.LogInformation(
                _shadow
                    ? "prompt manifest shadow 觀測中（實際使用 constants）：tenant={Tenant} revision={Revision} manifest_sha256={ManifestSha256} kinds={Kinds}"
                    : "prompt manifest 生效：tenant={Tenant} revision={Revision} manifest_sha256={ManifestSha256} kinds={Kinds}",
                tenant, verified.Revision, verified.ManifestSha256, string.Join(',', verified.ByKind.Keys));
        }

        return stored;
    }

    /// <summary>測試可見性(中1):負向快取佇列長度——只在真正 <see cref="AddNegative"/> 時增長,
    /// 命中負向快取不得推動它,否則長期運行下無上限成長。</summary>
    internal int NegativeCacheOrderCount => _negativeCacheOrder.Count;

    private void AddNegative((string Tenant, int Revision) key)
    {
        var now = _timeProvider.GetUtcNow();
        // 已在快取中:只續期時間,不重複入佇列,避免佇列無限成長。
        if (!_negativeCache.TryAdd(key, now)) { _negativeCache[key] = now; return; }
        // 新 key:檢查容量,超限則淘汰最舊的。
        if (_negativeCache.Count > MaxCachedManifests && _negativeCacheOrder.TryDequeue(out var oldest))
        {
            _negativeCache.TryRemove(oldest, out _);
        }
        _negativeCacheOrder.Enqueue(key);
    }

    /// <summary>讀 tenant 設定的 manifest revision;未設定/空 → null,值不合法 → 拋 <see cref="PermanentManifestException"/>(fail closed)。</summary>
    private static async Task<int?> ReadRevisionAsync(
        IServiceProvider scoped, string tenant, CancellationToken ct)
    {
        var item = await scoped.GetRequiredService<IConfigService>()
            .GetRuntimeAsync(ManifestRevisionConfigKey, ServerRead(tenant), ct);
        var value = item?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), out var revision) && revision > 0
            ? revision
            : throw new PermanentManifestException(
                null, $"{ManifestRevisionConfigKey} 不是正整數 revision（長度 {value.Trim().Length}）");
    }

    /// <summary>
    /// 讀 <c>/api/config/runtime/{key}</c> 用的身分:該路由刻意不是 ADMIN-only(執行期讀取,不是
    /// authoring),不需要合成 ADMIN 角色 —— 空角色即可通過 backend 的 RequireTenant() 信任邊界。
    /// 租戶一律取自已驗證身分(匿名沒有租戶 → 根本不讀),讀到的值只用來決定伺服器端 prompt 組成,
    /// 永不回傳瀏覽器;<c>/api/prompt-manifests*</c>(唯一會回傳 raw component content 的路由)刻意
    /// 沒有任何 platform 代理。
    /// </summary>
    private static UserContext ServerRead(string tenant) => new(string.Empty, tenant, string.Empty);

    /// <summary>
    /// 信任邊界驗證:schema version、manifest SHA 形狀、逐 component content SHA 重算。每一種拒收都是
    /// 永久性(同一個不可變 revision 不會自己驗證通過),掛 revision 供負向快取鍵用。
    /// </summary>
    private static ResolvedManifest Verify(ResolvedManifestDto dto, int requestedRevision)
    {
        if (dto.SchemaVersion != PromptManifestSchemaVersion)
        {
            throw new PermanentManifestException(
                requestedRevision,
                $"prompt manifest schema_version={dto.SchemaVersion}（僅支援 {PromptManifestSchemaVersion}）");
        }

        if (dto.Revision != requestedRevision)
        {
            throw new PermanentManifestException(
                requestedRevision,
                $"prompt manifest revision 不符：要求 {requestedRevision}，回傳 {dto.Revision}");
        }

        if (!IsSha256Hex(dto.ManifestSha256))
        {
            throw new PermanentManifestException(requestedRevision, "prompt manifest 缺少合法的 manifest_sha256");
        }

        if (dto.Components is not { Count: > 0 } components)
        {
            throw new PermanentManifestException(requestedRevision, "prompt manifest 沒有任何 component");
        }

        var byKind = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            // 空白 content 一律拒:backend publish 本來就不收空白,真的收到就是下游異常 ——
            // 靜默套用一段空白 guard 等於把數字護欄拆掉,寧可 fail closed 回 constants。
            if (string.IsNullOrWhiteSpace(component.Kind) || string.IsNullOrWhiteSpace(component.Content))
            {
                throw new PermanentManifestException(requestedRevision, "prompt manifest component 缺少 kind 或 content");
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(component.Content)));
            if (!string.Equals(actual, component.ContentSha256, StringComparison.Ordinal))
            {
                throw new PermanentManifestException(
                    requestedRevision, $"prompt component {component.Kind} 的 content_sha256 不符（實際 {actual}）");
            }

            if (!byKind.TryAdd(component.Kind, component.Content))
            {
                throw new PermanentManifestException(requestedRevision, $"prompt manifest 出現重複的 kind：{component.Kind}");
            }
        }

        return new ResolvedManifest(dto.Revision, dto.ManifestSha256!, byKind);
    }

    /// <summary>
    /// 以 manifest component 取代對應槽位;manifest 沒帶的 kind 保留 constants。
    /// <c>governance_frame</c> 是 workflow <c>_system_frame()</c> 的槽位,platform 不使用(計畫 03 §3.2);
    /// 其他未知 kind 同樣只是不被使用,無法影響組成。
    /// </summary>
    private static PromptComposition Compose(ResolvedManifest manifest, PromptComposition constants) =>
        new(
            manifest.Content("guard", constants.Guard),
            manifest.Content("memory_policy", constants.MemoryPrefix),
            manifest.Content("routing", constants.Routing),
            manifest.Content("summary", constants.Summary),
            // 沒有 transport persona 的鏈路(chat)不從 manifest 生出 persona。
            constants.Persona is null ? null : manifest.Content("persona", constants.Persona));

    /// <summary>manifest 是否至少替換了一個 platform 實際使用的槽位(低10)。</summary>
    private static bool HasAnyPlatformSlot(ResolvedManifest manifest) =>
        manifest.ByKind.ContainsKey("guard") || manifest.ByKind.ContainsKey("memory_policy")
        || manifest.ByKind.ContainsKey("routing") || manifest.ByKind.ContainsKey("summary")
        || manifest.ByKind.ContainsKey("persona");

    private const int PromptManifestSchemaVersion = 1;

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>已驗證的 manifest(快取單位)。</summary>
    public sealed record ResolvedManifest(
        int Revision, string ManifestSha256, IReadOnlyDictionary<string, string> ByKind)
    {
        public string Content(string kind, string fallback) =>
            ByKind.TryGetValue(kind, out var content) ? content : fallback;
    }

    /// <summary>暫時性下游故障(傳輸例外、backend 5xx)——每輪都要重試,不快取,維持改動前的行為。</summary>
    private sealed class TransientManifestException(string message) : Exception(message);

    /// <summary>
    /// 永久性(非暫時性)下游故障 —— 給定同一個不可變 revision、或同一個壞掉的 config 值,不會自己變好,
    /// 故 <see cref="ResolveManifestAsync"/> 短 TTL 負向快取並降為 Warning。<see cref="Revision"/> 為 null
    /// 代表失敗發生在還沒讀到 revision 之前(config 值本身不合法),沒有可快取的 revision 鍵。
    /// </summary>
    private sealed class PermanentManifestException(int? revision, string message) : Exception(message)
    {
        public int? Revision { get; } = revision;
    }
}

/// <summary>
/// 本輪(一個 HTTP request)的 prompt manifest 解析結果,由 <see cref="IChatIdentityAccessor.PromptManifestCache"/>
/// 持有。<see cref="Manifest"/> 為 null 代表本輪已解析過但沒有 manifest 生效(canary 未啟用,或解析失敗
/// 已 fail closed 到 constants)——用一個非 null 的包裝物件本身表達「已解析過」,才能和「尚未解析」
/// (<see cref="IChatIdentityAccessor.PromptManifestCache"/> 本身為 null)區分開來。
/// </summary>
public sealed record PromptManifestResolutionCache(PromptCompositionResolver.ResolvedManifest? Manifest);

/// <summary>
/// backend <c>GET /api/prompt-manifests/{revision}/resolved</c> 的 snake_case 回應。內部消費,
/// 不對外代理(raw component content 絕不進瀏覽器),故不放進 Dtos 的公開契約資料夾。
/// 回應另有 <c>tool_catalog_hash</c>/<c>skill_catalog_hash</c> 與每個 component 自己的 <c>revision</c>,
/// P1 的 platform 組成不使用(catalog hash 是 P2 evidence 的事),因此刻意不反序列化。
/// </summary>
internal sealed record ResolvedManifestDto(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("manifest_sha256")] string? ManifestSha256,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("components")] IReadOnlyList<ResolvedComponentDto>? Components);

internal sealed record ResolvedComponentDto(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("content_sha256")] string? ContentSha256,
    [property: JsonPropertyName("content")] string? Content);
