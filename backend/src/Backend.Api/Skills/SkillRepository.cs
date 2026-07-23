using Dapper;
using Npgsql;

namespace Backend.Api.Skills;

/// <summary>
/// 以 Dapper + Npgsql 實作 skill / skill_revision 兩表存取。snake_case 欄位一律以 SQL AS alias
/// 對映 PascalCase(同 ConfigRepository)。寫入時傳入的時間戳與 revision 一律忽略,由 DB 決定。
/// 建立/更新以 data-modifying CTE 一次完成「寫 skill + 寫 skill_revision」:PostgreSQL 保證 WITH 內的
/// 寫入語句必定執行、且與主查詢同一交易 — 不會出現寫了 skill 卻漏了稽核列的半套狀態。
/// skill-authoring 遺留的 flow/logic/script 三欄不再寫入(NOT NULL DEFAULT '' → 省略即空字串)。
/// </summary>
public sealed class SkillRepository : ISkillRepository
{
    // 完整欄位投影(GET / CTE RETURNING 共用)。package 一併投影(export / 內部 package 端點 / agentic 判定用);
    // Skill.Package 標了 JsonIgnore → 不會外洩到任何公開 JSON。
    private const string Cols =
        "name AS Name, description AS Description, definition AS Definition,"
        + " required_role AS RequiredRole, enabled AS Enabled,"
        + " current_revision AS CurrentRevision, created_at AS CreatedAt, updated_at AS UpdatedAt,"
        + " kind AS Kind, package AS Package";

    // skill_revision 稽核列插入片段(Create/Update/Import 共用):{0}=主 CTE 名、{1}=寫入者參數名。
    // 每版都 snapshot definition/kind/package；package bytes 不公開，但 restore 必須能還原完整作者輸入。
    // 主 CTE 語意不同(INSERT ON CONFLICT vs UPDATE)刻意不抽整段,只共用這段稽核插入。
    private const string RevisionCte =
        "), rev AS ("
        + " INSERT INTO skill_revision"
        + " (skill_id, revision, definition, definition_sha256, created_by, kind, package, package_sha256)"
        + " SELECT id, current_revision, definition, @sha, @{1}, kind, package, @packageSha FROM {0}"
        + ")";

    private readonly NpgsqlDataSource _dataSource;

    public SkillRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SkillInfo>(new CommandDefinition(
            "SELECT name AS Name, description AS Description, required_role AS RequiredRole,"
            + " enabled AS Enabled, current_revision AS CurrentRevision,"
            + " created_at AS CreatedAt, updated_at AS UpdatedAt, kind AS Kind"
            + " FROM skill WHERE tenant_id = @tenantId AND enabled ORDER BY name",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Skill>(new CommandDefinition(
            $"SELECT {Cols} FROM skill WHERE tenant_id = @tenantId AND name = @name AND enabled",
            new { tenantId, name }, cancellationToken: ct));
    }

    public async Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // CTE 一律 RETURNING *(真欄名):PG 會把未加引號的別名折成小寫,
        // 用 `AS RequiredRole` 產出的欄位其實叫 requiredrole,外層再以 required_name 引用就會 42703。
        // 別名只留給最外層那個 SELECT(Dapper 靠它對映)。
        //
        // 軟刪的名字要能重用:ON CONFLICT DO UPDATE ... WHERE NOT skill.enabled →
        //   - 名字還活著 → WHERE 不成立 → 0 列 → 回 null → controller 409(且不多寫 revision);
        //   - 名字已軟刪 → 復活,current_revision 接著加(稽核鏈不斷號),enabled 回 true。
        // revision 取 RETURNING 回來的真值(不是寫死的 1)—— 復活時是 N+1,新建時就是 1。
        return await ExecuteWriteCteAsync(conn, "ins",
            "INSERT INTO skill (tenant_id, name, description, definition, required_role, enabled,"
            + "  created_by, current_revision)"
            + " VALUES (@tenantId, @Name, @Description, @Definition, @RequiredRole, true, @createdBy, 1)"
            + " ON CONFLICT (tenant_id, name) DO UPDATE SET"
            + "  description = EXCLUDED.description, definition = EXCLUDED.definition,"
            + "  required_role = EXCLUDED.required_role, enabled = true,"
            + "  kind = 'flow', package = NULL,"
            + "  current_revision = skill.current_revision + 1, updated_at = now()"
            + " WHERE NOT skill.enabled",
            "createdBy",
            new
            {
                tenantId,
                skill.Name,
                skill.Description,
                skill.Definition,
                skill.RequiredRole,
                createdBy,
                sha = SkillHash.Sha256(skill.Definition),
                packageSha = (string?)null,
            }, ct);
    }

    public async Task<Skill?> UpdateAsync(
        string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // current_revision +1 後,把「新的 revision 值」與新定義一起落一筆稽核列。
        // AND enabled:已軟刪的 skill 不可經 PUT 復活(規格沒有復用/復活端點)。
        return await ExecuteWriteCteAsync(conn, "upd",
            "UPDATE skill SET description = @Description, definition = @Definition,"
            + "  required_role = @RequiredRole, kind = 'flow', package = NULL,"
            + "  current_revision = current_revision + 1, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND name = @name AND enabled",
            "updatedBy",
            new
            {
                tenantId,
                name,
                skill.Description,
                skill.Definition,
                skill.RequiredRole,
                updatedBy,
                sha = SkillHash.Sha256(skill.Definition),
                packageSha = (string?)null,
            }, ct);
    }

    /// <summary>
    /// Create/Update/Import 共用的寫入骨架:把主 CTE(INSERT ... ON CONFLICT 或 UPDATE,不含 RETURNING)
    /// 接上 RETURNING * 與 skill_revision 稽核 CTE、最後投影出 Cols。主 CTE 語意由呼叫端決定,
    /// 這裡只收斂「組 SQL + 執行」這段逐字相同的樣板。
    /// </summary>
    private static Task<Skill?> ExecuteWriteCteAsync(
        NpgsqlConnection conn, string cteName, string mainCteBody, string writerParam, object parameters,
        CancellationToken ct)
    {
        var sql = $"WITH {cteName} AS (" + mainCteBody + " RETURNING *"
            + string.Format(RevisionCte, cteName, writerParam)
            + $" SELECT {Cols} FROM {cteName}";
        return conn.QuerySingleOrDefaultAsync<Skill>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    /// <summary>
    /// Agent Skill 匯入:upsert(建立 / 更新 / 復活)一律成功並 bump revision。與 Create 的差異是
    /// ON CONFLICT DO UPDATE **無 WHERE 條件** — 對仍啟用的 skill 也直接覆寫(import 是作者的權威動作)。
    /// flow 與 agentic 匯入都保存原始 package；definition-only flow 才使用 null。
    /// definition + package + 兩個 hash 於同一交易的同一 CTE 內落地(稽核完整性)。
    /// </summary>
    public async Task<Skill?> ImportAsync(
        string tenantId, Skill skill, byte[]? package, string? packageSha256, string createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await ExecuteWriteCteAsync(conn, "imp",
            "INSERT INTO skill (tenant_id, name, description, definition, required_role, enabled,"
            + "  created_by, current_revision, kind, package)"
            + " VALUES (@tenantId, @Name, @Description, @Definition, @RequiredRole, true, @createdBy, 1, @Kind, @package)"
            + " ON CONFLICT (tenant_id, name) DO UPDATE SET"
            + "  description = EXCLUDED.description, definition = EXCLUDED.definition,"
            + "  required_role = EXCLUDED.required_role, enabled = true,"
            + "  kind = EXCLUDED.kind, package = EXCLUDED.package,"
            + "  current_revision = skill.current_revision + 1, updated_at = now()",
            "createdBy",
            new
            {
                tenantId,
                skill.Name,
                skill.Description,
                skill.Definition,
                skill.RequiredRole,
                skill.Kind,
                createdBy,
                package,
                sha = SkillHash.Sha256(skill.Definition),
                packageSha = packageSha256,
            }, ct);
    }

    /// <summary>軟刪:enabled=false。skill_revision 一列都不動(金融稽核:歷史永不消失)。</summary>
    public async Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE skill SET enabled = false, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND name = @name AND enabled",
            new { tenantId, name }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(
        string tenantId, string name, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // 刻意不帶 AND s.enabled:軟刪後歷史仍要查得到。租戶過濾仍在(跨租戶 → 空清單 → controller 回 404)。
        var rows = await conn.QueryAsync<SkillRevisionInfo>(new CommandDefinition(
            "SELECT r.revision AS Revision, r.definition AS Definition,"
            + " r.definition_sha256 AS DefinitionSha256, r.created_by AS CreatedBy, r.created_at AS CreatedAt,"
            + " r.kind AS Kind, (r.package IS NOT NULL) AS HasPackage,"
            + " r.package_sha256 AS PackageSha256"
            + " FROM skill_revision r JOIN skill s ON s.id = r.skill_id"
            + " WHERE s.tenant_id = @tenantId AND s.name = @name"
            + " ORDER BY r.revision DESC",
            new { tenantId, name }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<StoredSkillRevision?> GetRevisionAsync(
        string tenantId, string name, int revision, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<StoredSkillRevision>(new CommandDefinition(
            "SELECT r.revision AS Revision, r.definition AS Definition,"
            + " r.definition_sha256 AS DefinitionSha256, r.created_by AS CreatedBy,"
            + " r.created_at AS CreatedAt, r.kind AS Kind, r.package AS Package,"
            + " r.package_sha256 AS PackageSha256"
            + " FROM skill_revision r JOIN skill s ON s.id = r.skill_id"
            + " WHERE s.tenant_id = @tenantId AND s.name = @name AND r.revision = @revision",
            new { tenantId, name, revision }, cancellationToken: ct));
    }
}
