using Backend.Api.Files;
using Dapper;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// RagRepository 的**真 PostgreSQL**驗收:所有其他 Rag 測試都走 FakeRagRepository,真 SQL
/// (多租戶隔離、批次 INSERT、DELETE→INSERT→UPDATE 的交易冪等/回滾)完全沒被背書。
/// 比照 ConfigurationSetRepositoryTests / AuthRepositoryTests 共用同一個 PostgresFixture
/// (appdb 不可達則 SkipIfUnavailable 略過,不假綠)。每測用自己的 "ragrepo-<case>-" 租戶。
/// </summary>
[Collection("Postgres")]
public sealed class RagRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public RagRepositoryTests(PostgresFixture fx) => _fx = fx;

    private RagRepository Repo => new(_fx.DataSource!);

    // 本類是唯一寫 'ragrepo-' 的測試,類內測試循序執行;每測前後自清,不放進共用 fixture
    // (共用 fixture 有多個實例並行 dispose,會刪掉本類正在跑的資料 → FK 違規)。chunks ON DELETE CASCADE。
    public async Task InitializeAsync() => await CleanupAsync();

    public async Task DisposeAsync() => await CleanupAsync();

    private async Task CleanupAsync()
    {
        if (!_fx.Available)
        {
            return;
        }

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM rag_documents WHERE tenant_id LIKE 'ragrepo-%'");
    }

    /// <summary>合法的 1536 維單位向量(index 位置為 1,其餘 0)。非退化 → cosine 不會 NaN。</summary>
    private static float[] OneHot(int index)
    {
        var v = new float[1536];
        v[index] = 1f;
        return v;
    }

    private async Task<int> ChunkCountAsync(Guid docId)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rag_chunks WHERE document_id = @docId", new { docId });
    }

    /// <summary>建列(processing)後 complete,對應真實流程(platform 先建列、消費者再 complete)。</summary>
    private async Task CompleteAsync(string tenant, string docId, string title, string[] chunks, float[][] embeddings)
    {
        var repo = Repo;
        await repo.InsertProcessingDocumentAsync(docId, tenant, title, default);
        await repo.CompleteDocumentAsync(docId, tenant, chunks, embeddings, default);
    }

    // ---- (a) 租戶隔離:A 的 chunk 不會出現在 B 的搜尋(WHERE c.tenant_id = @tenantId) ----

    [SkippableFact]
    public async Task SearchAsync_ExcludesOtherTenantsChunks()
    {
        _fx.SkipIfUnavailable();
        const string tenantA = "ragrepo-search-a";
        const string tenantB = "ragrepo-search-b";
        var docA = Guid.NewGuid().ToString();
        var docB = Guid.NewGuid().ToString();

        await CompleteAsync(tenantA, docA, "doc-a",
            new[] { "A-apple", "A-banana" }, new[] { OneHot(0), OneHot(1) });
        await CompleteAsync(tenantB, docB, "doc-b",
            new[] { "B-cat", "B-dog" }, new[] { OneHot(0), OneHot(1) });

        // B 的搜尋只看得到 B 的 chunk;A 的 document_id / 內容一律不得洩漏(即便向量完全相同)。
        var bResults = await Repo.SearchAsync(tenantB, OneHot(0), 10, default);
        Assert.NotEmpty(bResults);
        Assert.All(bResults, r => Assert.Equal(docB, r.DocumentId));
        Assert.All(bResults, r => Assert.StartsWith("B-", r.Content));

        // 對稱:A 的搜尋也只看得到 A 的。
        var aResults = await Repo.SearchAsync(tenantA, OneHot(0), 10, default);
        Assert.NotEmpty(aResults);
        Assert.All(aResults, r => Assert.Equal(docA, r.DocumentId));
    }

    // ---- (b) 重跑冪等:重複投遞同一份不得讓 chunk 累積(先 DELETE 再批次 INSERT) ----

    [SkippableFact]
    public async Task CompleteDocumentAsync_Rerun_IsIdempotent_NoChunkAccumulation()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-idem-a";
        var docId = Guid.NewGuid().ToString();
        var gid = Guid.Parse(docId);

        await CompleteAsync(tenant, docId, "doc",
            new[] { "one", "two", "three" }, new[] { OneHot(0), OneHot(1), OneHot(2) });
        Assert.Equal(3, await ChunkCountAsync(gid));

        // 同一份再跑一次 → 切塊被替換而非累積(仍 3,不是 6)。
        await Repo.CompleteDocumentAsync(docId, tenant,
            new[] { "one", "two", "three" }, new[] { OneHot(0), OneHot(1), OneHot(2) }, default);
        Assert.Equal(3, await ChunkCountAsync(gid));

        // chunk_count 欄與 status 一致落地(交易整體提交)。
        var doc = (await Repo.ListDocumentsAsync(tenant, default)).Single(d => d.Id == docId);
        Assert.Equal(3, doc.ChunkCount);
        Assert.Equal("ready", doc.Status);
    }

    // ---- (b) 交易回滾:批次 INSERT 中途失敗,DELETE 也一併回滾,既有 chunk/狀態不得被破壞 ----

    [SkippableFact]
    public async Task CompleteDocumentAsync_FailedRerun_RollsBack_KeepsPreviousChunks()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-tx-a";
        var docId = Guid.NewGuid().ToString();
        var gid = Guid.Parse(docId);

        await CompleteAsync(tenant, docId, "doc",
            new[] { "keep-1", "keep-2" }, new[] { OneHot(0), OneHot(1) });
        Assert.Equal(2, await ChunkCountAsync(gid));

        // 重跑時第二個 embedding 維度錯誤(3 != 1536)→ 批次 INSERT 失敗。
        // DELETE+INSERT+UPDATE 同屬一交易,整體回滾:既有 2 個 chunk 與 ready 狀態不得被破壞。
        await Assert.ThrowsAsync<PostgresException>(() =>
            Repo.CompleteDocumentAsync(docId, tenant,
                new[] { "new-1", "new-2" }, new[] { OneHot(0), new float[] { 1f, 2f, 3f } }, default));

        Assert.Equal(2, await ChunkCountAsync(gid));
        var doc = (await Repo.ListDocumentsAsync(tenant, default)).Single(d => d.Id == docId);
        Assert.Equal(2, doc.ChunkCount);
        Assert.Equal("ready", doc.Status);

        // 內容仍是舊的(未被 new-* 取代)。
        var results = await Repo.SearchAsync(tenant, OneHot(0), 10, default);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.StartsWith("keep-", r.Content));
    }

    // ---- 邊界:空 chunk 清單(批次 INSERT 的 count>0 守門的 off-point)→ ready + 0 chunk,不炸 ----

    [SkippableFact]
    public async Task CompleteDocumentAsync_EmptyChunks_MarksReadyWithZeroChunks()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-empty-a";
        var docId = Guid.NewGuid().ToString();
        var gid = Guid.Parse(docId);

        var repo = Repo;
        await repo.InsertProcessingDocumentAsync(docId, tenant, "empty", default);
        await repo.CompleteDocumentAsync(docId, tenant, Array.Empty<string>(), Array.Empty<float[]>(), default);

        Assert.Equal(0, await ChunkCountAsync(gid));
        var doc = (await repo.ListDocumentsAsync(tenant, default)).Single(d => d.Id == docId);
        Assert.Equal(0, doc.ChunkCount);
        Assert.Equal("ready", doc.Status);
    }
}
