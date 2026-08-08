using System.Net;
using Platform.Service.Abstractions;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Web.Tests;

/// <summary>
/// 以「Testing」環境啟動真實的應用程式(真實認證、驗證、例外處理、SSE、ChatService 組裝),
/// 但把 backend 相依的服務層與對外相依(LLM、mem0)換成 fake,不需要真的 backend。
/// Testing 環境下不掛 OTLP exporter。
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 每個測試共用的固定設定,加上所有 feature flag 的 fail-closed 預設值;呼叫端的 settings bag 逐鍵覆寫。
    /// 這裡是「應用程式會讀哪些設定鍵」的唯一清單:新增旗標只在這裡多一行,建構子不再跟著變寬。
    /// </summary>
    private static readonly Dictionary<string, string?> DefaultSettings = new(StringComparer.Ordinal)
    {
        ["INTERNAL_API_TOKEN"] = "platform-test-internal-token",
        ["JWT_ISSUER"] = TestTokens.Issuer,
        ["JWT_AUDIENCE"] = TestTokens.Audience,
        ["JWT_PUBLIC_KEY_RING_JSON"] = TestTokens.PublicKeyRingJson,
        ["RABBITMQ_URL"] = "amqp://platform-test-user:platform-test-password@rabbit.test:5672/test",
        ["TRUSTED_PROXY_CIDR"] = "",
        // Feature flag(D1 起):預設關閉(fail-closed);需要走該端點家族的測試以 settings bag 開啟。
        ["AGENT_BUILDER_ENABLED"] = "false",
        ["AGENT_TEST_RUN_ENABLED"] = "false",
        ["WORKFLOW_DESIGNER_ENABLED"] = "false",
        ["MULTI_AGENT_DISPATCH_ENABLED"] = "false",
        ["CONTEXT_ENRICHMENT_ENABLED"] = "false",
        ["AGENT_WRITE_TOOLS_ENABLED"] = "false",
        // D6 chat canary:旗標與逗號分隔的伺服器端租戶白名單是兩個獨立條件(兩者皆通過才進 canary),
        // 故兩個鍵分開,允許測「已啟用但租戶不在白名單」這一格。
        ["AGENT_CHAT_ENABLED"] = "false",
        ["AGENT_CHAT_TENANT_ALLOWLIST"] = "",
    };

    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;
    private readonly bool _removeSessionIsolationProvider;
    private readonly IMem0Client? _mem0Override;
    private readonly IPAddress? _remoteIpAddress;
    private readonly AuthRateLimiter? _authRateLimiterOverride;
    private readonly IAuthService? _authServiceOverride;

    public TestWebAppFactory()
        : this(settings: null)
    {
    }

    /// <param name="settings">
    /// 覆寫 <see cref="DefaultSettings"/> 的設定鍵值(旗標、白名單、CIDR…);鍵必須是既有的設定鍵,
    /// 打錯直接拋例外——否則「旗標沒開卻通過」會變成靜默的假綠燈。
    /// </param>
    internal TestWebAppFactory(
        Dictionary<string, string?>? settings = null,
        string environment = "Testing",
        bool removeSessionIsolationProvider = false,
        IMem0Client? mem0Override = null,
        IPAddress? remoteIpAddress = null,
        AuthRateLimiter? authRateLimiterOverride = null,
        IAuthService? authServiceOverride = null)
    {
        _settings = new Dictionary<string, string?>(DefaultSettings, StringComparer.Ordinal);
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                if (!_settings.ContainsKey(key))
                {
                    throw new ArgumentException($"未知的設定鍵:{key}(請先加進 DefaultSettings)", nameof(settings));
                }

                _settings[key] = value;
            }
        }

        _environment = environment;
        _removeSessionIsolationProvider = removeSessionIsolationProvider;
        _mem0Override = mem0Override;
        _remoteIpAddress = remoteIpAddress;
        _authRateLimiterOverride = authRateLimiterOverride;
        _authServiceOverride = authServiceOverride;
    }

    /// <summary>只需要打開旗標的呼叫端捷徑;旗標名沿用 <see cref="DefaultSettings"/> 的鍵。</summary>
    internal static TestWebAppFactory WithFlags(params string[] enabledFlags)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var flag in enabledFlags)
        {
            settings[flag] = "true";
        }

        return new TestWebAppFactory(settings);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // P1 審查建議的釘子:AddAIAgent 的 hosted agent 若誤宣告 Scoped,Development 環境的
        // ValidateScopes=true 會在啟動期就炸——用真正的 "Development" 環境跑一次冒煙測試釘住
        // 「這個崩潰不會再發生」(見 DevelopmentEnvironmentSmokeTests)。
        builder.UseEnvironment(_environment);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            if (_remoteIpAddress is not null)
            {
                services.AddSingleton<IStartupFilter>(new RemoteIpAddressStartupFilter(_remoteIpAddress));
            }
            if (_authRateLimiterOverride is not null)
            {
                services.RemoveAll<AuthRateLimiter>();
                services.AddSingleton(_authRateLimiterOverride);
            }

            // 每個 factory 一份的重置權杖:fake 的 static 呼叫紀錄第一次被這個 factory 碰到時清空,
            // 讓「一個測試一個 factory」的類別不會讀到前一個測試的殘留(見 FakeCallScope)。
            services.AddSingleton<FakeCallScope>();

            // B-P1-06:移除 SessionIsolationKeyProvider 註冊,證明 Strict=true 的 fail-closed 真的開著——
            // 即使帶有效 JWT,AG-UI 端點仍應在存取 session store 時拋例外(對外 500),不得默默共用全域命名空間。
            if (_removeSessionIsolationProvider)
            {
                services.RemoveAll<SessionIsolationKeyProvider>();
            }

            // 對外相依:LLM 與 mem0(真實 ChatService 仍會用到)。
            services.RemoveAll<ILlmAgent>();
            services.AddSingleton<ILlmAgent, FakeLlmAgent>();

            services.RemoveAll<IMem0Client>();
            if (_mem0Override is not null)
            {
                // B-P3-01(順序斷言)需要專屬、非共用靜態狀態的 mem0 fake,避免與其他測試共用
                // FakeMem0Client.Remembered 造成跨測試污染或平行執行的順序不確定性。
                services.AddSingleton(_mem0Override);
            }
            else
            {
                services.AddSingleton<IMem0Client, FakeMem0Client>();
            }

            // AG-UI 操作助理的底層 IChatClient → fake(避免打真 LiteLLM)。
            services.RemoveAll<Microsoft.Extensions.AI.IChatClient>();
            services.AddSingleton<Microsoft.Extensions.AI.IChatClient, FakeChatClient>();

            // backend 相依的服務層 → fake(避免真的打 :8002)。
            services.RemoveAll<IConversationStore>();
            services.AddScoped<IConversationStore, FakeConversationStore>();

            services.RemoveAll<IAuthService>();
            if (_authServiceOverride is not null)
            {
                services.AddSingleton(_authServiceOverride);
            }
            else
            {
                services.AddScoped<IAuthService, FakeAuthService>();
            }

            services.RemoveAll<IWorkflowEngineClient>();
            services.AddScoped<IWorkflowEngineClient, FakeWorkflowEngineClient>();

            services.RemoveAll<IDocumentService>();
            services.AddScoped<IDocumentService, FakeDocumentService>();

            services.RemoveAll<IAnalysisService>();
            services.AddScoped<IAnalysisService, FakeAnalysisService>();

            services.RemoveAll<IConfigService>();
            services.AddScoped<IConfigService, FakeConfigService>();

            services.RemoveAll<ISkillService>();
            services.AddScoped<ISkillService, FakeSkillService>();

            services.RemoveAll<IBusinessWorkflowService>();
            services.AddScoped<IBusinessWorkflowService, FakeBusinessWorkflowService>();

            services.RemoveAll<IConfigurationSetService>();
            services.AddScoped<IConfigurationSetService, FakeConfigurationSetService>();

            services.RemoveAll<IAgentService>();
            services.AddScoped<IAgentService, FakeAgentService>();

            services.RemoveAll<IWorkflowAdminService>();
            services.AddScoped<IWorkflowAdminService, FakeWorkflowAdminService>();

            services.RemoveAll<IAgentRunService>();
            services.AddScoped<IAgentRunService, FakeAgentRunService>();

            services.RemoveAll<IOrchestratorRunService>();
            services.AddScoped<IOrchestratorRunService, FakeOrchestratorRunService>();
        });
    }

    /// <summary>簽出一個與 backend 位元相容、可通過 Bearer 中介軟體的測試 token。</summary>
    public string IssueToken(
        string username = "user-a", string role = "USER", string tenantCode = "demo-a",
        IReadOnlyCollection<string>? capabilities = null,
        IReadOnlyCollection<string>? groups = null)
        => TestTokens.Mint(
            username,
            role,
            tenantCode,
            capabilities: capabilities,
            groups: groups);
}

internal sealed class RemoteIpAddressStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use(async (context, following) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
                await following();
            });
            next(app);
        };
}
