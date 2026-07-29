using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Auth;
using Backend.Api.Common;
using Backend.Api.Config;
using Backend.Api.Configuration;
using Backend.Api.Conversations;
using Backend.Api.Files;
using Backend.Api.Skills;
using Backend.Api.Workflows;
using Backend.Api.Orchestrators;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.OperationsGovernance;
using Backend.Api.Data.InMemory;
using Backend.Api.Contexts;
using Backend.Api.PromptArtifacts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// 以「Testing」環境啟動真實應用程式(真實驗證、內部憑證守門、例外映射),
/// 但把 Dapper 儲存庫換成行程記憶體 fake,不連真 DB(Testing 環境亦跳過 DbBootstrap)。
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    public const string InternalToken = "internal-dev-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("WORKFLOW_DESIGNER_ENABLED", "true");
        builder.UseSetting("MULTI_AGENT_DISPATCH_ENABLED", "true");
        builder.UseSetting("AGENT_WRITE_TOOLS_ENABLED", "true");
        builder.UseSetting("CONTEXT_ENRICHMENT_ENABLED", "true");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthRepository>();
            services.AddSingleton<IAuthRepository, FakeAuthRepository>();

            services.RemoveAll<IConversationRepository>();
            services.AddSingleton<IConversationRepository, FakeConversationRepository>();

            services.RemoveAll<IRagRepository>();
            services.AddSingleton<IRagRepository, FakeRagRepository>();

            services.RemoveAll<IConfigRepository>();
            services.AddSingleton<IConfigRepository, FakeConfigRepository>();

            services.RemoveAll<ISkillRepository>();
            services.AddSingleton<ISkillRepository, FakeSkillRepository>();

            services.RemoveAll<IConfigurationSetRepository>();
            services.AddSingleton<IConfigurationSetRepository, FakeConfigurationSetRepository>();

            services.RemoveAll<IAgentRepository>();
            services.AddSingleton<IAgentRepository, FakeAgentRepository>();

            services.RemoveAll<IAgentRunRepository>();
            services.AddSingleton<IAgentRunRepository, FakeAgentRunRepository>();
            services.RemoveAll<IAgentRunApprovalRepository>();
            services.AddSingleton<IAgentRunApprovalRepository, InMemoryAgentRunApprovalRepository>();

            services.RemoveAll<IWorkflowRepository>();
            services.AddSingleton<IWorkflowRepository, InMemoryWorkflowRepository>();
            services.RemoveAll<IOrchestratorRepository>();
            services.AddSingleton<IOrchestratorRepository, InMemoryOrchestratorRepository>();
            services.RemoveAll<IRuntimeBindingRepository>();
            services.AddSingleton<IRuntimeBindingRepository, InMemoryRuntimeBindingRepository>();
            services.RemoveAll<IOperationsGovernanceRepository>();
            services.AddSingleton<IOperationsGovernanceRepository, InMemoryOperationsGovernanceRepository>();
            services.RemoveAll<IEvalRepository>();
            services.AddSingleton<IEvalRepository, InMemoryEvalRepository>();
            services.RemoveAll<IEvalRunner>();
            services.AddSingleton<IEvalRunner, FakeEvalRunner>();
            services.RemoveAll<IWorkflowCompiler>();
            services.AddSingleton<IWorkflowCompiler, FakeWorkflowCompiler>();

            services.RemoveAll<IContextRepository>();
            services.AddSingleton<IContextRepository, InMemoryContextRepository>();

            services.RemoveAll<IPromptArtifactRepository>();
            services.AddSingleton<IPromptArtifactRepository, InMemoryPromptArtifactRepository>();

            // Skill 驗證不打真的 workflow(:8001)。
            services.RemoveAll<ISkillValidator>();
            services.AddSingleton<ISkillValidator, FakeSkillValidator>();

            services.RemoveAll<IBusinessRuleValidator>();
            services.AddSingleton<IBusinessRuleValidator, FakeBusinessRuleValidator>();

            // Agent Skill package 驗證同樣不打真 workflow;fake 依 expected_name 腳本化 valid/invalid/unreachable。
            services.RemoveAll<ISkillPackageValidator>();
            services.AddSingleton<ISkillPackageValidator, FakeSkillPackageValidator>();
        });
    }

    /// <summary>取單例 fake(斷言 revision 稽核列/validate 呼叫紀錄用)。</summary>
    public T Fake<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>建立已帶 X-Internal-Token 的 client(通過守門);可再加身分 header。</summary>
    public HttpClient CreateInternalClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, InternalToken);
        return client;
    }

    /// <summary>
    /// 直接以 DocumentProcessor 種入一份已處理完成的文件(取代已移除的 POST 端點),
    /// 供讀取/檢索/刪除的整合測試使用。回傳文件 id。
    /// </summary>
    public async Task<string> SeedDocumentAsync(string tenantId, string title, string text)
    {
        var id = Guid.NewGuid().ToString();
        using var scope = Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
        await processor.ProcessAsync(new DocumentMessage(id, tenantId, "seed-user", title, text), retryCount: 0, CancellationToken.None);
        return id;
    }
}

internal static class TestHelpers
{
    public static HttpClient WithTenant(this HttpClient client, string tenantCode)
    {
        client.DefaultRequestHeaders.Add(IdentityHeaders.TenantHeader, tenantCode);
        return client;
    }

    public static HttpClient WithRole(this HttpClient client, string role)
    {
        client.DefaultRequestHeaders.Add(IdentityHeaders.RoleHeader, role);
        return client;
    }

    public static HttpClient WithUser(this HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Add(IdentityHeaders.UserHeader, userId);
        return client;
    }

    public static HttpClient WithGroups(this HttpClient client, params string[] groups)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            IdentityHeaders.GroupsHeader,
            string.Join(' ', groups));
        return client;
    }

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())
           ?? throw new InvalidOperationException("回應 body 不是有效 JSON");
}
