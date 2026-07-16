using Dapper;
using Npgsql;

namespace Backend.Api.Data;

/// <summary>
/// 啟動時冪等建立資料庫結構與種子資料。rag_documents / rag_chunks 兩表沿用 workflow
/// 既有結構(舊資料直接可用)。DB 不可達時 fail fast:記下清楚錯誤並讓啟動中止
/// (compose 由 depends_on 保證 appdb 先起;本機開發需先啟動 appdb)。
/// </summary>
public static class DbBootstrap
{
    private const string DefaultPassword = "password123";

    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE TABLE IF NOT EXISTS tenants (
          id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          code text NOT NULL UNIQUE, name text NOT NULL,
          invite_code text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS users (
          id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          username text NOT NULL UNIQUE, password_hash text NOT NULL,
          role text NOT NULL, tenant_id bigint NOT NULL REFERENCES tenants(id),
          created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS conversations (
          id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          prompt text NOT NULL, reply text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS rag_documents (
          id uuid PRIMARY KEY, tenant_id text NOT NULL, title text NOT NULL,
          chunk_count int NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
        -- 非同步處理:新增 status 欄位;舊資料自動視為 ready(冪等)。
        ALTER TABLE rag_documents ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'ready';
        CREATE TABLE IF NOT EXISTS rag_chunks (
          id uuid PRIMARY KEY,
          document_id uuid REFERENCES rag_documents (id) ON DELETE CASCADE,
          tenant_id text NOT NULL, content text NOT NULL,
          embedding vector(1536) NOT NULL);
        CREATE INDEX IF NOT EXISTS rag_documents_tenant_idx ON rag_documents (tenant_id);
        CREATE INDEX IF NOT EXISTS rag_chunks_tenant_idx ON rag_chunks (tenant_id);
        CREATE TABLE IF NOT EXISTS app_config (
          key text PRIMARY KEY, value text NOT NULL,
          updated_at timestamptz NOT NULL DEFAULT now());
        -- 使用者撰寫的 Skill。definition = YAML 原文(權威格式,引擎執行的事實來源);
        -- name/description/required_role 都寫在 YAML 裡,存檔時由引擎 validate 回報的中繼資料落欄位
        -- (backend 不解析 YAML — 兩個 parser 就是兩份事實)。
        -- tenant_id 用 text(= 租戶 code,與 rag_documents 一致;identity header X-Tenant-Id 傳的就是 code)。
        -- 設計稿第 3 節寫 tenant_id uuid、definition_json jsonb:前者與既有 schema 不符(見 D-N),
        -- 後者無任何讀取端(正規化 JSON 由引擎持有)→ 兩者皆不採用。
        CREATE TABLE IF NOT EXISTS skill (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          tenant_id text NOT NULL,
          name text NOT NULL,
          description text NOT NULL,
          definition text NOT NULL DEFAULT '',
          required_role text NOT NULL DEFAULT 'USER',
          enabled boolean NOT NULL DEFAULT true,
          current_revision int NOT NULL DEFAULT 1,
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          updated_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_skill_tenant_name UNIQUE (tenant_id, name));
        CREATE INDEX IF NOT EXISTS ix_skill_tenant_enabled ON skill (tenant_id, enabled);
        -- 稽核與回溯:每次建立/更新產生一筆。軟刪不動此表 — revision 永不刪。
        CREATE TABLE IF NOT EXISTS skill_revision (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          skill_id uuid NOT NULL REFERENCES skill(id),
          revision int NOT NULL,
          definition text NOT NULL,
          definition_sha256 text NOT NULL,
          created_by text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_skill_revision UNIQUE (skill_id, revision));
        CREATE INDEX IF NOT EXISTS ix_skill_revision_skill ON skill_revision (skill_id);
        -- Configuration Set(設計 §7.1):一組可調的執行期覆寫鍵(values jsonb),per-tenant。
        -- tenant_id 用 text(= 租戶 code,與 skill/rag_documents 一致)。is_active 一租戶至多一筆為 true,
        -- 由部分唯一索引 uq_confset_active 於 DB 級兜底(不靠應用碼保唯一)。
        CREATE TABLE IF NOT EXISTS configuration_set (
          id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          tenant_id   text NOT NULL,
          name        text NOT NULL,
          is_active   boolean NOT NULL DEFAULT false,
          values      jsonb NOT NULL DEFAULT '{}',
          created_by  text NOT NULL DEFAULT '',
          created_at  timestamptz NOT NULL DEFAULT now(),
          updated_at  timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_confset_tenant_name UNIQUE (tenant_id, name));
        CREATE UNIQUE INDEX IF NOT EXISTS uq_confset_active ON configuration_set (tenant_id) WHERE is_active;
        """;

    public static async Task RunAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(Ddl, cancellationToken: ct));
            await SeedAsync(conn, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "無法連線或初始化資料庫,backend 啟動中止。請確認 appdb 已啟動且 DB_CONNECTION_STRING 正確。");
            throw;
        }
    }

    private static async Task SeedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // 租戶冪等寫入(以 code 唯一)。
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants (code, name, invite_code) VALUES (@Code, @Name, @Invite)"
            + " ON CONFLICT (code) DO NOTHING",
            new[]
            {
                new { Code = "demo-a", Name = "示範租戶 A", Invite = "demo-a-invite" },
                new { Code = "demo-b", Name = "示範租戶 B", Invite = "demo-b-invite" },
            }, cancellationToken: ct));

        // 使用者冪等寫入(以 username 唯一);tenant_id 由 code 反查,密碼 BCrypt。
        foreach (var (username, role, tenantCode) in new[]
        {
            ("admin-a", "ADMIN", "demo-a"),
            ("user-a", "USER", "demo-a"),
            ("user-b", "USER", "demo-b"),
        })
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO users (username, password_hash, role, tenant_id)"
                + " SELECT @username, @hash, @role, t.id FROM tenants t WHERE t.code = @tenantCode"
                + " ON CONFLICT (username) DO NOTHING",
                new
                {
                    username,
                    hash = BCrypt.Net.BCrypt.HashPassword(DefaultPassword),
                    role,
                    tenantCode,
                }, cancellationToken: ct));
        }
    }
}
