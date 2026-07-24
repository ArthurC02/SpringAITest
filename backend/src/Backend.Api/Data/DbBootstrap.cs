using Dapper;
using Npgsql;
using Backend.Api.Agents;
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
          capabilities text[] NOT NULL DEFAULT '{}',
          created_at timestamptz NOT NULL DEFAULT now());
        -- 既有 appdb 的 additive upgrade；空陣列代表沒有 capability，ADMIN 不自動升格。
        ALTER TABLE users ADD COLUMN IF NOT EXISTS capabilities text[] NOT NULL DEFAULT '{}';
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
        -- 簡單模式表單狀態(opaque JSON:{ templateId, form }),讓非技術使用者建完 skill 後可重回簡單模式。
        -- 只 skill 有此欄(不做版本化,skill_revision 不加);NULL = 無表單狀態(只能進階編輯)。
        ALTER TABLE skill ADD COLUMN IF NOT EXISTS simple_form jsonb;
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
        -- ==== Agent 平台重整 D1:Agent aggregate + 最小 Workflow(欄位依 03-design §3)====
        -- Agent 是獨立 aggregate(D1),不擴充 skill 假裝 Agent。draft_definition 是唯一可變作者副本
        -- (canonical JSON,snake_case 鍵),以 draft_version 做 optimistic concurrency(ETag);
        -- draft_validated_version 記「哪個 draft version 已通過驗證」(publish 要求兩者相等)。
        -- draft_definition_sha256 於 create/put 時算好落欄(對 canonical 文字,不受 jsonb roundtrip 影響),
        -- publish 時原封抄進 revision → definition hash 穩定可重現。tenant_id 用 text(= 租戶 code,與 skill 一致)。
        CREATE TABLE IF NOT EXISTS agent (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          tenant_id text NOT NULL,
          slug text NOT NULL,
          name text NOT NULL,
          description text NOT NULL DEFAULT '',
          enabled boolean NOT NULL DEFAULT true,
          draft_version bigint NOT NULL DEFAULT 1,
          draft_definition jsonb NOT NULL DEFAULT '{}',
          draft_definition_sha256 text NOT NULL DEFAULT '',
          draft_validated_version bigint,
          published_revision integer,
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          updated_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_agent_tenant_slug UNIQUE (tenant_id, slug));
        CREATE INDEX IF NOT EXISTS ix_agent_tenant_enabled ON agent (tenant_id, enabled);
        -- 不可變發布快照。status = published / superseded(較新發布使舊版 superseded);
        -- 每欄拆存(03 §3)。publish 由 draft_definition 抽欄落地,restore 直接 INSERT ... SELECT 舊列複製。
        CREATE TABLE IF NOT EXISTS agent_revision (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          agent_id uuid NOT NULL REFERENCES agent(id),
          revision integer NOT NULL,
          status text NOT NULL DEFAULT 'published',
          system_prompt text NOT NULL DEFAULT '',
          execution_roles jsonb NOT NULL DEFAULT '[]',
          capabilities jsonb NOT NULL DEFAULT '[]',
          output_contract jsonb NOT NULL DEFAULT '{}',
          audience jsonb NOT NULL DEFAULT '[]',
          business_rules jsonb NOT NULL DEFAULT '{}',
          allowed_tools jsonb NOT NULL DEFAULT '[]',
          knowledge_sources jsonb NOT NULL DEFAULT '[]',
          runtime_limits jsonb NOT NULL DEFAULT '{}',
          runtime_workflow_id uuid,
          runtime_workflow_revision integer,
          definition_sha256 text NOT NULL DEFAULT '',
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_agent_revision UNIQUE (agent_id, revision));
        CREATE INDEX IF NOT EXISTS ix_agent_revision_agent ON agent_revision (agent_id);
        -- 發布時每個 Skill binding 固定到確切 revision(skill 的 current published revision)。
        -- skill_id 以 (tenant_id, name) 於寫入時 join 解析出真 uuid(FK 穩定);skill 更新不改此 pin。
        CREATE TABLE IF NOT EXISTS agent_revision_skill (
          agent_id uuid NOT NULL,
          agent_revision integer NOT NULL,
          skill_id uuid NOT NULL REFERENCES skill(id),
          skill_revision integer NOT NULL,
          position integer NOT NULL DEFAULT 0,
          enabled boolean NOT NULL DEFAULT true,
          PRIMARY KEY (agent_id, agent_revision, skill_id));
        -- 最小 Workflow / Workflow_revision(D1 只建表 + 種子一筆系統 Default Agent-Runtime Workflow;
        -- 內容 D3 才會被消費,此期只求形狀正確)。
        CREATE TABLE IF NOT EXISTS workflow (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          tenant_id text NOT NULL,
          name text NOT NULL,
          kind text NOT NULL,
          enabled boolean NOT NULL DEFAULT true,
          draft_version bigint NOT NULL DEFAULT 1,
          draft_definition jsonb NOT NULL DEFAULT '{}',
          draft_ui_metadata jsonb NOT NULL DEFAULT '{}',
          published_revision integer,
          created_at timestamptz NOT NULL DEFAULT now(),
          updated_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS workflow_revision (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          workflow_id uuid NOT NULL REFERENCES workflow(id),
          revision integer NOT NULL,
          schema_version integer NOT NULL DEFAULT 1,
          definition jsonb NOT NULL DEFAULT '{}',
          ui_metadata jsonb NOT NULL DEFAULT '{}',
          definition_sha256 text NOT NULL DEFAULT '',
          ui_metadata_sha256 text NOT NULL DEFAULT '',
          compiler_contract_version text NOT NULL DEFAULT '1',
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_workflow_revision UNIQUE (workflow_id, revision));
        -- Published Agent references 必須在 DB 層也能對回 immutable revisions；應用層的
        -- tenant/enabled/kind validation 仍不可省略（FK 不表達那些政策）。
        DO $constraints$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint WHERE conname = 'fk_agent_revision_runtime_workflow') THEN
            ALTER TABLE agent_revision
              ADD CONSTRAINT fk_agent_revision_runtime_workflow
              FOREIGN KEY (runtime_workflow_id, runtime_workflow_revision)
              REFERENCES workflow_revision(workflow_id, revision) NOT VALID;
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint WHERE conname = 'fk_agent_revision_skill_agent_revision') THEN
            ALTER TABLE agent_revision_skill
              ADD CONSTRAINT fk_agent_revision_skill_agent_revision
              FOREIGN KEY (agent_id, agent_revision)
              REFERENCES agent_revision(agent_id, revision) NOT VALID;
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint WHERE conname = 'fk_agent_revision_skill_skill_revision') THEN
            ALTER TABLE agent_revision_skill
              ADD CONSTRAINT fk_agent_revision_skill_skill_revision
              FOREIGN KEY (skill_id, skill_revision)
              REFERENCES skill_revision(skill_id, revision) NOT VALID;
          END IF;
        END
        $constraints$;
        -- NOT VALID 讓 additive upgrade 可以先建立 constraint；同一個 bootstrap 隨即做完整掃描。
        -- 若 legacy 資料違反 immutable-reference invariant，啟動必須 fail fast，不能長期留下
        -- convalidated=false、卻宣稱已有 DB 級保護的半套 migration。
        ALTER TABLE agent_revision VALIDATE CONSTRAINT fk_agent_revision_runtime_workflow;
        ALTER TABLE agent_revision_skill VALIDATE CONSTRAINT fk_agent_revision_skill_agent_revision;
        ALTER TABLE agent_revision_skill VALIDATE CONSTRAINT fk_agent_revision_skill_skill_revision;
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
        foreach (var seed in new[]
        {
            new
            {
                Username = "admin-a",
                Role = "ADMIN",
                TenantCode = "demo-a",
                Capabilities = new[] { "workflow.manage" },
            },
            new
            {
                Username = "user-a",
                Role = "USER",
                TenantCode = "demo-a",
                Capabilities = Array.Empty<string>(),
            },
            new
            {
                Username = "user-b",
                Role = "USER",
                TenantCode = "demo-b",
                Capabilities = Array.Empty<string>(),
            },
        })
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO users (username, password_hash, role, tenant_id, capabilities)"
                + " SELECT @Username, @Hash, @Role, t.id, @Capabilities"
                + " FROM tenants t WHERE t.code = @TenantCode"
                + " ON CONFLICT (username) DO NOTHING",
                new
                {
                    seed.Username,
                    Hash = BCrypt.Net.BCrypt.HashPassword(DefaultPassword),
                    seed.Role,
                    seed.TenantCode,
                    seed.Capabilities,
                }, cancellationToken: ct));
        }

        await SeedDefaultWorkflowAsync(conn, ct);
    }

    /// <summary>
    /// 種入 system-owned Default Agent-Runtime Workflow current revision(published、不可編輯)。
    /// rev1 是歷史快照，絕不原地 repair；修正版以 rev2 寫入，新 Agent 預設 pin rev2。
    /// 冪等重跑只調整 mutable workflow pointer/draft，revision row 只 INSERT、衝突時驗證內容相同，
    /// 不做 UPDATE，避免既有 Agent 的 immutable workflow pin 在 restart 後漂移。
    /// </summary>
    private static async Task SeedDefaultWorkflowAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var fixtureErrors = AgentDefaults.ValidateRuntimeWorkflowFixture();
        if (fixtureErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Default Agent-Runtime Workflow fixture 不合法：" + string.Join("；", fixtureErrors));
        }

        var args = new
        {
            id = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            tenant = AgentDefaults.SystemTenant,
            name = AgentDefaults.RuntimeWorkflowName,
            kind = AgentDefaults.RuntimeWorkflowKind,
            revision = AgentDefaults.RuntimeWorkflowRevision,
            def = AgentDefaults.RuntimeWorkflowDefinition,
            sha = SkillHash.Sha256(AgentDefaults.RuntimeWorkflowDefinition),
            uiSha = SkillHash.Sha256("{}"),
        };

        await using var tx = await conn.BeginTransactionAsync(ct);
        var existingTenant = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT tenant_id FROM workflow WHERE id = @id",
            new { id = args.id }, tx, cancellationToken: ct));
        if (existingTenant is not null && existingTenant != AgentDefaults.SystemTenant)
        {
            throw new InvalidOperationException(
                $"Default Agent-Runtime Workflow id 與非 system workflow 衝突：{args.id}");
        }

        // workflow row 是 current pointer/authoring draft，可安全 reconcile；workflow_revision 則不可變。
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workflow (id, tenant_id, name, kind, enabled, draft_version, draft_definition,"
            + "  draft_ui_metadata, published_revision)"
            + " VALUES (@id, @tenant, @name, @kind, true, 1, @def::jsonb, '{}'::jsonb, @revision)"
            + " ON CONFLICT (id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, name = EXCLUDED.name,"
            + "  kind = EXCLUDED.kind, enabled = true, draft_version = 1,"
            + "  draft_definition = EXCLUDED.draft_definition, draft_ui_metadata = '{}'::jsonb,"
            + "  published_revision = EXCLUDED.published_revision, updated_at = now()",
            args, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workflow_revision (workflow_id, revision, schema_version, definition, ui_metadata,"
            + "  definition_sha256, ui_metadata_sha256, compiler_contract_version, created_by)"
            + " VALUES (@id, @revision, 1, @def::jsonb, '{}'::jsonb, @sha, @uiSha, '1', 'system')"
            + " ON CONFLICT (workflow_id, revision) DO NOTHING",
            args, tx, cancellationToken: ct));

        var currentRevisionMatches = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS("
            + " SELECT 1 FROM workflow_revision"
            + " WHERE workflow_id = @id AND revision = @revision"
            + " AND definition = @def::jsonb AND definition_sha256 = @sha"
            + " AND ui_metadata = '{}'::jsonb AND ui_metadata_sha256 = @uiSha"
            + " AND compiler_contract_version = '1')",
            args, tx, cancellationToken: ct));
        if (!currentRevisionMatches)
        {
            throw new InvalidOperationException(
                $"Default Agent-Runtime Workflow rev{AgentDefaults.RuntimeWorkflowRevision} 已存在但內容不同；"
                + "published revision 不可原地覆寫，請建立下一個 revision");
        }

        await tx.CommitAsync(ct);
    }
}
