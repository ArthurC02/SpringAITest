using Dapper;
using Npgsql;
using Backend.Api.Skills;

namespace Backend.Api.Data;

/// <summary>
/// 啟動時冪等建立資料庫結構與種子資料。rag_documents / rag_chunks 兩表沿用 workflow
/// 既有結構(舊資料直接可用)。DB 不可達時 fail fast:記下清楚錯誤並讓啟動中止
/// (compose 由 depends_on 保證 appdb 先起;本機開發需先啟動 appdb)。
/// </summary>
public static class DbBootstrap
{
    private const string DefaultPassword = "password123";

    private const string RowSavepoint = "skill_migration_row";

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
        -- 跨租戶/使用者隔離:新增 tenant_id/user_id 欄位;舊資料以空字串補(冪等)。
        ALTER TABLE conversations ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '';
        ALTER TABLE conversations ADD COLUMN IF NOT EXISTS user_id text NOT NULL DEFAULT '';
        CREATE INDEX IF NOT EXISTS conversations_tenant_user_idx ON conversations (tenant_id, user_id);
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
        -- ANN 索引:SearchAsync 以 cosine distance(<=>)排序,配 vector_cosine_ops 的 HNSW。
        -- 需 pgvector >= 0.5(hnsw);extension 過舊時此 DDL 會明確報錯而中止啟動(fail fast)。
        CREATE INDEX IF NOT EXISTS rag_chunks_embedding_hnsw_idx
          ON rag_chunks USING hnsw (embedding vector_cosine_ops);
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
        -- Agent Skill package 與 revision snapshot。kind 不再由 package 是否為 NULL 推導：
        -- flow import 也保存原 zip，才能保留任意額外 entries。
        ALTER TABLE skill ADD COLUMN IF NOT EXISTS package bytea;
        ALTER TABLE skill_revision ADD COLUMN IF NOT EXISTS package_sha256 text;
        ALTER TABLE skill_revision ADD COLUMN IF NOT EXISTS package bytea;
        ALTER TABLE skill ADD COLUMN IF NOT EXISTS kind text;
        ALTER TABLE skill_revision ADD COLUMN IF NOT EXISTS kind text;
        -- 只對新增欄位為 NULL 的舊資料做一次性分類；之後 flow package 非 NULL 也不會被誤判 agentic。
        UPDATE skill
        SET kind = CASE
          WHEN definition ~ '(?m)^[ \t]*kind:[ \t]*agentic[ \t]*$' THEN 'agentic'
          ELSE 'flow'
        END
        WHERE kind IS NULL;
        UPDATE skill_revision
        SET kind = CASE WHEN package_sha256 IS NOT NULL THEN 'agentic' ELSE 'flow' END
        WHERE kind IS NULL;
        ALTER TABLE skill ALTER COLUMN kind SET DEFAULT 'flow';
        ALTER TABLE skill ALTER COLUMN kind SET NOT NULL;
        ALTER TABLE skill_revision ALTER COLUMN kind SET DEFAULT 'flow';
        ALTER TABLE skill_revision ALTER COLUMN kind SET NOT NULL;
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
            await MigrateSkillPackagesAndNamesAsync(conn, logger, ct);
            await SeedAsync(conn, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "無法連線或初始化資料庫,backend 啟動中止。請確認 appdb 已啟動且 DB_CONNECTION_STRING 正確。");
            throw;
        }
    }

    /// <summary>
    /// 05 §5 就地遷移：底線名稱改 kebab、所有 revision definition/hash 同步，current revision
    /// 與 skill current row 保持一致。legacy agentic package 的 SKILL.md 同時升級為標準
    /// metadata/allowed-tools frontmatter；其他 zip entry bytes 保留。
    /// </summary>
    private static async Task MigrateSkillPackagesAndNamesAsync(
        NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<SkillMigrationRow>(new CommandDefinition(
            "SELECT id AS Id, tenant_id AS TenantId, name AS Name, definition AS Definition,"
            + " kind AS Kind, package AS Package, current_revision AS CurrentRevision"
            + " FROM skill ORDER BY tenant_id, name FOR UPDATE",
            transaction: tx, cancellationToken: ct))).AsList();

        foreach (var row in rows)
        {
            // 遷移是一次性 legacy 升級,不是 request-time validator:某一列的資料形狀壞掉
            // (例如 package 內找不到可解析的 SKILL.md)不得讓整個服務起不來。savepoint 讓
            // 失敗的列整列回捲後跳過,其餘列照常升級。名稱衝突/無法標準化仍維持 fail fast。
            await tx.SaveAsync(RowSavepoint, ct);
            try
            {
                await MigrateSkillRowAsync(conn, tx, row, ct);
                await tx.ReleaseAsync(RowSavepoint, ct);
            }
            catch (Exception ex)
                when (ex is not (InvalidOperationException or OperationCanceledException))
            {
                // ROLLBACK TO 不銷毀 savepoint:不 RELEASE 的話每跳過一列就多疊一層子交易,
                // 巢狀深度隨壞列數無界成長並累積後端資源。(RELEASE 不會回收已配發的 subxid —
                // 那些要到頂層交易 commit 才不再列入 PGPROC->subxids。)rollback 後 savepoint 仍在 → 必成功。
                await tx.RollbackAsync(RowSavepoint, ct);
                await tx.ReleaseAsync(RowSavepoint, ct);
                logger.LogWarning(ex,
                    "略過無法遷移的 Skill：tenant={TenantId}, name={Name}", row.TenantId, row.Name);
            }
        }

        await tx.CommitAsync(ct);
    }

    private static async Task MigrateSkillRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SkillMigrationRow row, CancellationToken ct)
    {
        var targetName = row.Name.Replace('_', '-');
        if (!SkillNameRules.IsStandard(targetName))
        {
            throw new InvalidOperationException(
                $"無法把既有 Skill 名稱遷移為標準格式：tenant={row.TenantId}, name={row.Name}, target={targetName}");
        }

        if (!string.Equals(targetName, row.Name, StringComparison.Ordinal))
        {
            var collision = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM skill"
                + " WHERE tenant_id = @TenantId AND name = @targetName AND id <> @Id)",
                new { row.TenantId, targetName, row.Id }, tx, cancellationToken: ct));
            if (collision)
            {
                throw new InvalidOperationException(
                    $"Skill 名稱遷移發生衝突：tenant={row.TenantId}, old={row.Name}, target={targetName}");
            }
        }

        var nameChanged = !string.Equals(targetName, row.Name, StringComparison.Ordinal);
        var packageNeedsRewrite = row.Package is not null
            && SkillPackageMigration.PackageNeedsRewrite(row.Package, targetName, row.Kind);
        var definitionNeedsRewrite =
            !SkillPackageMigration.DefinitionNameMatches(row.Definition, targetName)
            || (string.Equals(row.Kind, "agentic", StringComparison.Ordinal)
                && !SkillPackageMigration.IsStandardAgenticCanonical(row.Definition, targetName));
        var definition = row.Definition;
        if (nameChanged || definitionNeedsRewrite || packageNeedsRewrite)
        {
            definition = string.Equals(row.Kind, "agentic", StringComparison.Ordinal)
                ? SkillPackageMigration.RewriteAgenticCanonical(row.Definition, targetName)
                : SkillPackageMigration.RewriteDefinitionName(row.Definition, targetName);
        }

        byte[]? package = row.Package;
        string? packageSha = null;
        if (package is not null)
        {
            if (packageNeedsRewrite)
            {
                var migrated = SkillPackageMigration.Rewrite(
                    package, targetName, row.Kind, row.Definition);
                package = migrated.Bytes;
                if (migrated.CanonicalDefinition is not null)
                {
                    definition = migrated.CanonicalDefinition;
                }
            }

            packageSha = SkillHash.Sha256(package);
        }

        var revisions = (await conn.QueryAsync<SkillRevisionMigrationRow>(new CommandDefinition(
            "SELECT revision AS Revision, definition AS Definition, kind AS Kind,"
            + " package AS Package, package_sha256 AS PackageSha256"
            + " FROM skill_revision WHERE skill_id = @Id ORDER BY revision",
            new { row.Id }, tx, cancellationToken: ct))).AsList();

        foreach (var revision in revisions)
        {
            var revisionDefinition = revision.Definition;
            var revisionNeedsDefinitionRewrite =
                nameChanged
                || !SkillPackageMigration.DefinitionNameMatches(revision.Definition, targetName)
                || (string.Equals(revision.Kind, "agentic", StringComparison.Ordinal)
                    && !SkillPackageMigration.IsStandardAgenticCanonical(
                        revision.Definition, targetName));
            if (revisionNeedsDefinitionRewrite)
            {
                revisionDefinition =
                    string.Equals(revision.Kind, "agentic", StringComparison.Ordinal)
                        ? SkillPackageMigration.RewriteAgenticCanonical(
                            revision.Definition, targetName)
                        : SkillPackageMigration.RewriteDefinitionName(
                            revision.Definition, targetName);
            }
            var revisionPackage = revision.Package;
            var revisionPackageSha = revision.PackageSha256;
            if (revisionPackage is not null
                && SkillPackageMigration.PackageNeedsRewrite(
                    revisionPackage, targetName, revision.Kind))
            {
                var migrated = SkillPackageMigration.Rewrite(
                    revisionPackage, targetName, revision.Kind, revision.Definition);
                revisionPackage = migrated.Bytes;
                revisionPackageSha = SkillHash.Sha256(revisionPackage);
                if (migrated.CanonicalDefinition is not null)
                {
                    revisionDefinition = migrated.CanonicalDefinition;
                }
            }

            // 舊 schema 只在 skill 保存 current package：把它補進 current revision snapshot。
            if (revision.Revision == row.CurrentRevision && revisionPackage is null && package is not null)
            {
                revisionPackage = package;
                revisionPackageSha = packageSha;
                revisionDefinition = definition;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE skill_revision SET definition = @revisionDefinition,"
                + " definition_sha256 = @definitionSha, kind = @Kind,"
                + " package = @revisionPackage, package_sha256 = @revisionPackageSha"
                + " WHERE skill_id = @Id AND revision = @Revision",
                new
                {
                    row.Id,
                    revision.Revision,
                    revisionDefinition,
                    definitionSha = SkillHash.Sha256(revisionDefinition),
                    revision.Kind,
                    revisionPackage,
                    revisionPackageSha,
                }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE skill SET name = @targetName, definition = @definition,"
            + " package = @package, kind = @Kind"
            + " WHERE id = @Id",
            new { row.Id, targetName, definition, package, row.Kind },
            tx, cancellationToken: ct));
    }

    private sealed record SkillMigrationRow(
        Guid Id,
        string TenantId,
        string Name,
        string Definition,
        string Kind,
        byte[]? Package,
        int CurrentRevision);

    private sealed record SkillRevisionMigrationRow(
        int Revision,
        string Definition,
        string Kind,
        byte[]? Package,
        string? PackageSha256);

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
