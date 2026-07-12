using System.Collections.Concurrent;
using Backend.Api.Analysis;
using Backend.Api.Auth;
using Backend.Api.Config;
using Backend.Api.Conversations;
using Backend.Api.Files;
using Backend.Api.Retrieval;

namespace Backend.Api.Tests;

/// <summary>
/// 認證儲存庫 fake:預置兩租戶與三名種子使用者(密碼皆 password123 的 BCrypt hash),
/// 用來測 register/login 的錯誤映射與 JWT 簽發,不碰真 DB。
/// </summary>
public sealed class FakeAuthRepository : IAuthRepository
{
    private static readonly string Password123 = BCrypt.Net.BCrypt.HashPassword("password123");

    private readonly Dictionary<string, TenantRow> _tenants = new()
    {
        ["demo-a"] = new TenantRow(1, "demo-a", "示範租戶 A", "demo-a-invite"),
        ["demo-b"] = new TenantRow(2, "demo-b", "示範租戶 B", "demo-b-invite"),
    };

    private readonly Dictionary<string, UserRow> _users = new()
    {
        ["admin-a"] = new UserRow("admin-a", Password123, "ADMIN", "demo-a"),
        ["user-a"] = new UserRow("user-a", Password123, "USER", "demo-a"),
        ["user-b"] = new UserRow("user-b", Password123, "USER", "demo-b"),
    };

    public Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct)
        => Task.FromResult(_tenants.GetValueOrDefault(code));

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct)
        => Task.FromResult(_users.ContainsKey(username));

    public Task AddUserAsync(string username, string passwordHash, string role, long tenantId, CancellationToken ct)
    {
        var tenantCode = _tenants.Values.First(t => t.Id == tenantId).Code;
        _users[username] = new UserRow(username, passwordHash, role, tenantCode);
        return Task.CompletedTask;
    }

    public Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct)
        => Task.FromResult(_users.GetValueOrDefault(username));
}

/// <summary>對話儲存庫 fake:行程記憶體遞增 id。</summary>
public sealed class FakeConversationRepository : IConversationRepository
{
    private readonly List<ConversationItem> _items = new();
    private long _seq;

    public Task<ConversationCreated> AddAsync(string prompt, string reply, CancellationToken ct)
    {
        var id = ++_seq;
        var now = DateTime.UtcNow;
        _items.Add(new ConversationItem(id, reply, now));
        return Task.FromResult(new ConversationCreated(id, now));
    }

    public Task<IReadOnlyList<ConversationItem>> ListDescAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConversationItem>>(
            _items.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).ToList());
}

/// <summary>rag 儲存庫 fake:行程記憶體,per-tenant 隔離;不做真的向量距離(score 固定)。
/// 追蹤 status,支援非同步處理三步(processing → ready / failed)。</summary>
public sealed class FakeRagRepository : IRagRepository
{
    private sealed class Doc
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string Title { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int ChunkCount { get; set; }
        public string Status { get; set; } = "processing";
        public List<string> Chunks { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, Doc> _docs = new();

    public Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct)
        => Task.FromResult(_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId ? d.Status : null);

    public Task InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct)
    {
        // ON CONFLICT (id) DO NOTHING:已存在則保留(重複投遞不覆寫既有狀態)。
        _docs.TryAdd(documentId, new Doc
        {
            Id = documentId, TenantId = tenantId, Title = title, CreatedAt = DateTime.UtcNow,
            ChunkCount = 0, Status = "processing",
        });
        return Task.CompletedTask;
    }

    public Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct)
    {
        if (_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId)
        {
            d.Chunks = chunks.ToList();
            d.ChunkCount = chunks.Count;
            d.Status = "ready";
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(string documentId, string tenantId, CancellationToken ct)
    {
        if (_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId)
        {
            d.Status = "failed";
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocumentInfo>>(
            _docs.Values.Where(d => d.TenantId == tenantId).OrderBy(d => d.CreatedAt)
                .Select(d => new DocumentInfo(d.Id, d.Title, d.ChunkCount, d.CreatedAt, d.Status)).ToList());

    public Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct)
        => Task.FromResult(_docs.TryGetValue(docId, out var d) && d.TenantId == tenantId && _docs.TryRemove(docId, out _));

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string tenantId, float[] queryEmbedding, int topK, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RetrievedChunk>>(
            _docs.Values.Where(d => d.TenantId == tenantId)
                .SelectMany(d => d.Chunks.Select(c => new RetrievedChunk(d.Id, d.Title, c, 1.0)))
                .Take(topK).ToList());

    public Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct)
    {
        var mine = _docs.Values.Where(d => d.TenantId == tenantId).ToList();
        var titles = mine.OrderByDescending(d => d.CreatedAt).Take(5).Select(d => d.Title).ToList();
        return Task.FromResult(new AnalysisSummary(mine.Count, mine.Sum(d => d.ChunkCount), titles));
    }
}

/// <summary>組態儲存庫 fake:行程記憶體 key-value。</summary>
public sealed class FakeConfigRepository : IConfigRepository
{
    private readonly ConcurrentDictionary<string, ConfigItem> _store = new();

    public Task<IReadOnlyList<ConfigItem>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConfigItem>>(_store.Values.OrderBy(i => i.Key).ToList());

    public Task<ConfigItem> UpsertAsync(string key, string value, CancellationToken ct)
    {
        var item = new ConfigItem(key, value, DateTime.UtcNow);
        _store[key] = item;
        return Task.FromResult(item);
    }
}
