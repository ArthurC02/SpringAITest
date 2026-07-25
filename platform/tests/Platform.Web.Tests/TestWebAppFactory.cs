using Platform.Service.Abstractions;
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
    private readonly bool _enableRateLimiting;
    private readonly bool _removeSessionIsolationProvider;
    private readonly bool _useDevelopmentEnvironment;
    private readonly bool _agentBuilderEnabled;
    private readonly bool _agentTestRunEnabled;
    private readonly IMem0Client? _mem0Override;

    public TestWebAppFactory()
    {
    }

    internal TestWebAppFactory(
        bool enableRateLimiting = false, bool removeSessionIsolationProvider = false,
        bool useDevelopmentEnvironment = false, bool agentBuilderEnabled = false,
        bool agentTestRunEnabled = false, IMem0Client? mem0Override = null)
    {
        _enableRateLimiting = enableRateLimiting;
        _removeSessionIsolationProvider = removeSessionIsolationProvider;
        _useDevelopmentEnvironment = useDevelopmentEnvironment;
        _agentBuilderEnabled = agentBuilderEnabled;
        _agentTestRunEnabled = agentTestRunEnabled;
        _mem0Override = mem0Override;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // P1 審查建議的釘子:AddAIAgent 的 hosted agent 若誤宣告 Scoped,Development 環境的
        // ValidateScopes=true 會在啟動期就炸——用真正的 "Development" 環境跑一次冒煙測試釘住
        // 「這個崩潰不會再發生」(見 DevelopmentEnvironmentSmokeTests)。
        builder.UseEnvironment(_useDevelopmentEnvironment
            ? "Development"
            : _enableRateLimiting ? "RateLimitingTesting" : "Testing");
        // Agent Builder feature flag(D1):預設關閉(fail-closed);需要走 /api/agents* 代理的測試以此開啟。
        builder.UseSetting("AGENT_BUILDER_ENABLED", _agentBuilderEnabled ? "true" : "false");
        builder.UseSetting("AGENT_TEST_RUN_ENABLED", _agentTestRunEnabled ? "true" : "false");
        builder.ConfigureTestServices(services =>
        {
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
            services.AddScoped<IAuthService, FakeAuthService>();

            services.RemoveAll<IWorkflowService>();
            services.AddScoped<IWorkflowService, FakeWorkflowService>();

            services.RemoveAll<IDocumentService>();
            services.AddScoped<IDocumentService, FakeDocumentService>();

            services.RemoveAll<IAnalysisService>();
            services.AddScoped<IAnalysisService, FakeAnalysisService>();

            services.RemoveAll<IConfigService>();
            services.AddScoped<IConfigService, FakeConfigService>();

            services.RemoveAll<ISkillService>();
            services.AddScoped<ISkillService, FakeSkillService>();

            services.RemoveAll<IConfigurationSetService>();
            services.AddScoped<IConfigurationSetService, FakeConfigurationSetService>();

            services.RemoveAll<IAgentService>();
            services.AddScoped<IAgentService, FakeAgentService>();

            services.RemoveAll<IAgentRunService>();
            services.AddScoped<IAgentRunService, FakeAgentRunService>();
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
