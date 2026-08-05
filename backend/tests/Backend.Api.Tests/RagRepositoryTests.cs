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
        await conn.ExecuteAsync(
            "DELETE FROM document_ingest WHERE tenant_id LIKE 'ragrepo-%';"
            + " DELETE FROM rag_documents WHERE tenant_id LIKE 'ragrepo-%'");
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

    private NpgsqlDataSource CreateObservedDataSource(string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fx.ConnectionString)
        {
            ApplicationName = applicationName,
            MaxPoolSize = 1,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private async Task AssertBlockedOnDatabaseLockAsync(string applicationName, Task operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = await _fx.DataSource!.OpenConnectionAsync(timeout.Token);
        while (true)
        {
            Assert.False(operation.IsCompleted, "operation completed before the expected PostgreSQL row lock wait");
            var blocked = await observer.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity"
                + " WHERE datname = current_database() AND application_name = @applicationName"
                + " AND wait_event_type = 'Lock')",
                new { applicationName }, cancellationToken: timeout.Token));
            if (blocked)
            {
                return;
            }
            await Task.Yield();
        }
    }

    [SkippableFact]
    public async Task AllocateDocumentAsync_CreateReplayConflictAndConcurrentCalls_AreStable()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-ingest-a";
        const string user = "user-a";
        var keyHash = DocumentIngestIdentity.HashKey("raw-key-never-persisted");
        var requestHash = DocumentIngestIdentity.HashRequest("title", "text");

        var allocations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Repo.AllocateDocumentAsync(tenant, user, keyHash, requestHash, "title", default)));

        Assert.Single(allocations, x => x.Status == DocumentIngestAllocationStatus.Created);
        Assert.Equal(7, allocations.Count(x => x.Status == DocumentIngestAllocationStatus.Replay));
        Assert.Single(allocations.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal));
        Assert.Empty(await Repo.ListDocumentsAsync(tenant, default)); // pending_publish stays private.
        await using (var conn = await _fx.DataSource!.OpenConnectionAsync())
        {
            var persisted = await conn.QuerySingleAsync<string>(
                "SELECT idempotency_key_sha256 FROM document_ingest WHERE tenant_id = @tenant",
                new { tenant });
            Assert.Equal(keyHash, persisted);
            Assert.DoesNotContain("raw-key-never-persisted", persisted, StringComparison.Ordinal);
        }

        var conflict = await Repo.AllocateDocumentAsync(
            tenant,
            user,
            keyHash,
            DocumentIngestIdentity.HashRequest("title", "changed"),
            "title",
            default);
        Assert.Equal(DocumentIngestAllocationStatus.PayloadConflict, conflict.Status);
        Assert.Equal(allocations[0].DocumentId, conflict.DocumentId);
    }

    [SkippableFact]
    public async Task DeleteDocumentAsync_TombstonesAndFencesCompletionFailureAndReplay()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-tombstone-a";
        const string user = "user-a";
        var keyHash = DocumentIngestIdentity.HashKey("delete-key");
        var requestHash = DocumentIngestIdentity.HashRequest("title", "text");
        var allocated = await Repo.AllocateDocumentAsync(tenant, user, keyHash, requestHash, "title", default);
        var documentId = allocated.DocumentId;
        var guid = Guid.Parse(documentId);
        await Repo.InsertProcessingDocumentAsync(documentId, tenant, "title", default);
        await Repo.CompleteDocumentAsync(documentId, tenant, new[] { "old" }, new[] { OneHot(0) }, default);
        Assert.Equal(1, await ChunkCountAsync(guid));

        Assert.True(await Repo.DeleteDocumentAsync(tenant, documentId, default));
        Assert.False(await Repo.DeleteDocumentAsync(tenant, documentId, default));
        Assert.Equal(0, await ChunkCountAsync(guid));
        Assert.Equal("deleted", await Repo.GetDocumentStatusAsync(documentId, tenant, default));

        await Repo.CompleteDocumentAsync(documentId, tenant, new[] { "late" }, new[] { OneHot(0) }, default);
        await Repo.MarkFailedAsync(documentId, tenant, default);
        Assert.Equal("deleted", await Repo.GetDocumentStatusAsync(documentId, tenant, default));
        Assert.Equal(0, await ChunkCountAsync(guid));
        Assert.Empty(await Repo.ListDocumentsAsync(tenant, default));
        Assert.Empty(await Repo.SearchAsync(tenant, OneHot(0), 10, default));

        var replay = await Repo.AllocateDocumentAsync(tenant, user, keyHash, requestHash, "title", default);
        Assert.Equal(DocumentIngestAllocationStatus.DeletedConflict, replay.Status);
        Assert.Equal(documentId, replay.DocumentId);
    }

    [SkippableFact]
    public async Task Completion_BlocksBehindUncommittedDelete_ThenCannotResurrect()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-race-delete-first";
        var documentId = Guid.NewGuid();
        await CompleteAsync(
            tenant, documentId.ToString("D"), "doc", new[] { "old" }, new[] { OneHot(0) });

        await using var holder = await _fx.DataSource!.OpenConnectionAsync();
        await using var deleteTx = await holder.BeginTransactionAsync();
        Assert.Equal(1, await holder.ExecuteAsync(
            "UPDATE rag_documents SET status = 'deleted', chunk_count = 0"
            + " WHERE id = @documentId AND tenant_id = @tenant",
            new { documentId, tenant }, deleteTx));

        var applicationName = "ragrepo-complete-wait-" + Guid.NewGuid().ToString("N");
        await using var observedSource = CreateObservedDataSource(applicationName);
        var completion = new RagRepository(observedSource).CompleteDocumentAsync(
            documentId.ToString("D"), tenant, new[] { "late" }, new[] { OneHot(0) }, default);
        await AssertBlockedOnDatabaseLockAsync(applicationName, completion);

        await holder.ExecuteAsync(
            "DELETE FROM rag_chunks WHERE document_id = @documentId AND tenant_id = @tenant",
            new { documentId, tenant }, deleteTx);
        await deleteTx.CommitAsync();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("deleted", await Repo.GetDocumentStatusAsync(documentId.ToString("D"), tenant, default));
        Assert.Equal(0, await ChunkCountAsync(documentId));
    }

    [SkippableFact]
    public async Task Delete_BlocksBehindCompletionRowLock_ThenWinsTerminally()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-race-complete-first";
        var documentId = Guid.NewGuid();
        await CompleteAsync(
            tenant, documentId.ToString("D"), "doc", new[] { "completed" }, new[] { OneHot(0) });

        await using var holder = await _fx.DataSource!.OpenConnectionAsync();
        await using var completionTx = await holder.BeginTransactionAsync();
        Assert.Equal("ready", await holder.QuerySingleAsync<string>(
            "SELECT status FROM rag_documents"
            + " WHERE id = @documentId AND tenant_id = @tenant FOR UPDATE",
            new { documentId, tenant }, completionTx));

        var applicationName = "ragrepo-delete-wait-" + Guid.NewGuid().ToString("N");
        await using var observedSource = CreateObservedDataSource(applicationName);
        var deletion = new RagRepository(observedSource).DeleteDocumentAsync(
            tenant, documentId.ToString("D"), default);
        await AssertBlockedOnDatabaseLockAsync(applicationName, deletion);

        await holder.ExecuteAsync(
            "UPDATE rag_documents SET chunk_count = 1, status = 'ready'"
            + " WHERE id = @documentId AND tenant_id = @tenant",
            new { documentId, tenant }, completionTx);
        await completionTx.CommitAsync();
        Assert.True(await deletion.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("deleted", await Repo.GetDocumentStatusAsync(documentId.ToString("D"), tenant, default));
        Assert.Equal(0, await ChunkCountAsync(documentId));
    }

    [SkippableFact]
    public async Task MarkFailed_BlocksBehindUncommittedDelete_ThenNoOps()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-race-failed";
        var documentId = Guid.NewGuid();
        Assert.True(await Repo.InsertProcessingDocumentAsync(
            documentId.ToString("D"), tenant, "doc", default));

        await using var holder = await _fx.DataSource!.OpenConnectionAsync();
        await using var deleteTx = await holder.BeginTransactionAsync();
        Assert.Equal(1, await holder.ExecuteAsync(
            "UPDATE rag_documents SET status = 'deleted', chunk_count = 0"
            + " WHERE id = @documentId AND tenant_id = @tenant",
            new { documentId, tenant }, deleteTx));

        var applicationName = "ragrepo-failed-wait-" + Guid.NewGuid().ToString("N");
        await using var observedSource = CreateObservedDataSource(applicationName);
        var markFailed = new RagRepository(observedSource).MarkFailedAsync(
            documentId.ToString("D"), tenant, default);
        await AssertBlockedOnDatabaseLockAsync(applicationName, markFailed);

        await holder.ExecuteAsync(
            "DELETE FROM rag_chunks WHERE document_id = @documentId AND tenant_id = @tenant",
            new { documentId, tenant }, deleteTx);
        await deleteTx.CommitAsync();
        await markFailed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("deleted", await Repo.GetDocumentStatusAsync(documentId.ToString("D"), tenant, default));
        Assert.Equal(0, await ChunkCountAsync(documentId));
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
    public async Task SearchScopedAsync_EnforcesTenantAndExactDocumentIds()
    {
        _fx.SkipIfUnavailable();
        const string tenantA = "ragrepo-scoped-a";
        const string tenantB = "ragrepo-scoped-b";
        var allowed = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await CompleteAsync(
            tenantA,
            allowed.ToString("D"),
            "allowed",
            new[] { "allowed" },
            new[] { OneHot(0) });
        await CompleteAsync(
            tenantA,
            denied.ToString("D"),
            "denied",
            new[] { "denied" },
            new[] { OneHot(0) });
        await CompleteAsync(
            tenantB,
            otherTenant.ToString("D"),
            "other",
            new[] { "other" },
            new[] { OneHot(0) });

        var scoped = await Repo.SearchScopedAsync(
            tenantA,
            OneHot(0),
            10,
            new[] { allowed, otherTenant },
            default);
        var empty = await Repo.SearchScopedAsync(
            tenantA,
            OneHot(0),
            10,
            Array.Empty<Guid>(),
            default);

        Assert.NotEmpty(scoped);
        Assert.All(scoped, item => Assert.Equal(allowed.ToString("D"), item.DocumentId));
        Assert.Empty(empty);
    }

    // ---- 邊界:LIMIT @topK 真的截斷結果(on-point topK = 命中數;off-point topK = 命中數 - 1) ----

    [SkippableFact]
    public async Task SearchAsync_TopKBelowMatchCount_TruncatesToTopK()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-topk-a";
        var docId = Guid.NewGuid().ToString();

        await CompleteAsync(tenant, docId, "topk",
            new[] { "hit", "far-1", "far-2" }, new[] { OneHot(0), OneHot(1), OneHot(2) });

        // on-point:topK 等於命中數 → 全數回傳,LIMIT 不砍。
        Assert.Equal(3, (await Repo.SearchAsync(tenant, OneHot(0), 3, default)).Count);

        // off-point:topK 少一 → 被 LIMIT 截斷;仍保留距離最近的那筆(OneHot(0) 對自己 cosine distance = 0,
        // 另兩筆正交 = 1,ORDER BY 下 "hit" 必為第一)。
        var truncated = await Repo.SearchAsync(tenant, OneHot(0), 2, default);
        Assert.Equal(2, truncated.Count);
        Assert.Equal("hit", truncated[0].Content);
    }

    // ---- 刪除:非法 GUID(不進 DB)與跨租戶(WHERE tenant_id 過濾)都回 false,且不得動到別人的資料 ----

    [SkippableFact]
    public async Task DeleteDocumentAsync_InvalidGuidOrOtherTenant_ReturnsFalse_KeepsDocument()
    {
        _fx.SkipIfUnavailable();
        const string tenantA = "ragrepo-delete-a";
        const string tenantB = "ragrepo-delete-b";
        var docId = Guid.NewGuid().ToString();
        var gid = Guid.Parse(docId);

        await CompleteAsync(tenantA, docId, "owned-by-a", new[] { "keep" }, new[] { OneHot(0) });
        Assert.Equal(1, await ChunkCountAsync(gid));

        // 等價類 1:Guid.TryParse 失敗 → 提早回 false,不做 DB 往返。
        Assert.False(await Repo.DeleteDocumentAsync(tenantA, "not-a-guid", default));

        // 等價類 2:合法 GUID 但屬於別的租戶 → 影響 0 列 → false,A 的文件與切塊完好。
        Assert.False(await Repo.DeleteDocumentAsync(tenantB, docId, default));
        Assert.Equal(1, await ChunkCountAsync(gid));
        Assert.Contains(await Repo.ListDocumentsAsync(tenantA, default), d => d.Id == docId);

        // 正控:同租戶刪得掉 → 證明上面兩個 false 不是「文件本來就刪不掉」。chunks 隨 ON DELETE CASCADE 消失。
        Assert.True(await Repo.DeleteDocumentAsync(tenantA, docId, default));
        Assert.Equal(0, await ChunkCountAsync(gid));
        Assert.DoesNotContain(await Repo.ListDocumentsAsync(tenantA, default), d => d.Id == docId);
    }

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

    // ---- (b') 重投遞的**建列**步驟本身的冪等:已 ready 的文件不得被打回 processing / 清空 chunk_count ----

    [SkippableFact]
    public async Task InsertProcessingDocumentAsync_OnAlreadyReadyDocument_IsNoOp()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "ragrepo-reinsert-a";
        var docId = Guid.NewGuid().ToString();
        var gid = Guid.Parse(docId);

        await CompleteAsync(tenant, docId, "original",
            new[] { "one", "two" }, new[] { OneHot(0), OneHot(1) });
        Assert.Equal(2, await ChunkCountAsync(gid));

        // platform 重投遞同一份 → ON CONFLICT (id) DO NOTHING:status / chunk_count / title / 切塊全不動,
        // 不會把已完成的文件重設成 processing + 0 chunk。
        await Repo.InsertProcessingDocumentAsync(docId, tenant, "redelivered-title", default);

        Assert.Equal(2, await ChunkCountAsync(gid));
        var doc = (await Repo.ListDocumentsAsync(tenant, default)).Single(d => d.Id == docId);
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount);
        Assert.Equal("original", doc.Title);
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
