using Dapper;
using Npgsql;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Skills;
using Backend.Api.Workflows;

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
    private const long BootstrapAdvisoryLockId = 823746291;

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
        CREATE UNIQUE INDEX IF NOT EXISTS ux_users_id_tenant ON users (id, tenant_id);
        CREATE TABLE IF NOT EXISTS user_group_membership (
          tenant_id bigint NOT NULL REFERENCES tenants(id),
          user_id bigint NOT NULL,
          group_id text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY (tenant_id, user_id, group_id),
          CONSTRAINT fk_user_group_membership_user_tenant
            FOREIGN KEY (user_id, tenant_id) REFERENCES users(id, tenant_id) ON DELETE CASCADE,
          CONSTRAINT ck_user_group_membership_group_id CHECK (
            char_length(group_id) BETWEEN 1 AND 128
            AND group_id ~ '^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$'));
        CREATE INDEX IF NOT EXISTS ix_user_group_membership_user
          ON user_group_membership (user_id, tenant_id);
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
          draft_definition_canonical bytea,
          draft_definition_sha256 text NOT NULL DEFAULT '',
          draft_validated_version bigint,
          published_revision integer,
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          updated_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_agent_tenant_slug UNIQUE (tenant_id, slug));
        CREATE INDEX IF NOT EXISTS ix_agent_tenant_enabled ON agent (tenant_id, enabled);
        ALTER TABLE agent
          ADD COLUMN IF NOT EXISTS draft_definition_canonical bytea;
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
          canonical_definition bytea,
          created_by text NOT NULL DEFAULT '',
          created_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT uq_agent_revision UNIQUE (agent_id, revision));
        CREATE INDEX IF NOT EXISTS ix_agent_revision_agent ON agent_revision (agent_id);
        ALTER TABLE agent_revision
          ADD COLUMN IF NOT EXISTS canonical_definition bytea;
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
         -- D4: semantic Graph IR and UI metadata are separate authoritative canonical byte
         -- streams. jsonb remains a query projection only; immutable revisions never depend on
         -- PostgreSQL's jsonb reserialization for their digest.
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS draft_definition_canonical bytea;
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS draft_ui_metadata_canonical bytea;
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS draft_definition_sha256 text NOT NULL DEFAULT '';
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS draft_ui_metadata_sha256 text NOT NULL DEFAULT '';
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS draft_validated_version bigint;
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS created_by text NOT NULL DEFAULT '';
         ALTER TABLE workflow ADD COLUMN IF NOT EXISTS system_owned boolean NOT NULL DEFAULT false;
         CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_tenant_name ON workflow (tenant_id, name);
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
         ALTER TABLE workflow_revision ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'published';
         ALTER TABLE workflow_revision ADD COLUMN IF NOT EXISTS definition_canonical bytea;
         ALTER TABLE workflow_revision ADD COLUMN IF NOT EXISTS ui_metadata_canonical bytea;
         -- D4 Root Orchestrator aggregate: definitions are authoritative canonical bytes; JSONB
         -- is retained only for inspection.  References are pinned into immutable revisions.
         CREATE TABLE IF NOT EXISTS orchestrator (
           id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id text NOT NULL,
           name text NOT NULL, description text NOT NULL DEFAULT '', enabled boolean NOT NULL DEFAULT true,
           draft_version bigint NOT NULL DEFAULT 1, draft_validated_version bigint,
           draft_definition jsonb NOT NULL DEFAULT '{}', draft_definition_canonical bytea,
           draft_definition_sha256 text NOT NULL DEFAULT '', published_revision integer,
           created_by text NOT NULL DEFAULT '', created_at timestamptz NOT NULL DEFAULT now(),
           updated_at timestamptz NOT NULL DEFAULT now(), CONSTRAINT uq_orchestrator_tenant_name UNIQUE(tenant_id,name));
         CREATE INDEX IF NOT EXISTS ix_orchestrator_tenant_enabled ON orchestrator(tenant_id,enabled);
         CREATE TABLE IF NOT EXISTS orchestrator_revision (
           id uuid PRIMARY KEY DEFAULT gen_random_uuid(), orchestrator_id uuid NOT NULL REFERENCES orchestrator(id),
           revision integer NOT NULL, status text NOT NULL DEFAULT 'published', definition jsonb NOT NULL,
           canonical_definition bytea NOT NULL, definition_sha256 text NOT NULL,
           workflow_id uuid NOT NULL, workflow_revision integer NOT NULL,
           verifier_agent_id uuid NOT NULL, verifier_agent_revision integer NOT NULL,
           created_by text NOT NULL DEFAULT '', created_at timestamptz NOT NULL DEFAULT now(),
           CONSTRAINT uq_orchestrator_revision UNIQUE(orchestrator_id,revision));
         CREATE INDEX IF NOT EXISTS ix_orchestrator_revision_orchestrator ON orchestrator_revision(orchestrator_id);
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

        -- ==== Agent 平台重整 D3：published direct-Agent test runs ====
        -- appdb 保存 immutable snapshot、lineage、公開狀態投影與 append-only audit；LangGraph
        -- checkpoint 本體由 Workflow 的 checkpointer 擁有，這裡只保存 opaque ref/version。
        CREATE TABLE IF NOT EXISTS agent_run (
          id uuid PRIMARY KEY,
          tenant_id text NOT NULL,
          root_run_id uuid NOT NULL,
          parent_run_id uuid,
          task_id text,
          run_kind text NOT NULL,
          user_id text NOT NULL,
          caller_role text NOT NULL,
          conversation_id text,
          agent_id uuid NOT NULL,
          agent_revision integer NOT NULL,
          workflow_id uuid NOT NULL,
          workflow_revision integer NOT NULL,
          execution_snapshot jsonb NOT NULL,
          execution_snapshot_canonical bytea,
          snapshot_sha256 text NOT NULL,
          status text NOT NULL,
          state_version bigint NOT NULL DEFAULT 1,
          lease_generation bigint NOT NULL DEFAULT 0,
          checkpoint_ref text,
          checkpoint_generation bigint NOT NULL DEFAULT 0,
          checkpoint_version bigint NOT NULL DEFAULT 0,
          event_ack_cursor bigint NOT NULL DEFAULT 0,
          pending_input jsonb,
          result jsonb,
          error_code text,
          error_message text,
          cancel_requested_at timestamptz,
          cancel_requested_by text,
          lease_owner text,
          lease_token_sha256 text,
          lease_expires_at timestamptz,
          lease_command_id uuid,
          latest_event_sequence bigint NOT NULL DEFAULT 0,
          started_at timestamptz,
          created_at timestamptz NOT NULL DEFAULT now(),
          deadline_at timestamptz NOT NULL,
          updated_at timestamptz NOT NULL DEFAULT now(),
          completed_at timestamptz,
          CONSTRAINT ck_agent_run_kind CHECK (run_kind IN ('direct-agent','orchestrator','worker','verifier')),
          CONSTRAINT ck_agent_run_status CHECK (status IN ('queued','running','waiting_input','waiting_approval','completed','failed','cancelled')),
          CONSTRAINT ck_agent_run_pinned_identity_v2 CHECK (
            tenant_id !~ '^[[:space:]]*$'
            AND char_length(tenant_id) <= 256
            AND user_id !~ '^[[:space:]]*$'
            AND char_length(user_id) <= 256
            AND caller_role !~ '^[[:space:]]*$'
            AND char_length(caller_role) <= 64
            AND tenant_id IS NOT DISTINCT FROM
              execution_snapshot->'caller'->>'tenant_id'
            AND user_id IS NOT DISTINCT FROM
              execution_snapshot->'caller'->>'user_id'
            AND caller_role IS NOT DISTINCT FROM
              execution_snapshot->'caller'->>'role'),
          CONSTRAINT ck_agent_run_versions CHECK (
            state_version >= 1
            AND lease_generation >= 0
            AND checkpoint_generation >= 0
            AND checkpoint_generation <= lease_generation
            AND checkpoint_version >= 0
            AND event_ack_cursor >= 0),
          CONSTRAINT ck_agent_run_direct_lineage CHECK (
            run_kind <> 'direct-agent'
            OR (root_run_id = id AND parent_run_id IS NULL AND task_id IS NULL)),
          CONSTRAINT fk_agent_run_agent_revision FOREIGN KEY (agent_id, agent_revision)
            REFERENCES agent_revision(agent_id, revision),
          CONSTRAINT fk_agent_run_workflow_revision FOREIGN KEY (workflow_id, workflow_revision)
            REFERENCES workflow_revision(workflow_id, revision));
        CREATE INDEX IF NOT EXISTS ix_agent_run_tenant_owner_updated
          ON agent_run (tenant_id, user_id, updated_at DESC);
        -- D7: an approval binds exactly one server-generated action fingerprint and
        -- has no reusable browser token. Decisions are append-only/idempotent.
        ALTER TABLE agent_run DROP CONSTRAINT IF EXISTS ck_agent_run_status;
        ALTER TABLE agent_run ADD CONSTRAINT ck_agent_run_status CHECK(status IN ('queued','running','waiting_input','waiting_approval','completed','failed','cancelled'));
        CREATE TABLE IF NOT EXISTS agent_run_approval (
          id uuid PRIMARY KEY, run_id uuid NOT NULL REFERENCES agent_run(id), tenant_id text NOT NULL,
          requested_by text NOT NULL, required_role text NOT NULL, action_fingerprint text NOT NULL,
          expires_at timestamptz NOT NULL, self_approval_forbidden boolean NOT NULL DEFAULT true,
          status text NOT NULL CHECK(status IN ('pending','approved','rejected','consumed','expired','cancelled')),
          checkpoint_ref text NOT NULL, checkpoint_version bigint NOT NULL,
          decision text, decided_by text, decided_at timestamptz, reason text,
          created_at timestamptz NOT NULL DEFAULT now(),
          CHECK(action_fingerprint ~ '^[0-9a-f]{64}$'),
          CHECK(required_role ~ '^[A-Z][A-Z0-9_]{0,63}$'));
        CREATE INDEX IF NOT EXISTS ix_agent_run_approval_pending ON agent_run_approval(run_id,status,expires_at);
        CREATE TABLE IF NOT EXISTS agent_run_approval_decision (
          approval_id uuid NOT NULL REFERENCES agent_run_approval(id), idempotency_key_sha256 text NOT NULL,
          decision text NOT NULL CHECK(decision IN ('approved','rejected')), approver_id text NOT NULL,
          reason text, created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(approval_id,idempotency_key_sha256));
        -- The durable effect ledger is consumed by the Workflow write boundary before
        -- invoking a side effect; unique action fingerprint makes a retry/restart safe.
        CREATE TABLE IF NOT EXISTS agent_run_write_effect (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), approval_id uuid NOT NULL REFERENCES agent_run_approval(id),
          run_id uuid NOT NULL REFERENCES agent_run(id), action_fingerprint text NOT NULL,
          status text NOT NULL CHECK(status IN ('reserved','completed','failed')),
          created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz,
          UNIQUE(run_id,action_fingerprint));
        CREATE TABLE IF NOT EXISTS agent_run_approval_execute (
          approval_id uuid PRIMARY KEY REFERENCES agent_run_approval(id), run_id uuid NOT NULL REFERENCES agent_run(id),
          tenant_id text NOT NULL, approver_id text NOT NULL, status text NOT NULL CHECK(status IN ('queued','claimed','completed','dead_letter')),
          claim_token_sha256 text, claim_expires_at timestamptz, attempts integer NOT NULL DEFAULT 0,
          created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz);
        CREATE INDEX IF NOT EXISTS ix_agent_run_approval_execute_recovery
          ON agent_run_approval_execute(status,claim_expires_at,created_at) WHERE status IN ('queued','claimed');
        ALTER TABLE agent_run_approval_execute DROP CONSTRAINT IF EXISTS agent_run_approval_execute_status_check;
        ALTER TABLE agent_run_approval_execute ADD CONSTRAINT agent_run_approval_execute_status_check CHECK(status IN ('queued','claimed','completed','dead_letter'));
        ALTER TABLE agent_run_write_effect ADD COLUMN IF NOT EXISTS evidence jsonb;
        CREATE TABLE IF NOT EXISTS agent_run_write_outbox (
          effect_id uuid PRIMARY KEY REFERENCES agent_run_write_effect(id), run_id uuid NOT NULL REFERENCES agent_run(id),
          tenant_id text NOT NULL, payload jsonb NOT NULL, status text NOT NULL CHECK(status IN ('pending','delivered')),
          created_at timestamptz NOT NULL DEFAULT now(), delivered_at timestamptz);
        CREATE INDEX IF NOT EXISTS ix_agent_run_write_outbox_pending ON agent_run_write_outbox(status,created_at) WHERE status='pending';
        -- D5 root aggregate is intentionally separate from agent_run: roots have no Agent
        -- revision, whereas Worker/Verifier children retain D3's immutable Agent snapshots.
        CREATE TABLE IF NOT EXISTS orchestrator_run (
          id uuid PRIMARY KEY, tenant_id text NOT NULL, user_id text NOT NULL, caller_role text NOT NULL,
          orchestrator_id uuid NOT NULL, orchestrator_revision integer NOT NULL, conversation_id text NOT NULL,
          workflow_id uuid NOT NULL, workflow_revision integer NOT NULL,
          execution_snapshot jsonb NOT NULL, execution_snapshot_canonical bytea NOT NULL, snapshot_sha256 text NOT NULL,
          workflow_dispatch_snapshot jsonb, workflow_dispatch_snapshot_canonical bytea, workflow_dispatch_snapshot_sha256 text,
          request_sha256 text NOT NULL, idempotency_key_sha256 text NOT NULL,
          status text NOT NULL CHECK(status IN ('queued','running','waiting_input','completed','failed','cancelled','timed_out')),
          state_version bigint NOT NULL DEFAULT 1, cancel_requested_at timestamptz,
          result jsonb, error_code text, error_message text, completed_at timestamptz,
          checkpoint_ref text, checkpoint_version bigint NOT NULL DEFAULT 0,
          deadline_at timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
          UNIQUE(tenant_id,user_id,idempotency_key_sha256));
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS workflow_dispatch_snapshot jsonb;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS workflow_dispatch_snapshot_canonical bytea;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS workflow_dispatch_snapshot_sha256 text;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS result jsonb;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS error_code text;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS error_message text;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS completed_at timestamptz;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS checkpoint_ref text;
        ALTER TABLE orchestrator_run ADD COLUMN IF NOT EXISTS checkpoint_version bigint NOT NULL DEFAULT 0;
        ALTER TABLE orchestrator_run DROP CONSTRAINT IF EXISTS orchestrator_run_status_check;
        ALTER TABLE orchestrator_run ADD CONSTRAINT orchestrator_run_status_check CHECK(status IN ('queued','running','waiting_input','completed','failed','cancelled','timed_out'));
        -- D6 rollout selection is durable, server-owned and never inferred from a client header.
        CREATE TABLE IF NOT EXISTS tenant_runtime_binding (
          tenant_id text PRIMARY KEY,
          enabled boolean NOT NULL DEFAULT false,
          default_orchestrator_id uuid,
          default_orchestrator_revision integer,
          canary_user_ids jsonb NOT NULL DEFAULT '[]'::jsonb,
          updated_at timestamptz NOT NULL DEFAULT now(),
          CHECK ((NOT enabled) OR (default_orchestrator_id IS NOT NULL AND default_orchestrator_revision >= 1)),
          CHECK (jsonb_typeof(canary_user_ids) = 'array'));
        -- D7 release-control ledger. Evidence references and override reasons are retained for
        -- audit, while public metrics expose aggregates only. Override keys are hashes, never
        -- caller-supplied raw idempotency identities.
        CREATE TABLE IF NOT EXISTS operations_regression_result (
          id uuid PRIMARY KEY, tenant_id text NOT NULL, suite text NOT NULL, passed boolean NOT NULL,
          evidence_ref text NOT NULL, recorded_by text NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now(),
          CHECK (char_length(suite) BETWEEN 1 AND 128), CHECK (char_length(evidence_ref) BETWEEN 1 AND 256),
          CHECK (char_length(recorded_by) BETWEEN 1 AND 256));
        CREATE INDEX IF NOT EXISTS ix_operations_regression_result_tenant_latest
          ON operations_regression_result(tenant_id,recorded_at DESC,id DESC);
        CREATE TABLE IF NOT EXISTS operations_regression_override (
          tenant_id text NOT NULL, idempotency_key_sha256 text NOT NULL, regression_id uuid NOT NULL REFERENCES operations_regression_result(id),
          reason text NOT NULL, actor_id text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY(tenant_id,idempotency_key_sha256), CHECK (idempotency_key_sha256 ~ '^[0-9a-f]{64}$'),
          CHECK (char_length(reason) BETWEEN 8 AND 1000), CHECK (char_length(actor_id) BETWEEN 1 AND 256));
        CREATE INDEX IF NOT EXISTS ix_operations_regression_override_gate
          ON operations_regression_override(tenant_id,regression_id);
        CREATE TABLE IF NOT EXISTS operations_release_audit (
          id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, tenant_id text NOT NULL, kind text NOT NULL,
          outcome text NOT NULL, actor_id text NOT NULL, detail text NOT NULL, occurred_at timestamptz NOT NULL DEFAULT now(),
          CHECK (kind IN ('regression','regression_override','rollout','rollback')),
          CHECK (char_length(outcome) BETWEEN 1 AND 64), CHECK (char_length(actor_id) BETWEEN 1 AND 256),
          CHECK (char_length(detail) BETWEEN 1 AND 256));
        CREATE INDEX IF NOT EXISTS ix_operations_release_audit_tenant_time
          ON operations_release_audit(tenant_id,occurred_at DESC,id DESC);
        CREATE TABLE IF NOT EXISTS operations_execution_metric (
          tenant_id text NOT NULL, run_id uuid NOT NULL REFERENCES agent_run(id), event_id uuid NOT NULL,
          kind text NOT NULL CHECK(kind IN ('model','tool','node')), node_id text, tool_name text, skill_name text, skill_revision integer, agent_id text, agent_revision integer,
          usage_units bigint, cost_units numeric(18,6), latency_ms bigint,
          observed_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(tenant_id,run_id,event_id),
          CHECK (usage_units IS NULL OR usage_units >= 0), CHECK (cost_units IS NULL OR cost_units >= 0), CHECK (latency_ms IS NULL OR latency_ms >= 0));
        CREATE INDEX IF NOT EXISTS ix_operations_execution_metric_tenant_time
          ON operations_execution_metric(tenant_id,observed_at DESC);
        ALTER TABLE operations_execution_metric ADD COLUMN IF NOT EXISTS skill_name text, ADD COLUMN IF NOT EXISTS skill_revision integer, ADD COLUMN IF NOT EXISTS agent_id text, ADD COLUMN IF NOT EXISTS agent_revision integer;
        CREATE UNIQUE INDEX IF NOT EXISTS uq_orchestrator_run_active_conversation
          ON orchestrator_run(tenant_id,user_id,conversation_id,orchestrator_id)
          WHERE status IN ('queued','running','waiting_input');
        CREATE TABLE IF NOT EXISTS orchestrator_run_event (
          run_id uuid NOT NULL REFERENCES orchestrator_run(id), sequence bigint NOT NULL,
          event_type text NOT NULL, snapshot_sha256 text NOT NULL, payload jsonb NOT NULL DEFAULT '{}',
          created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(run_id,sequence));
        CREATE TABLE IF NOT EXISTS orchestrator_run_command (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), run_id uuid NOT NULL REFERENCES orchestrator_run(id),
          command_type text NOT NULL CHECK(command_type IN ('start','cancel')), dispatch_attempt integer NOT NULL DEFAULT 0,
          claim_owner text, claim_token_sha256 text, claim_expires_at timestamptz, dispatch_completed_at timestamptz, completed_at timestamptz, lease_generation bigint NOT NULL DEFAULT 0,
          idempotency_key_sha256 text, command_input jsonb, command_input_sha256 text,
          created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(run_id,command_type));
        CREATE INDEX IF NOT EXISTS ix_orchestrator_run_command_reclaim ON orchestrator_run_command(claim_expires_at,created_at) WHERE completed_at IS NULL;
        ALTER TABLE orchestrator_run_command ADD COLUMN IF NOT EXISTS claim_owner text;
        ALTER TABLE orchestrator_run_command ADD COLUMN IF NOT EXISTS dispatch_completed_at timestamptz;
        ALTER TABLE orchestrator_run_command ADD COLUMN IF NOT EXISTS idempotency_key_sha256 text;
        ALTER TABLE orchestrator_run_command ADD COLUMN IF NOT EXISTS command_input jsonb;
        ALTER TABLE orchestrator_run_command ADD COLUMN IF NOT EXISTS command_input_sha256 text;
        ALTER TABLE orchestrator_run_command DROP CONSTRAINT IF EXISTS orchestrator_run_command_command_type_check;
        ALTER TABLE orchestrator_run_command ADD CONSTRAINT orchestrator_run_command_command_type_check CHECK(command_type IN ('start','resume','cancel'));
        -- A root may legitimately pause and resume more than once. Keep start/cancel
        -- singletons, but scope resume uniqueness to its caller idempotency key.
        ALTER TABLE orchestrator_run_command DROP CONSTRAINT IF EXISTS orchestrator_run_command_run_id_command_type_key;
        CREATE UNIQUE INDEX IF NOT EXISTS uq_orchestrator_run_command_start
          ON orchestrator_run_command(run_id) WHERE command_type='start';
        CREATE UNIQUE INDEX IF NOT EXISTS uq_orchestrator_run_command_cancel
          ON orchestrator_run_command(run_id) WHERE command_type='cancel';
        CREATE UNIQUE INDEX IF NOT EXISTS uq_orchestrator_run_command_resume_idempotency
          ON orchestrator_run_command(run_id,idempotency_key_sha256)
          WHERE command_type='resume' AND idempotency_key_sha256 IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_orchestrator_run_command_idempotency_lookup
          ON orchestrator_run_command(idempotency_key_sha256,run_id)
          WHERE idempotency_key_sha256 IS NOT NULL;
        CREATE TABLE IF NOT EXISTS orchestrator_run_child (
          id uuid PRIMARY KEY, orchestrator_root_run_id uuid NOT NULL REFERENCES orchestrator_run(id),
          task_id text NOT NULL, attempt integer NOT NULL CHECK(attempt>=1), run_kind text NOT NULL CHECK(run_kind IN ('worker','verifier')),
          agent_id uuid NOT NULL, agent_revision integer NOT NULL, workflow_id uuid NOT NULL, workflow_revision integer NOT NULL,
          agent_snapshot_sha256 text NOT NULL, agent_run_id uuid, command_id uuid, task_envelope jsonb NOT NULL DEFAULT '{}', dispatch_artifact jsonb NOT NULL, status text NOT NULL CHECK(status IN ('queued','running','completed','failed','cancelled')),
          created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
          UNIQUE(orchestrator_root_run_id,task_id,attempt));
        CREATE INDEX IF NOT EXISTS ix_orchestrator_run_child_active ON orchestrator_run_child(orchestrator_root_run_id,status);
        ALTER TABLE orchestrator_run_child ADD COLUMN IF NOT EXISTS agent_run_id uuid;
        ALTER TABLE orchestrator_run_child ADD COLUMN IF NOT EXISTS command_id uuid;
        ALTER TABLE orchestrator_run_child ADD COLUMN IF NOT EXISTS task_envelope jsonb NOT NULL DEFAULT '{}';
        CREATE UNIQUE INDEX IF NOT EXISTS uq_orchestrator_run_child_agent_run ON orchestrator_run_child(agent_run_id) WHERE agent_run_id IS NOT NULL;
        ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS orchestrator_root_run_id uuid;
        DO $orchestrator_child_lineage$
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='fk_agent_run_orchestrator_root') THEN
            ALTER TABLE agent_run ADD CONSTRAINT fk_agent_run_orchestrator_root
              FOREIGN KEY (orchestrator_root_run_id) REFERENCES orchestrator_run(id);
          END IF;
        END $orchestrator_child_lineage$;
        CREATE INDEX IF NOT EXISTS ix_agent_run_orchestrator_root
          ON agent_run(orchestrator_root_run_id) WHERE orchestrator_root_run_id IS NOT NULL;
        ALTER TABLE agent_run
          ADD COLUMN IF NOT EXISTS execution_snapshot_canonical bytea,
          ADD COLUMN IF NOT EXISTS lease_generation bigint NOT NULL DEFAULT 0,
          ADD COLUMN IF NOT EXISTS checkpoint_generation bigint NOT NULL DEFAULT 0,
          ADD COLUMN IF NOT EXISTS event_ack_cursor bigint NOT NULL DEFAULT 0,
          ADD COLUMN IF NOT EXISTS deadline_at timestamptz,
          ADD COLUMN IF NOT EXISTS caller_role text,
          ADD COLUMN IF NOT EXISTS lease_command_id uuid;
        UPDATE agent_run
          SET caller_role =
            (convert_from(execution_snapshot_canonical, 'UTF8')::jsonb)
              ->'caller'->>'role'
          WHERE caller_role IS NULL;
        ALTER TABLE agent_run
          ALTER COLUMN caller_role SET NOT NULL;
        DO $agent_run_pinned_identity$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'ck_agent_run_pinned_identity_v2'
              AND conrelid = 'agent_run'::regclass) THEN
            ALTER TABLE agent_run
              ADD CONSTRAINT ck_agent_run_pinned_identity_v2 CHECK (
                tenant_id !~ '^[[:space:]]*$'
                AND char_length(tenant_id) <= 256
                AND user_id !~ '^[[:space:]]*$'
                AND char_length(user_id) <= 256
                AND caller_role !~ '^[[:space:]]*$'
                AND char_length(caller_role) <= 64
                AND tenant_id IS NOT DISTINCT FROM
                  execution_snapshot->'caller'->>'tenant_id'
                AND user_id IS NOT DISTINCT FROM
                  execution_snapshot->'caller'->>'user_id'
                AND caller_role IS NOT DISTINCT FROM
                  execution_snapshot->'caller'->>'role') NOT VALID;
          END IF;
        END
        $agent_run_pinned_identity$;
        ALTER TABLE agent_run VALIDATE CONSTRAINT ck_agent_run_pinned_identity_v2;
        UPDATE agent_run
          SET deadline_at = created_at + interval '60 seconds'
          WHERE deadline_at IS NULL;
        ALTER TABLE agent_run
          ALTER COLUMN deadline_at SET NOT NULL;
        DO $agent_run_fencing$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'ck_agent_run_fencing_v2'
              AND conrelid = 'agent_run'::regclass) THEN
            ALTER TABLE agent_run
              ADD CONSTRAINT ck_agent_run_fencing_v2 CHECK (
                state_version >= 1
                AND lease_generation >= 0
                AND checkpoint_generation >= 0
                AND checkpoint_generation <= lease_generation
                AND checkpoint_version >= 0
                AND event_ack_cursor >= 0) NOT VALID;
          END IF;
        END
        $agent_run_fencing$;
        ALTER TABLE agent_run VALIDATE CONSTRAINT ck_agent_run_fencing_v2;
        CREATE INDEX IF NOT EXISTS ix_agent_run_status_lease
          ON agent_run (status, lease_expires_at)
          WHERE status IN ('queued','running');

        CREATE TABLE IF NOT EXISTS agent_run_skill (
          run_id uuid NOT NULL REFERENCES agent_run(id),
          position integer NOT NULL,
          skill_id uuid NOT NULL,
          skill_revision integer NOT NULL,
          skill_name text NOT NULL,
          kind text NOT NULL,
          definition_sha256 text NOT NULL,
          package_sha256 text,
          PRIMARY KEY (run_id, skill_id),
          CONSTRAINT uq_agent_run_skill_position UNIQUE (run_id, position),
          CONSTRAINT fk_agent_run_skill_revision FOREIGN KEY (skill_id, skill_revision)
            REFERENCES skill_revision(skill_id, revision));

        CREATE TABLE IF NOT EXISTS agent_run_event (
          run_id uuid NOT NULL REFERENCES agent_run(id),
          sequence bigint NOT NULL,
          event_id uuid NOT NULL,
          event_type text NOT NULL,
          node_id text,
          lease_generation bigint,
          event_cursor bigint,
          snapshot_sha256 text NOT NULL,
          payload jsonb NOT NULL DEFAULT '{}',
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY (run_id, sequence),
          CONSTRAINT uq_agent_run_event_id UNIQUE (run_id, event_id));
        ALTER TABLE agent_run_event
          ADD COLUMN IF NOT EXISTS lease_generation bigint,
          ADD COLUMN IF NOT EXISTS event_cursor bigint;
        CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_run_event_cursor
          ON agent_run_event (run_id, event_cursor)
          WHERE event_cursor IS NOT NULL;
        DO $agent_run_event_fencing$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'ck_agent_run_event_fencing'
              AND conrelid = 'agent_run_event'::regclass) THEN
            ALTER TABLE agent_run_event
              ADD CONSTRAINT ck_agent_run_event_fencing CHECK (
                (lease_generation IS NULL AND event_cursor IS NULL)
                OR (lease_generation >= 1 AND event_cursor >= 0)) NOT VALID;
          END IF;
        END
        $agent_run_event_fencing$;
        ALTER TABLE agent_run_event
          VALIDATE CONSTRAINT ck_agent_run_event_fencing;

        CREATE TABLE IF NOT EXISTS agent_run_command (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          command_sequence bigint GENERATED ALWAYS AS IDENTITY,
          tenant_id text NOT NULL,
          user_id text NOT NULL,
          run_id uuid NOT NULL REFERENCES agent_run(id),
          command_type text NOT NULL,
          idempotency_key_sha256 text NOT NULL,
          request_sha256 text NOT NULL,
          command_input jsonb NOT NULL DEFAULT '{}',
          command_input_sha256 char(64) NOT NULL,
          dispatch_claim_owner text,
          dispatch_claim_token_sha256 text,
          dispatch_claim_expires_at timestamptz,
          dispatch_attempts integer NOT NULL DEFAULT 0,
          dispatch_completed_at timestamptz,
          execution_recovery_lease_token_sha256 text,
          last_dispatch_at timestamptz,
          created_at timestamptz NOT NULL DEFAULT now(),
          CONSTRAINT ck_agent_run_command_type CHECK (
            command_type IN ('start','resume','cancel','deadline_cleanup')),
          CONSTRAINT uq_agent_run_command_idempotency
            UNIQUE (tenant_id, user_id, command_type, idempotency_key_sha256));
        ALTER TABLE agent_run_command
          ADD COLUMN IF NOT EXISTS command_sequence bigint GENERATED ALWAYS AS IDENTITY,
          ADD COLUMN IF NOT EXISTS command_input jsonb NOT NULL DEFAULT '{}',
          ADD COLUMN IF NOT EXISTS command_input_sha256 char(64),
          ADD COLUMN IF NOT EXISTS dispatch_claim_owner text,
          ADD COLUMN IF NOT EXISTS dispatch_claim_token_sha256 text,
          ADD COLUMN IF NOT EXISTS dispatch_claim_expires_at timestamptz,
          ADD COLUMN IF NOT EXISTS dispatch_attempts integer NOT NULL DEFAULT 0,
          ADD COLUMN IF NOT EXISTS dispatch_completed_at timestamptz,
          ADD COLUMN IF NOT EXISTS execution_recovery_lease_token_sha256 text,
          ADD COLUMN IF NOT EXISTS last_dispatch_at timestamptz;
        DO $agent_run_command_type$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'ck_agent_run_command_type'
              AND conrelid = 'agent_run_command'::regclass
              AND pg_get_constraintdef(oid) NOT LIKE '%deadline_cleanup%') THEN
            ALTER TABLE agent_run_command
              DROP CONSTRAINT ck_agent_run_command_type;
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'ck_agent_run_command_type'
              AND conrelid = 'agent_run_command'::regclass) THEN
            ALTER TABLE agent_run_command
              ADD CONSTRAINT ck_agent_run_command_type CHECK (
                command_type IN ('start','resume','cancel','deadline_cleanup')) NOT VALID;
          END IF;
        END
        $agent_run_command_type$;
        ALTER TABLE agent_run_command
          VALIDATE CONSTRAINT ck_agent_run_command_type;
        CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_run_deadline_cleanup
          ON agent_run_command (run_id)
          WHERE command_type='deadline_cleanup';
        CREATE INDEX IF NOT EXISTS ix_agent_run_command_recovery
          ON agent_run_command (dispatch_claim_expires_at, created_at)
          WHERE dispatch_completed_at IS NULL;
        CREATE INDEX IF NOT EXISTS ix_agent_run_command_run_sequence
          ON agent_run_command (run_id, command_sequence DESC);
        """;

    public static async Task RunAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_lock(@lockId)",
                new { lockId = BootstrapAdvisoryLockId },
                cancellationToken: ct));
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(Ddl, cancellationToken: ct));
                await MigrateAgentRunCommandInputHashesAsync(conn, logger, ct);
                await MigrateSkillPackagesAndNamesAsync(conn, logger, ct);
                await SeedAsync(conn, ct);
            }
            finally
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_unlock(@lockId)",
                    new { lockId = BootstrapAdvisoryLockId },
                    cancellationToken: CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "無法連線或初始化資料庫,backend 啟動中止。請確認 appdb 已啟動且 DB_CONNECTION_STRING 正確。");
            throw;
        }
    }

    private static async Task MigrateAgentRunCommandInputHashesAsync(
        NpgsqlConnection conn,
        ILogger logger,
        CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<AgentRunCommandHashMigrationRow>(
            new CommandDefinition(
                "SELECT c.id AS Id,c.command_type AS CommandType,"
                + " c.command_input::text AS CommandInput,"
                + " r.checkpoint_generation AS CheckpointGeneration,"
                + " r.checkpoint_version AS CheckpointVersion,"
                + " r.checkpoint_ref AS CheckpointRef"
                + " FROM agent_run_command c"
                + " JOIN agent_run r ON r.id=c.run_id"
                + " WHERE c.command_input_sha256 IS NULL"
                + " ORDER BY c.command_sequence FOR UPDATE OF c",
                transaction: tx,
                cancellationToken: ct))).AsList();

        foreach (var row in rows)
        {
            var commandInputHash = new string('0', 64);
            if (AgentRunCommandInput.TryParseAndValidate(
                    row.CommandInput,
                    row.CommandType,
                    row.CheckpointGeneration,
                    row.CheckpointVersion,
                    row.CheckpointRef,
                    AgentRunCommandValidationMode.Recovery,
                    out var validatedInput,
                    out _))
            {
                commandInputHash = AgentRunCommandInput.CanonicalSha256(
                    validatedInput,
                    row.CommandType);
            }
            else
            {
                logger.LogWarning(
                    "Legacy agent-run command {CommandId} has invalid input; "
                    + "installing a fail-closed integrity marker",
                    row.Id);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run_command"
                + " SET command_input_sha256=@commandInputHash"
                + " WHERE id=@Id AND command_input_sha256 IS NULL",
                new { row.Id, commandInputHash },
                tx,
                cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE agent_run_command"
            + " ALTER COLUMN command_input_sha256 SET NOT NULL",
            transaction: tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
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

    private sealed record AgentRunCommandHashMigrationRow(
        Guid Id,
        string CommandType,
        string CommandInput,
        long CheckpointGeneration,
        long CheckpointVersion,
        string? CheckpointRef);

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

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_group_membership (tenant_id,user_id,group_id)"
            + " SELECT t.id,u.id,seed.group_id"
            + " FROM (VALUES"
            + " ('demo-a','admin-a','operations'),"
            + " ('demo-a','user-a','analysts'),"
            + " ('demo-b','user-b','analysts'))"
            + " AS seed(tenant_code,username,group_id)"
            + " JOIN tenants t ON t.code=seed.tenant_code"
            + " JOIN users u ON u.username=seed.username AND u.tenant_id=t.id"
            + " ON CONFLICT DO NOTHING",
            cancellationToken: ct));

        await SeedDefaultWorkflowAsync(conn, ct);
    }

    /// <summary>
    /// 種入 system-owned Default Agent-Runtime Workflow current revision(published、不可編輯)。
    /// rev1/rev2 都是歷史快照，絕不原地 repair；新版 fixture 以 rev3 寫入，新 Agent 預設 pin rev3。
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
            + "  draft_ui_metadata, draft_definition_canonical, draft_ui_metadata_canonical,"
            + "  draft_definition_sha256, draft_ui_metadata_sha256, published_revision, system_owned, created_by)"
            + " VALUES (@id, @tenant, @name, @kind, true, 1, @def::jsonb, '{}'::jsonb, convert_to(@def,'UTF8'),"
            + "  convert_to('{}','UTF8'), @sha, @uiSha, @revision, true, 'system')"
            + " ON CONFLICT (id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, name = EXCLUDED.name,"
            + "  kind = EXCLUDED.kind, enabled = true, draft_version = 1,"
            + "  draft_definition = EXCLUDED.draft_definition, draft_ui_metadata = '{}'::jsonb,"
            + "  draft_definition_canonical = EXCLUDED.draft_definition_canonical,"
            + "  draft_ui_metadata_canonical = EXCLUDED.draft_ui_metadata_canonical,"
            + "  draft_definition_sha256 = EXCLUDED.draft_definition_sha256,"
            + "  draft_ui_metadata_sha256 = EXCLUDED.draft_ui_metadata_sha256,"
            + "  published_revision = EXCLUDED.published_revision, system_owned = true, updated_at = now()",
            args, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workflow_revision (workflow_id, revision, status, schema_version, definition, ui_metadata,"
            + "  definition_canonical, ui_metadata_canonical, definition_sha256, ui_metadata_sha256, compiler_contract_version, created_by)"
            + " VALUES (@id, @revision, 'published', 1, @def::jsonb, '{}'::jsonb, convert_to(@def,'UTF8'),"
            + "  convert_to('{}','UTF8'), @sha, @uiSha, @compilerContract, 'system')"
            + " ON CONFLICT (workflow_id, revision) DO NOTHING",
            new { args.id, args.tenant, args.name, args.kind, args.revision, args.def, args.sha, args.uiSha, compilerContract = WorkflowCompilerContracts.Current }, tx, cancellationToken: ct));

        var currentRevisionMatches = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS("
            + " SELECT 1 FROM workflow_revision"
            + " WHERE workflow_id = @id AND revision = @revision"
            + " AND status = 'published' AND schema_version = 1"
            + " AND definition = @def::jsonb AND definition_canonical = convert_to(@def,'UTF8')"
            + " AND definition_sha256 = @sha"
            + " AND ui_metadata = '{}'::jsonb AND ui_metadata_canonical = convert_to('{}','UTF8')"
            + " AND ui_metadata_sha256 = @uiSha"
            + " AND compiler_contract_version = @compilerContract)",
            new { args.id, args.revision, args.def, args.sha, args.uiSha, compilerContract = WorkflowCompilerContracts.Current }, tx, cancellationToken: ct));
        if (!currentRevisionMatches)
        {
            throw new InvalidOperationException(
                $"Default Agent-Runtime Workflow rev{AgentDefaults.RuntimeWorkflowRevision} 已存在但內容不同；"
                + "published revision 不可原地覆寫，請建立下一個 revision");
        }

        await tx.CommitAsync(ct);
    }
}
