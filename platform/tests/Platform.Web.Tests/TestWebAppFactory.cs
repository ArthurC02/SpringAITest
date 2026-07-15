using Platform.Service.Abstractions;
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
public sealed class TestWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // 對外相依:LLM 與 mem0(真實 ChatService 仍會用到)。
            services.RemoveAll<ILlmAgent>();
            services.AddSingleton<ILlmAgent, FakeLlmAgent>();

            services.RemoveAll<IMem0Client>();
            services.AddSingleton<IMem0Client, FakeMem0Client>();

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
        });
    }

    /// <summary>簽出一個與 backend 位元相容、可通過 Bearer 中介軟體的測試 token。</summary>
    public string IssueToken(string username = "user-a", string role = "USER", string tenantCode = "demo-a")
        => TestTokens.Mint(username, role, tenantCode);
}
