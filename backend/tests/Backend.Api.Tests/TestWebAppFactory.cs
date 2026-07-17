using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Backend.Api.Auth;
using Backend.Api.Common;
using Backend.Api.Config;
using Backend.Api.Configuration;
using Backend.Api.Conversations;
using Backend.Api.Files;
using Backend.Api.Skills;
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
public sealed class TestWebAppFactory : WebApplicationFactory<Program>
{
    public const string InternalToken = "internal-dev-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthRepository>();
            services.AddScoped<IAuthRepository, FakeAuthRepository>();

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

            // Skill 驗證不打真的 workflow(:8001)。
            services.RemoveAll<ISkillValidator>();
            services.AddSingleton<ISkillValidator, FakeSkillValidator>();
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
        await processor.ProcessAsync(new DocumentMessage(id, tenantId, "seed-user", title, text), CancellationToken.None);
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

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())
           ?? throw new InvalidOperationException("回應 body 不是有效 JSON");
}
