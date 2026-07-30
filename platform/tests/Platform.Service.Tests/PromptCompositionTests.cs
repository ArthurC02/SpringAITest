using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// P1 manifest-aware prompt 組成(plans/agent-architecture-improvements/03-prompt-model-runtime-plan.md
/// §3/§7)。決策表兩半都測:「解析成功 → 組成被替換」與「任何失敗 → 退回 constants + error log」。
/// 旗標關閉那一半是結構性的(resolver 未註冊),由既有 ChatServiceTests/ChatSkillRoutingTests/
/// <see cref="Assembler_Defaults_AreCurrentConstantBytes"/> 逐字釘住。
/// </summary>
public sealed class PromptCompositionTests
{
    private const string Tenant = "demo-a";
    private static readonly string ConfigRuntimePath =
        "/api/config/runtime/" + PromptCompositionResolver.ManifestRevisionConfigKey;

    // 逐字 golden(改動任一常數都會讓本檔失敗):護欄、mem0 前綴、AG-UI persona。
    private const string GuardVerbatim =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    private const string MemoryPrefixVerbatim =
        "以下是你先前記住、關於這位使用者的長期記憶，回答時可參考（與當前問題無關者請忽略）：";

    private const string PersonaVerbatim = "你是測試用 persona";

    // ============================================================================
    // Assembler(constants 預設來源)
    // ============================================================================

    // 組成的位元 = 改造前「framework 把 agent instructions 換行接 AIContext.Instructions」的輸出。
    // 三種等價類:無 persona(鏈路 A)、有 persona(AG-UI)、有 mem0 記憶。
    [Fact]
    public void Assembler_Defaults_AreCurrentConstantBytes()
    {
        Assert.Equal(GuardVerbatim, PromptComposition.Defaults(null).Instructions(null));
        Assert.Equal(
            PersonaVerbatim + "\n" + GuardVerbatim,
            PromptComposition.Defaults(PersonaVerbatim).Instructions(null));
        Assert.Equal(
            GuardVerbatim + "\n" + MemoryPrefixVerbatim + "\n" + "- 使用者喜歡貓",
            PromptComposition.Defaults(null).Instructions("- 使用者喜歡貓"));
    }

    // mem0 回純空白不得接出空前言(既有邊界,搬家後仍成立)。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Assembler_BlankMemories_AreNotInjected(string? memories)
        => Assert.Equal(GuardVerbatim, PromptComposition.Defaults(null).Instructions(memories));

    // ============================================================================
    // Resolver:manifest 選定與替換
    // ============================================================================

    [Fact]
    public async Task Resolver_Anonymous_UsesConstants_WithoutTouchingBackend()
    {
        var backend = new FakeBackendCalls();
        var resolver = Resolver(backend);

        var prompts = await resolver.ResolveAsync(tenant: null, transportPersona: null);

        Assert.Equal(PromptComposition.Defaults(null), prompts);
        Assert.Empty(backend.Paths);
    }

    // 租戶沒設定 prompt.manifest_revision = 未加入 canary:用 constants,且不打 manifest 端點。
    // null = allowlist 內但本租戶未設定值(backend 404);其餘兩個是「設定了但值空白」的防禦性邊界
    // (backend PUT 本身不收空白,這裡測 platform 自己這一層的防禦仍然成立)。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolver_TenantWithoutManifestRevision_UsesConstants_AndNeverFetchesManifest(string? configValue)
    {
        var backend = new FakeBackendCalls { ConfigValue = configValue };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        var prompts = await resolver.ResolveAsync(Tenant, PersonaVerbatim);

        Assert.Equal(PromptComposition.Defaults(PersonaVerbatim), prompts);
        Assert.Equal(new[] { ConfigRuntimePath }, backend.Paths);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // 設定了 revision:每個 kind 都被 manifest component 取代;manifest 沒帶的 kind 保留 constants;
    // 兩條鏈路共用同一份 guard/routing/summary/memory_policy identity,只有 persona 依 transport 不同。
    [Fact]
    public async Task Resolver_ConfiguredManifest_ReplacesKinds_AndBothChainsShareOneIdentity()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(
                revision: 7,
                components: new[]
                {
                    ("guard", "護欄-manifest"),
                    ("routing", "路由-manifest"),
                    ("memory_policy", "記憶-manifest"),
                    ("persona", "persona-manifest"),
                    // governance_frame 是 workflow 的槽位,platform 必須忽略而不是誤用。
                    ("governance_frame", "workflow 專用"),
                }),
        };
        var resolver = Resolver(backend);

        var agui = await resolver.ResolveAsync(Tenant, PersonaVerbatim);
        var chat = await resolver.ResolveAsync(Tenant, null);

        Assert.Equal("護欄-manifest", agui.Guard);
        Assert.Equal("路由-manifest", agui.Routing);
        Assert.Equal("記憶-manifest", agui.MemoryPrefix);
        // manifest 沒帶 summary → 保留常數(部分替換)。
        Assert.Equal(PromptComposition.SummaryDefault, agui.Summary);
        Assert.Equal("persona-manifest", agui.Persona);

        // 共享槽位 identity 完全相同(不是各自解析出兩份)。
        Assert.Equal(
            (agui.Guard, agui.Routing, agui.Summary, agui.MemoryPrefix),
            (chat.Guard, chat.Routing, chat.Summary, chat.MemoryPrefix));

        // persona 依 transport:鏈路 A 本來就沒有 persona,manifest 不得替它生出一個。
        Assert.Null(chat.Persona);

        // 組出的 instructions 也確實用上 manifest 的 persona/guard/記憶前綴。
        Assert.Equal("persona-manifest\n護欄-manifest", agui.Instructions(null));
        Assert.Equal("護欄-manifest\n記憶-manifest\n- 記憶", chat.Instructions("- 記憶"));
    }

    // 租戶隔離:各租戶各自的 revision/manifest,且送出的 X-Tenant-Id 是該租戶自己的。
    [Fact]
    public async Task Resolver_TenantIsolation_EachTenantGetsItsOwnManifest()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValueByTenant = new Dictionary<string, string> { ["demo-a"] = "7", ["demo-b"] = "9" },
            ManifestBodyByRevision = new Dictionary<int, string>
            {
                [7] = ManifestJson(7, new[] { ("guard", "A 的護欄") }),
                [9] = ManifestJson(9, new[] { ("guard", "B 的護欄") }),
            },
        };
        var resolver = Resolver(backend);

        var a = await resolver.ResolveAsync("demo-a", null);
        var b = await resolver.ResolveAsync("demo-b", null);

        Assert.Equal("A 的護欄", a.Guard);
        Assert.Equal("B 的護欄", b.Guard);
        Assert.Equal(
            new[] { "demo-a", "demo-a", "demo-b", "demo-b" },
            backend.Tenants);
    }

    // 快取:同一 (tenant, revision) 只取一次 resolved manifest;config 仍每輪讀(canary 開關要能即時生效)。
    [Fact]
    public async Task Resolver_CachesManifestPerTenantRevision_ButRereadsConfigEachTurn()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
        };
        var resolver = Resolver(backend);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("護欄-manifest", (await resolver.ResolveAsync(Tenant, null)).Guard);
        }

        Assert.Equal(3, backend.Paths.Count(p => p == ConfigRuntimePath));
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");
    }

    // 低6:每個 (tenant, revision) 的「生效」Information log 只在真正寫入快取的那一次(GetOrAdd 命中
    // 既有值不重複)出現一次,即使同一輪重複解析三次。
    [Fact]
    public async Task Resolver_InformationLog_FiresOnlyOncePerTenantRevision()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        await resolver.ResolveAsync(Tenant, null);
        await resolver.ResolveAsync(Tenant, null);
        await resolver.ResolveAsync(Tenant, null);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("生效", info.Message);
    }

    // ============================================================================
    // Fail closed:回 constants,依失敗性質分兩類(中4)——
    // 暫時性(傳輸例外/backend 5xx/config 讀取被拒)維持現狀:Error + 不快取,每輪重試;
    // 永久性(404/驗證失敗/schema drift/config 值不合法)降為 Warning,並以 (tenant, revision) 短 TTL
    // 負向快取,同一個不可變 revision 不再重打 backend。
    // ============================================================================

    // 驗證失敗的等價類:component content SHA 不符、manifest_sha256 形狀不合法、schema drift、revision 不符
    // ——每一種都是給定同一個不可變 revision 絕不會自己變好,故 Warning + 負向快取。
    [Theory]
    [InlineData("bad-component-sha")]
    [InlineData("bad-manifest-sha")]
    [InlineData("schema-drift")]
    [InlineData("revision-mismatch")]
    [InlineData("blank-content")]
    public async Task Resolver_VerificationFailure_FailsClosedToConstants_LogsWarning_AndNegativeCachesRevision(
        string flavour)
    {
        var body = flavour switch
        {
            // 空白護欄若被靜默套用,數字護欄就消失了 —— 必須 fail closed。
            "blank-content" => ManifestJson(7, new[] { ("guard", "   ") }),
            "bad-component-sha" => ManifestJson(
                7, new[] { ("guard", "護欄-manifest") }, componentShaOverride: new string('a', 64)),
            "bad-manifest-sha" => ManifestJson(7, new[] { ("guard", "護欄-manifest") }, manifestSha: "not-a-sha"),
            "schema-drift" => ManifestJson(7, new[] { ("guard", "護欄-manifest") }, schemaVersion: 2),
            _ => ManifestJson(8, new[] { ("guard", "護欄-manifest") }),
        };
        var backend = new FakeBackendCalls { ConfigValue = "7", ManifestBody = body };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        var prompts = await resolver.ResolveAsync(Tenant, PersonaVerbatim);

        Assert.Equal(PromptComposition.Defaults(PersonaVerbatim), prompts);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        // 永久失敗負向快取:下一輪同一個 (tenant, revision) 不再重打 /api/prompt-manifests/7/resolved。
        await resolver.ResolveAsync(Tenant, PersonaVerbatim);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");
    }

    // 中1:負向快取 TTL 是「從真正驗證失敗那一刻」算起的固定視窗,不是每次命中就續期的滑動視窗——
    // 命中不得打 backend,也不得把 failedAt 往後推;61s(超過 60s TTL)後必須重新驗證。
    // (若曾把 revision 帶進命中拋出的例外,catch 端會再次 AddNegative 續期,聊天間隔 < TTL 的租戶就永遠
    // 卡在負向快取,必須重啟 platform 才能重試——這裡直接撥動時鐘覆核修法。)
    [Fact]
    public async Task Resolver_NegativeCacheHit_DoesNotExtendTtl_AndRetriesAfterTtlExpires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            // schema drift:每次重新驗證都會再次失敗(同一個不可變 revision 不會自己變好),
            // 純粹用來觀察「manifest 端點有沒有被重打」,不混入「重試後轉為成功」這個額外變數。
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }, schemaVersion: 2),
        };
        var resolver = Resolver(backend, timeProvider: time);

        // t0:第一次真正驗證失敗 → 負向快取 failedAt = t0。
        await resolver.ResolveAsync(Tenant, null);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");

        // t0+30s(< 60s TTL):命中負向快取,不打 backend。
        time.Advance(TimeSpan.FromSeconds(30));
        await resolver.ResolveAsync(Tenant, null);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");

        // t0+61s:距「第一次失敗」61s、超過 60s TTL。若命中曾經續期(舊 bug,failedAt 被推到 t0+30s),
        // 此時只過了 31s,仍會誤判為快取有效、不重試;修好後 failedAt 停在 t0,61s 已過期 → 重新打 backend。
        time.Advance(TimeSpan.FromSeconds(31));
        await resolver.ResolveAsync(Tenant, null);
        Assert.Equal(2, backend.Paths.Count(p => p == "/api/prompt-manifests/7/resolved"));
    }

    // 中1:反覆命中負向快取(TTL 內)不得無上限 Enqueue——佇列只在「新的一次真正驗證失敗」時增長,
    // 命中快取的呼叫不得推動它(否則長期運行、單一租戶反覆命中會讓佇列無上限成長)。
    [Fact]
    public async Task Resolver_RepeatedNegativeCacheHits_DoNotGrowInternalQueue()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }, schemaVersion: 2),
        };
        var resolver = Resolver(backend);

        await resolver.ResolveAsync(Tenant, null);
        Assert.Equal(1, resolver.NegativeCacheOrderCount);

        for (var i = 0; i < 10; i++)
        {
            await resolver.ResolveAsync(Tenant, null);
        }

        Assert.Equal(1, resolver.NegativeCacheOrderCount);
    }

    // 中1:TTL 過期後重新驗證若再次失敗,同一個 key 只在第一次 AddNegative 時入佇列;
    // 逾期後重試的 AddNegative 呼叫只續期 failedAt、不再 Enqueue,故佇列 count 仍為 1。
    [Fact]
    public async Task Resolver_NegativeCacheRetriesAfterTtlExpires_DoNotRegrewQueue()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }, schemaVersion: 2),
        };
        var resolver = Resolver(backend, timeProvider: time);

        // t0:第一次真正驗證失敗 → AddNegative 入佇列。
        await resolver.ResolveAsync(Tenant, null);
        Assert.Equal(1, resolver.NegativeCacheOrderCount);

        // t0+61s:TTL 已過期,重新打 backend 再次失敗 → AddNegative 被呼叫。
        time.Advance(TimeSpan.FromSeconds(61));
        await resolver.ResolveAsync(Tenant, null);

        // 修法正確:第二次 AddNegative 只續期不 Enqueue → 佇列 count 仍為 1。
        Assert.Equal(1, resolver.NegativeCacheOrderCount);
    }

    // backend 明確找不到這個 revision(404)是永久性(旗標關閉/不存在/跨租戶都不會自己變好)——
    // Warning + 負向快取,與上面的驗證失敗同一個分類。
    [Fact]
    public async Task Resolver_ManifestNotFound_FailsClosedToConstants_LogsWarning_AndNegativeCachesRevision()
    {
        var backend = new FakeBackendCalls { ConfigValue = "7", ManifestStatus = HttpStatusCode.NotFound };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        Assert.Equal(PromptComposition.Defaults(null), await resolver.ResolveAsync(Tenant, null));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        await resolver.ResolveAsync(Tenant, null);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");
    }

    // 暫時性下游故障的等價類:完全沒有回應(傳輸例外)、backend 5xx、config 讀取被拒(非 404 的其餘狀態)——
    // 都可能只是網路/服務瞬斷,維持改動前的行為:Error 級別、不快取,每輪都重試。
    [Theory]
    [InlineData("transport")]
    [InlineData("manifest-500")]
    [InlineData("config-403")]
    public async Task Resolver_TransientBackendFailure_FailsClosedToConstants_LogsError_AndNeverCaches(string flavour)
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
            ThrowOnManifest = flavour == "transport",
            ManifestStatus = flavour == "manifest-500" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
            ConfigStatus = flavour == "config-403" ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        var prompts = await resolver.ResolveAsync(Tenant, null);

        Assert.Equal(PromptComposition.Defaults(null), prompts);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);

        // 低4:「never caches」承諾必須被斷言——第二輪仍重打 backend,不因第一輪失敗被負向快取擋下
        // (比照永久失敗測試的收尾寫法:那邊斷言第二輪只打一次 manifest 端點,這裡反過來斷言打了兩次)。
        var second = await resolver.ResolveAsync(Tenant, null);
        Assert.Equal(PromptComposition.Defaults(null), second);
        Assert.Equal(2, backend.Paths.Count(p => p == ConfigRuntimePath));
        if (flavour != "config-403")
        {
            // config-403 這個等價類在讀到 revision 之前就失敗,manifest 端點本來就不會被打到;
            // 其餘兩種(傳輸例外、manifest 5xx)必須證明「每輪都重試」而非「第一輪之後被快取擋下」。
            Assert.Equal(2, backend.Paths.Count(p => p == "/api/prompt-manifests/7/resolved"));
        }
    }

    // config 值不是正整數 revision → 設定錯誤,fail closed 而不是猜一個 revision;沒有 revision 可快取
    // (失敗發生在讀到 revision 之前),但同屬永久性分類,降為 Warning。
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("7.5")]
    public async Task Resolver_NonPositiveIntegerConfigValue_FailsClosedToConstants_AndLogsWarning(string value)
    {
        var backend = new FakeBackendCalls { ConfigValue = value };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        Assert.Equal(PromptComposition.Defaults(null), await resolver.ResolveAsync(Tenant, null));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(backend.Paths, p => p.StartsWith("/api/prompt-manifests", StringComparison.Ordinal));
    }

    // 低10:manifest 驗證通過但不含任何 platform 使用的槽位(只有 workflow 專用的 governance_frame)——
    // 組成逐位元等於 constants,不是錯誤,但值得留一條 Warning 讓 ops 注意到這個 revision 沒有作用。
    [Fact]
    public async Task Resolver_ManifestWithNoPlatformSlots_ComposesToConstants_ButLogsWarning()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("governance_frame", "workflow 專用") }),
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, logger: logger);

        var prompts = await resolver.ResolveAsync(Tenant, PersonaVerbatim);

        Assert.Equal(PromptComposition.Defaults(PersonaVerbatim), prompts);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("未包含任何 platform 使用的槽位", warning.Message);
    }

    // ============================================================================
    // Shadow 子模式:兩種組成都算、只比 hash、實際用 constants
    // ============================================================================

    [Fact]
    public async Task Resolver_Shadow_UsesConstants_AndWarnsWithHashesOnly()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, shadow: true, logger: logger);

        var prompts = await resolver.ResolveAsync(Tenant, PersonaVerbatim);

        // 實際使用 constants(shadow 不改變行為),但 manifest 確實被解析過(有打端點)。
        Assert.Equal(PromptComposition.Defaults(PersonaVerbatim), prompts);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        // 只 log identity(revision + manifest SHA + 兩份組成 SHA);兩種組成的原文都不得出現。
        Assert.DoesNotContain("護欄-manifest", warning.Message);
        Assert.DoesNotContain(GuardVerbatim, warning.Message);
        Assert.Contains("revision=7", warning.Message);
        Assert.Contains("manifest_sha256=" + new string('b', 64), warning.Message);
        var shadowComposition = PromptComposition.Defaults(PersonaVerbatim) with { Guard = "護欄-manifest" };
        Assert.Contains("composition_sha256=" + shadowComposition.Sha256(), warning.Message);
        Assert.Contains(
            "constants_sha256=" + PromptComposition.Defaults(PersonaVerbatim).Sha256(), warning.Message);
    }

    // shadow 且兩邊組成一致(manifest 內容剛好等於 constants)→ 不必吵:沒有 warning。
    [Fact]
    public async Task Resolver_Shadow_IdenticalComposition_DoesNotWarn()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", GuardVerbatim) }),
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, shadow: true, logger: logger);

        Assert.Equal(PromptComposition.Defaults(null), await resolver.ResolveAsync(Tenant, null));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // 低6:shadow 模式下 manifest 沒有真的套用(實際仍用 constants),Information log 措辭不得說「生效」。
    [Fact]
    public async Task Resolver_Shadow_InformationLog_SaysObservingNotInEffect()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
        };
        var logger = new RecordingLogger<PromptCompositionResolver>();
        var resolver = Resolver(backend, shadow: true, logger: logger);

        await resolver.ResolveAsync(Tenant, null);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("shadow 觀測中", info.Message);
        Assert.DoesNotContain("生效", info.Message);
    }

    // ============================================================================
    // 管線接線:兩條鏈路是否真的用上 resolver(單元測試證明不了接線)
    // ============================================================================

    private static readonly Dtos.UserContext UserA = new("user-a", Tenant, "USER");

    private const string RoutableCatalog = """
    [ { "name":"kb-query", "description":"知識庫檢索", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    // 路由命中(HIT):路由與摘要的 system prompt 都來自 manifest(SkillRoutingAgent 側的接線)。
    [Fact]
    public async Task Pipeline_ManifestInEffect_RoutingAndSummaryComeFromManifest()
    {
        var resolver = Resolver(new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(
                7, new[] { ("routing", "路由-manifest"), ("summary", "摘要-manifest") }),
        });
        var llm = new FakeLlmAgent();
        llm.Responses.Enqueue("kb-query");     // 路由
        llm.Responses.Enqueue("潤飾後回覆");   // 摘要
        var workflows = new FakeWorkflowService
        {
            Catalog = JsonDocument.Parse(RoutableCatalog).RootElement.Clone(),
            SkillOutput = JsonSerializer.SerializeToElement(new { output = new { answer = "毛利率 32.8%" } }),
        };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "p1-hit", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(
            identity: identity, llmAgent: llm, workflows: workflows, prompts: resolver);

        var session = await hostAgent.GetOrCreateSessionAsync("p1-hit");
        var response = await hostAgent.RunAsync("這季毛利率多少?", session);

        Assert.Equal("潤飾後回覆", response.Text);
        Assert.StartsWith("路由-manifest", llm.CompleteCalls[0][0].Content);
        Assert.Equal("摘要-manifest", llm.CompleteCalls[1][0].Content);
    }

    // 未命中(純聊天兜底):送進模型的 Instructions 用 manifest 的護欄(ChatContextProvider 側的接線)。
    [Fact]
    public async Task Pipeline_ManifestInEffect_GuardComesFromManifest()
    {
        var resolver = Resolver(new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest") }),
        });
        var chatClient = new FakeChatClient();
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "p1-miss", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(
            chatClient, identity: identity, llmAgent: new FakeLlmAgent { Response = "NONE" },
            prompts: resolver);

        var session = await hostAgent.GetOrCreateSessionAsync("p1-miss");
        await hostAgent.RunAsync("你好呀", session);

        Assert.Equal("護欄-manifest", chatClient.LastOptions!.Instructions);
    }

    // 中3:一輪(同一次 hostAgent.RunAsync)內 SkillRoutingAgent(路由)與 ChatContextProvider(護欄)
    // 各自開新 scope 呼叫 resolver;傳入同一個 IChatIdentityAccessor 後兩者必須共用同一次解析結果
    // ——manifest 內容一致(同一 revision),且只讀一次 config/backend,不是各自獨立解析兩次。
    [Fact]
    public async Task Pipeline_MissTurn_SkillRoutingAndContextProvider_ShareOneResolve()
    {
        var backend = new FakeBackendCalls
        {
            ConfigValue = "7",
            ManifestBody = ManifestJson(7, new[] { ("guard", "護欄-manifest"), ("routing", "路由-manifest") }),
        };
        var resolver = Resolver(backend);
        var workflows = new FakeWorkflowService { Catalog = JsonDocument.Parse(RoutableCatalog).RootElement.Clone() };
        var chatClient = new FakeChatClient();
        var llm = new FakeLlmAgent { Response = "NONE" };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "p1-shared", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(
            chatClient, identity: identity, llmAgent: llm, workflows: workflows, prompts: resolver);

        var session = await hostAgent.GetOrCreateSessionAsync("p1-shared");
        await hostAgent.RunAsync("你好呀", session);

        // 兩個消費點都真的解析過 manifest,且用的是同一份:路由用 manifest 的 routing,委派後
        // ChatContextProvider 用 manifest 的 guard。
        Assert.StartsWith("路由-manifest", llm.CompleteCalls[0][0].Content);
        Assert.Equal("護欄-manifest", chatClient.LastOptions!.Instructions);

        // 只讀一次 config/manifest —— 不是兩個消費點各自打一次(降為每輪 1 次本機 backend 往返)。
        Assert.Single(backend.Paths, p => p == ConfigRuntimePath);
        Assert.Single(backend.Paths, p => p == "/api/prompt-manifests/7/resolved");
    }

    // ============================================================================
    // 測試支架
    // ============================================================================

    private static PromptCompositionResolver Resolver(
        FakeBackendCalls backend,
        bool shadow = false,
        RecordingLogger<PromptCompositionResolver>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var client = TestBackend.Client(new StubHttpMessageHandler(backend.Respond));
        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddSingleton<IConfigService>(new ConfigService(client));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new PromptCompositionResolver(
            scopeFactory, shadow, logger ?? new RecordingLogger<PromptCompositionResolver>(), timeProvider);
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string ManifestJson(
        int revision,
        (string Kind, string Content)[] components,
        int schemaVersion = 1,
        string? manifestSha = null,
        string? componentShaOverride = null) =>
        JsonSerializer.Serialize(new
        {
            revision,
            manifest_sha256 = manifestSha ?? new string('b', 64),
            schema_version = schemaVersion,
            tool_catalog_hash = "sha256:tools",
            skill_catalog_hash = "sha256:skills",
            components = components.Select(c => new
            {
                kind = c.Kind,
                revision = 1,
                content_sha256 = componentShaOverride ?? Sha(c.Content),
                content = c.Content,
            }).ToArray(),
        });

    /// <summary>
    /// backend(:8002)的 stub:同時服務 <c>/api/config/runtime/{key}</c>(單鍵讀取,中2)與
    /// <c>/api/prompt-manifests/{rev}/resolved</c>,並記下每次呼叫的路徑與 X-Tenant-Id(供「不該打的端點
    /// 沒被打」與租戶隔離斷言)。
    /// </summary>
    private sealed class FakeBackendCalls
    {
        public List<string> Paths { get; } = new();
        public List<string> Tenants { get; } = new();

        /// <summary>null = 該 key 未設定(backend 404);非 null = 該租戶的 config 值(可能空白/非整數,測防禦性邊界)。</summary>
        public string? ConfigValue { get; init; }
        public IReadOnlyDictionary<string, string>? ConfigValueByTenant { get; init; }
        public HttpStatusCode ConfigStatus { get; init; } = HttpStatusCode.OK;

        public string? ManifestBody { get; init; }
        public IReadOnlyDictionary<int, string>? ManifestBodyByRevision { get; init; }
        public HttpStatusCode ManifestStatus { get; init; } = HttpStatusCode.OK;
        public bool ThrowOnManifest { get; init; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var tenant = request.Headers.TryGetValues("X-Tenant-Id", out var values)
                ? values.Single()
                : string.Empty;
            Tenants.Add(tenant);

            if (path == "/api/config/runtime/" + PromptCompositionResolver.ManifestRevisionConfigKey)
            {
                if (ConfigStatus != HttpStatusCode.OK)
                {
                    return TestHttp.Error(ConfigStatus, "權限不足");
                }

                var value = ConfigValueByTenant is not null
                    ? ConfigValueByTenant.TryGetValue(tenant, out var byTenant) ? byTenant : null
                    : ConfigValue;
                return value is null
                    ? TestHttp.Error(HttpStatusCode.NotFound, "找不到")
                    : TestHttp.Json(
                        HttpStatusCode.OK,
                        $$"""{"key":"prompt.manifest_revision","value":{{JsonSerializer.Serialize(value)}},"updatedAt":"2026-07-30T00:00:00Z"}""");
            }

            if (ThrowOnManifest)
            {
                throw new HttpRequestException("backend 不可達");
            }

            if (ManifestStatus != HttpStatusCode.OK)
            {
                return TestHttp.Error(ManifestStatus, "找不到資源");
            }

            var requested = int.Parse(path.Split('/')[3]);
            var body = ManifestBodyByRevision?[requested] ?? ManifestBody!;
            return TestHttp.Json(HttpStatusCode.OK, body);
        }
    }

    /// <summary>記下 level + 已格式化訊息的 logger fake(手寫,不引入 mocking 套件)。</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
