using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// D3 runtime 的 immutable execution artifact(<c>GET /api/skills/{name}/revisions/{rev}/execution-artifact</c>)。
/// 這是跨服務契約端點:workflow 的 <c>app/runtime/artifacts.py</c> 對帶 package 的 pinned skill
/// 硬性要求 <c>package_base64</c> + <c>package_sha256</c> 同時存在,缺任一就 ArtifactError。
/// </summary>
public sealed class SkillExecutionArtifactTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillExecutionArtifactTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithTenant(tenant).WithRole("ADMIN").WithUser("admin-a");

    private FakeSkillPackageValidator Pkg => (FakeSkillPackageValidator)_factory.Fake<ISkillPackageValidator>();

    private static byte[] Zip(string entryName, string text)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task ExactRevisionArtifact_DoesNotDriftWhenCurrentUpdates()
    {
        var client = Admin();
        var name = $"artifact-{Guid.NewGuid():N}";
        var rev1 = $"name: {name}\ndescription: 第一版\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        var rev2 = $"name: {name}\ndescription: 第二版\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/skills", new { definition = rev1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/skills/{name}", new { definition = rev2 })).StatusCode);

        var response = await client.GetAsync(
            $"/api/skills/{name}/revisions/1/execution-artifact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifact = await response.ReadJsonAsync();
        Assert.Equal(1, artifact["revision"]!.GetValue<int>());
        Assert.Equal(rev1, artifact["definition"]!.GetValue<string>());
        Assert.Equal(
            SkillHash.Sha256(rev1),
            artifact["definition_sha256"]!.GetValue<string>());
        Assert.Null(artifact["package_base64"]);
    }

    [Fact]
    public async Task ExactRevisionArtifact_IsTenantScoped_AndAdminOnly()
    {
        var owner = Admin();
        var name = $"artifact-private-{Guid.NewGuid():N}";
        var yaml = $"name: {name}\ndescription: private\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        _ = await owner.PostAsJsonAsync("/api/skills", new { definition = yaml });

        Assert.Equal(HttpStatusCode.NotFound, (await Admin("demo-b").GetAsync(
            $"/api/skills/{name}/revisions/1/execution-artifact")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateInternalClient()
            .WithTenant("demo-a").WithRole("USER").WithUser("user-a").GetAsync(
                $"/api/skills/{name}/revisions/1/execution-artifact")).StatusCode);
    }

    // 帶 package 的 revision(agentic,以及匯入時附 package 的 flow)必須同時給出 package_base64
    // 與 package_sha256:workflow 的 artifacts.py 對 agentic 缺欄位會拋
    // 「pinned agentic skill has no immutable package」,對只給一半的 flow 會拋
    // 「pinned flow package metadata is incomplete」。base64 必須逐 byte 還原原始 zip。
    [Theory]
    [InlineData("agentic")]
    [InlineData("flow")]
    public async Task ExecutionArtifact_RevisionWithPackage_ReturnsBase64PackageAndSha(string kind)
    {
        var name = $"artifact-pkg-{kind}-{Guid.NewGuid():N}";
        var canonical = $"name: {name}\ndescription: 打包版\nmetadata:\n  kind: {kind}\n";
        var zip = Zip("SKILL.md", $"---\nname: {name}\n---\n內文\0bytes 中文");
        Pkg.Setup(name, _ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "打包版", "USER", kind), canonical));

        var fileContent = new ByteArrayContent(zip);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var imported = await Admin().PostAsync(
            $"/api/skills/{name}/import",
            new MultipartFormDataContent { { fileContent, "package", "pkg.zip" } });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var response = await Admin().GetAsync($"/api/skills/{name}/revisions/1/execution-artifact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifact = await response.ReadJsonAsync();
        Assert.Equal(kind, artifact["kind"]!.GetValue<string>());
        Assert.Equal(canonical, artifact["definition"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(zip), artifact["package_sha256"]!.GetValue<string>());
        Assert.Equal(zip, Convert.FromBase64String(artifact["package_base64"]!.GetValue<string>()));
    }

    // revision 0 / 負數 / current+1 都是「不存在」的同一格:404,訊息指名 name#revision。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task ExecutionArtifact_UnknownRevision_Returns404(int revision)
    {
        var client = Admin();
        var name = $"artifact-rev404-{Guid.NewGuid():N}";
        var yaml = $"name: {name}\ndescription: d\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/skills", new { definition = yaml })).StatusCode);

        var response = await client.GetAsync($"/api/skills/{name}/revisions/{revision}/execution-artifact");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"找不到 Skill revision：{name}#{revision}",
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 完整性守門(fail-closed):儲存的 hash 與內容對不上就不得交付 artifact —— runtime 會拿它
    // 當「已固定且可驗證」的執行輸入。共用的 in-memory repo 一律即時重算 sha,這格只能用一個
    // 專門說謊的 stub repo 打到,故不進共用骨架。
    [Theory]
    [InlineData("definition", "Skill revision definition hash mismatch")]
    [InlineData("package", "Skill revision package hash mismatch")]
    public async Task ExecutionArtifact_HashMismatch_FailsClosed(string broken, string message)
    {
        var row = broken == "definition"
            ? new StoredSkillRevision(
                1, "name: liar\nflow: []\n", SkillHash.Sha256("完全不是這份 definition"),
                "admin-a", DateTime.UtcNow, "flow", null, null)
            // legacy 資料形狀:package 已遺失但 package_sha256 還在 → 同樣不得放行。
            : new StoredSkillRevision(
                1, "name: liar\nflow: []\n", SkillHash.Sha256("name: liar\nflow: []\n"),
                "admin-a", DateTime.UtcNow, "agentic", null, SkillHash.Sha256("遺失的 package"));
        using var factory = new StubRevisionFactory(row);

        var response = await factory.CreateInternalClient()
            .WithTenant("demo-a").WithRole("ADMIN").WithUser("admin-a")
            .GetAsync("/api/skills/liar/revisions/1/execution-artifact");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(500, body["status"]!.GetValue<int>());
        Assert.Contains(message, body["message"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);
    }

    /// <summary>只把 ISkillRepository 換成會回傳「hash 對不上」列的 stub;其餘沿用共用測試骨架。</summary>
    private sealed class StubRevisionFactory : TestWebAppFactory
    {
        private readonly StoredSkillRevision _row;

        public StubRevisionFactory(StoredSkillRevision row) => _row = row;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISkillRepository>();
                services.AddSingleton<ISkillRepository>(new StubRevisionRepository(_row));
            });
        }
    }

    /// <summary>只實作 GetRevisionAsync(本檔唯一會走到的方法),其餘刻意不支援。</summary>
    private sealed class StubRevisionRepository : ISkillRepository
    {
        private readonly StoredSkillRevision _row;

        public StubRevisionRepository(StoredSkillRevision row) => _row = row;

        public Task<StoredSkillRevision?> GetRevisionAsync(
            string tenantId, string name, int revision, CancellationToken ct)
            => Task.FromResult<StoredSkillRevision?>(_row);

        public Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> CreateAsync(
            string tenantId, string expectedExistingKind, Skill skill, string createdBy, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> UpdateAsync(
            string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> UpdateAsync(
            string tenantId, string name, string expectedKind, Skill skill, string updatedBy,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Skill?> ImportAsync(
            string tenantId, Skill skill, byte[]? package, string? packageSha256,
            string createdBy, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string tenantId, string name, string kind, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(
            string tenantId, string name, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
