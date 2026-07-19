using System.Collections.Concurrent;
using Backend.Api.Analysis;
using Backend.Api.Auth;
using Backend.Api.Common;
using Backend.Api.Config;
using Backend.Api.Configuration;
using Backend.Api.Conversations;
using Backend.Api.Files;
using Backend.Api.Retrieval;
using Backend.Api.Skills;

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

/// <summary>對話儲存庫 fake:行程記憶體遞增 id,以 (tenant_id, user_id) 忠實模擬隔離。</summary>
public sealed class FakeConversationRepository : IConversationRepository
{
    private readonly List<(string Tenant, string User, ConversationItem Item)> _items = new();
    private long _seq;

    public Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct)
    {
        var id = ++_seq;
        var now = DateTime.UtcNow;
        _items.Add((tenantId, userId, new ConversationItem(id, reply, now)));
        return Task.FromResult(new ConversationCreated(id, now));
    }

    public Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConversationItem>>(
            _items.Where(e => e.Tenant == tenantId && e.User == userId).Select(e => e.Item)
                .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).ToList());
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

/// <summary>單調遞增假時鐘:每次 Now() 前進一秒,避開 Windows ~15ms 解析度撞值。
/// 斷言只驗排序、不依賴具體時戳,故 Epoch 任選;每個 fake 各持一個實例(各自獨立序列)。</summary>
internal sealed class MonotonicClock
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private long _tick;

    public DateTime Now() => Epoch.AddSeconds(Interlocked.Increment(ref _tick));
}

/// <summary>
/// Skill 儲存庫 fake:行程記憶體,key = (租戶, 名稱) — 忠實模擬 DB 的 UNIQUE (tenant_id, name) 與租戶過濾。
/// 時間戳用單調遞增的假時鐘(DateTime.UtcNow 在 Windows 只有 ~15ms 解析度,同一測試內兩次寫入可能撞到同值)。
/// 忠實模擬三件事實:軟刪(enabled=false,列仍在;清單/單筆看不到)、revision 遞增、
/// skill_revision 永不刪且軟刪後仍查得到。
/// </summary>
public sealed class FakeSkillRepository : ISkillRepository
{
    private readonly ConcurrentDictionary<(string Tenant, string Name), Skill> _store = new();

    /// <summary>稽核表:只增不減(軟刪不動它)。</summary>
    private readonly List<(string Tenant, string Name, SkillRevisionInfo Row)> _revisions = new();

    private readonly MonotonicClock _clock = new();

    private DateTime Now() => _clock.Now();

    public Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SkillInfo>>(
            _store.Where(e => e.Key.Tenant == tenantId).Select(e => e.Value).Where(s => s.Enabled)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new SkillInfo(
                    s.Name, s.Description, s.RequiredRole, s.Enabled, s.CurrentRevision, s.CreatedAt, s.UpdatedAt))
                .ToList());

    public Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct)
    {
        var skill = _store.GetValueOrDefault((tenantId, name));
        // 軟刪後不可見(WHERE ... AND enabled)。
        return Task.FromResult(skill is { Enabled: true } ? skill : null);
    }

    /// <summary>
    /// 忠實模擬 ON CONFLICT (tenant_id, name) DO UPDATE ... WHERE NOT skill.enabled:
    /// 同名仍啟用 → 0 列 → null(不寫 revision);同名已軟刪 → 復活並把 revision 接著加。
    /// </summary>
    public Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct)
    {
        var now = Now();

        if (_store.TryGetValue((tenantId, skill.Name), out var existing))
        {
            if (existing.Enabled)
            {
                return Task.FromResult<Skill?>(null);
            }

            // 軟刪的名字可以重用:同一列復活,稽核鏈不斷號。
            var revived = skill with
            {
                CurrentRevision = existing.CurrentRevision + 1,
                Enabled = true,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
            };
            _store[(tenantId, skill.Name)] = revived;
            AddRevision(tenantId, revived, createdBy);
            return Task.FromResult<Skill?>(revived);
        }

        var stored = skill with { CurrentRevision = 1, Enabled = true, CreatedAt = now, UpdatedAt = now };
        _store[(tenantId, skill.Name)] = stored;
        AddRevision(tenantId, stored, createdBy);
        return Task.FromResult<Skill?>(stored);
    }

    public Task<Skill?> UpdateAsync(string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct)
    {
        // 已軟刪的 skill 不可經 PUT 復活(WHERE ... AND enabled)。
        if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
        {
            return Task.FromResult<Skill?>(null);
        }

        // name 不可經 PUT 改變(以路由的 name 為準);updated_at 必變動;current_revision +1。
        var stored = skill with
        {
            Name = name,
            Enabled = true,
            CurrentRevision = existing.CurrentRevision + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = Now(),
        };
        _store[(tenantId, name)] = stored;
        AddRevision(tenantId, stored, updatedBy);
        return Task.FromResult<Skill?>(stored);
    }

    /// <summary>軟刪:enabled=false;列與 revision 都留著。已停用/不存在 → false。</summary>
    public Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct)
    {
        if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
        {
            return Task.FromResult(false);
        }

        _store[(tenantId, name)] = existing with { Enabled = false, UpdatedAt = Now() };
        return Task.FromResult(true);
    }

    /// <summary>不過濾 enabled — 軟刪後歷史仍查得到;依 revision 遞減。</summary>
    public Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(
        string tenantId, string name, CancellationToken ct)
    {
        lock (_revisions)
        {
            return Task.FromResult<IReadOnlyList<SkillRevisionInfo>>(
                _revisions.Where(r => r.Tenant == tenantId && r.Name == name)
                    .Select(r => r.Row).OrderByDescending(r => r.Revision).ToList());
        }
    }

    private void AddRevision(string tenantId, Skill stored, string createdBy)
    {
        lock (_revisions)
        {
            _revisions.Add((tenantId, stored.Name, new SkillRevisionInfo(
                stored.CurrentRevision, stored.Definition, SkillHash.Sha256(stored.Definition),
                createdBy, Now())));
        }
    }
}

/// <summary>
/// Configuration Set 儲存庫 fake:行程記憶體,忠實模擬 DB 的 UNIQUE (tenant_id, name)、租戶過濾、
/// 「一租戶至多一 active」(activate 先關其餘再開目標)、刪 active 後不自動改選、values 僅在單筆回傳。
/// **DB 級不變量(部分唯一索引兜底、原子 activate 的並發正確性)不在此 fake 的職責** — 那些由
/// ConfigurationSetRepositoryTests 打真 PostgreSQL 驗;此 fake 只服務 controller 層(授權/驗證/形狀/租戶)。
/// 時間戳用單調遞增假時鐘(避開 Windows ~15ms 解析度撞值)。
/// </summary>
public sealed class FakeConfigurationSetRepository : IConfigurationSetRepository
{
    private sealed record Entry(
        Guid Id, string Tenant, string Name, bool IsActive,
        Dictionary<string, object> Values, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

    private readonly Dictionary<Guid, Entry> _store = new();

    private readonly MonotonicClock _clock = new();

    private DateTime Now() => _clock.Now();

    private static ConfigurationSet ToDto(Entry e) => new(
        e.Id, e.Name, e.IsActive, new Dictionary<string, object>(e.Values), e.CreatedBy, e.CreatedAt, e.UpdatedAt);

    public Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        lock (_store)
        {
            return Task.FromResult<IReadOnlyList<ConfigurationSetInfo>>(
                _store.Values.Where(e => e.Tenant == tenantId).OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => new ConfigurationSetInfo(e.Id, e.Name, e.IsActive, e.CreatedAt, e.UpdatedAt))
                    .ToList());
        }
    }

    public Task<ConfigurationSet?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            var e = _store.GetValueOrDefault(id);
            return Task.FromResult(e is not null && e.Tenant == tenantId ? ToDto(e) : null);
        }
    }

    public Task<ConfigurationSet?> GetActiveAsync(string tenantId, CancellationToken ct)
    {
        lock (_store)
        {
            var e = _store.Values.FirstOrDefault(x => x.Tenant == tenantId && x.IsActive);
            return Task.FromResult(e is not null ? ToDto(e) : null);
        }
    }

    public Task<ConfigurationSet?> CreateAsync(
        string tenantId, string name, IReadOnlyDictionary<string, object> values, string createdBy, CancellationToken ct)
    {
        lock (_store)
        {
            if (_store.Values.Any(e => e.Tenant == tenantId && e.Name == name))
            {
                return Task.FromResult<ConfigurationSet?>(null); // UNIQUE (tenant_id, name)
            }

            var now = Now();
            var entry = new Entry(
                Guid.NewGuid(), tenantId, name, false,
                new Dictionary<string, object>(values), createdBy, now, now);
            _store[entry.Id] = entry;
            return Task.FromResult<ConfigurationSet?>(ToDto(entry));
        }
    }

    public Task<ConfigurationSet?> UpdateAsync(
        string tenantId, Guid id, string name, IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        lock (_store)
        {
            if (!_store.TryGetValue(id, out var e) || e.Tenant != tenantId)
            {
                return Task.FromResult<ConfigurationSet?>(null);
            }

            if (_store.Values.Any(x => x.Tenant == tenantId && x.Name == name && x.Id != id))
            {
                throw new ApiException(409, "Configuration Set 名稱已存在：" + name);
            }

            var updated = e with { Name = name, Values = new Dictionary<string, object>(values), UpdatedAt = Now() };
            _store[id] = updated;
            return Task.FromResult<ConfigurationSet?>(ToDto(updated));
        }
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            if (_store.TryGetValue(id, out var e) && e.Tenant == tenantId)
            {
                _store.Remove(id);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<ConfigurationSet?> ActivateAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            if (!_store.TryGetValue(id, out var target) || target.Tenant != tenantId)
            {
                return Task.FromResult<ConfigurationSet?>(null);
            }

            foreach (var e in _store.Values.Where(x => x.Tenant == tenantId && x.IsActive && x.Id != id).ToList())
            {
                _store[e.Id] = e with { IsActive = false, UpdatedAt = Now() };
            }

            var activated = target with { IsActive = true, UpdatedAt = Now() };
            _store[id] = activated;
            return Task.FromResult<ConfigurationSet?>(ToDto(activated));
        }
    }
}

/// <summary>
/// Skill 驗證器 fake(取代真的打 workflow :8001 的 POST /skills/validate)。
/// 記錄每一次呼叫供斷言;definition 含 <see cref="InvalidMarker"/> → valid=false 與兩個引擎錯誤碼,
/// 其餘一律通過並比照引擎回報 skill 中繼資料(以最陽春的逐行掃描取代真 YAML parser — 這是 fake 的工作)。
/// 以「內容觸發」而非可變旗標:fake 為 class fixture 共用,旗標會造成測試互相汙染。
/// </summary>
public sealed class FakeSkillValidator : ISkillValidator
{
    public const string InvalidMarker = "__invalid__";

    /// <summary>引擎不可達(WorkflowSkillValidator 對傳輸失敗/非 200 一律拋 502)。</summary>
    public const string EngineDownMarker = "__engine_down__";

    public sealed record Call(string Definition, string TenantId, string? UserId, string? Role);

    public List<Call> Calls { get; } = new();

    public Task<SkillValidationResult> ValidateAsync(
        string definition, string tenantId, string? userId, string? role, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(new Call(definition, tenantId, userId, role));
        }

        if (definition.Contains(EngineDownMarker, StringComparison.Ordinal))
        {
            throw new ApiException(502, "Skill 驗證服務呼叫失敗：連線被拒");
        }

        if (definition.Contains(InvalidMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(new SkillValidationResult(
                false,
                new[]
                {
                    new SkillValidationError("unbounded_loop", "loop 缺少 max_iterations", 7),
                    new SkillValidationError("unknown_node", "節點不存在：no_such_node", null),
                },
                null));
        }

        var meta = new SkillMetadata(
            Field(definition, "name") ?? "unnamed",
            Field(definition, "description") ?? string.Empty,
            Field(definition, "required_role") ?? "USER");
        return Task.FromResult(new SkillValidationResult(true, Array.Empty<SkillValidationError>(), meta));
    }

    /// <summary>取 YAML 頂層 `key: value` 的值(fake 專用的粗略掃描,不處理引號/巢狀)。</summary>
    private static string? Field(string definition, string key)
        => definition.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(line => line[(key.Length + 1)..].Trim())
            .FirstOrDefault(v => v.Length > 0);
}
