# P3 盤點 dossier(2026-08-05,baseline b93b63c)

> 本文件由六份平行盤點子代理報告整合而成,是 P3-1~P3-7 派工的事實依據。
> 所有 file:line 以 HEAD b93b63c 驗證;施工前若 HEAD 前進須重驗。
>
> 六份來源報告(Explore 子代理,HEAD b93b63c):SQL 目標 schema(task `a10d44cfa02b7b78c`)、backend 消費圖(`aeddf35c7b204f4aa`)、workflow 拆分縫(`a39c99455d687ca7a`)、契約與測試清理面(`aa48783c47a0b4c07`)、platform 拆分面(`ac2f43de5ae479f14`)、frontend 拆分面(`a07ec528499607299`)。全部六份原文均已尋獲並完整保留於下方第 3–8 節。

## 1. 待裁決事項(阻擋施工)

### 1.1 SQL 撰寫裁決 ×8

出處:第 3 節(SQL 目標 schema 報告)§「待裁決事項(阻擋 SQL 撰寫,共 8 項)」。

1. Agent↔Skill pin 表與兩張 typed run pin 表的**正式表名**(02-spec 未命名)。
2. 兩張 run pin 表的 `position` 是否共用序號空間。
3. `agent_skill` 是否真的無 `definition` 欄(spec §2.1 未列,但今日 `skill.definition` 是引擎事實來源)。
4. `agent_skill_revision.instruction_sha256` 的計算來源定義(全新欄位,repo 無既有實作)。
5. `orchestrator_revision.verifier_agent_*` 與 `orchestrator_run.*` 的缺失 FK 是否補建(ledger 未涵蓋)。
6. `agent_run` 兩條重複 fencing CHECK 保留哪一條。
7. `tenant_runtime_binding.canary_user_ids` 在 P4 刪 canary flag 後的去留。
8. D5 seed 走 raw SQL 還是 repository publish 路徑(後者需 Workflow 服務在 seed 時可達)。

### 1.1.1 建議與預決(2026-08-05,主代理綜合六份報告後提出)

可由規格/工程慣例推導的 6 項,除非使用者否決,依下列預決施工;第 3/7 項(定義欄、canary)與 seed 路徑屬產品/營運層,另行送使用者裁決:

1. **表名**:沿既有命名慣例 — binding 表 `agent_revision_agent_skill` + `agent_revision_business_workflow`;run pin 表 `agent_run_agent_skill` + `agent_run_business_workflow`(取代今日 `agent_revision_skill`/`agent_run_skill`)。
2. **position 序號空間**:兩張 run pin 表**各自獨立**,各建 `uq_*_position(run_id, position)`;snapshot 的兩個 typed collection 各自有序,不跨表混編(P3-D3 已裁定 typed 集合,共用序號空間無消費者)。
3. (送裁決)`agent_skill.definition` 欄有無 — 建議**無**:Agent Skill 的事實來源是 package,SKILL.md 指令文字由 package 解出,不再落獨立欄。
4. **`instruction_sha256` 定義**:package 內 `SKILL.md` 全文的 canonical hash(`SkillHash.Sha256`,LF 正規化,與 P2 manifest checksum 同法),與 `package_sha256`(整包)並存 — 前者鎖指令內容,後者鎖整個工件。
5. **缺失 FK**:**補建**。`orchestrator_revision.workflow_*`→`workflow_revision`、`orchestrator_revision.verifier_agent_*`→`agent_revision`、`orchestrator_run` 對應欄位同理 — hard cutover 是唯一零成本補 FK 的時機。
6. **重複 fencing CHECK**:只保留 `ck_agent_run_fencing_v2`,刪 `ck_agent_run_versions` — 消除 fresh/legacy fingerprint 分歧(§3 表 18 ⚠ 項)。
7. (送裁決)`canary_user_ids` 去留 — 建議 P3 **保留欄位不動**,P4 拆 canary 機制時一併裁決;P3 seed 依現行 D6 語意給值。
8. (送裁決)D5 seed 路徑 — 建議 **raw SQL + 預驗 fixture + byte-for-byte 事後驗證**(沿 `SeedDefaultWorkflowAsync` pattern),不依賴 Workflow 服務可達;orchestrator publish 的跨服務 revalidation 以「fixture 內容在 repo 內預先通過 D4 驗證」補償。

### 1.2 規格衝突 ×2

出處:第 6 節(platform 拆分報告)§「附:兩份 spec 的衝突點(P3 動手前需裁決)」。

- **revisions 歸屬**:`plans\skill-concept-realignment\02-spec.md:95`「revisions/restore 一律留在 Skill 面,BW 不提供 revisions 路由」 vs `plans\architecture-hard-reset\02-spec.md:88`「`/api/business-workflows*` … revisions」。後者較新(hard-reset 於 `01-plan.md:3` 明示 supersede),且 `02-spec.md:61-68` 已為 BW 建獨立 revision 表 —— 以 hard-reset 為準,但 skill-concept 那份未加 superseded 標記。**預決(2026-08-05):以 hard-reset 為準;P3-7 文件同步時在 `plans/skill-concept-realignment/02-spec.md` 加 superseded 標記**,免得後續實作者誤讀舊規格。
- **`rag-qa` / `template-*` 的 kind 歸屬**:決定 `SkillRoutingAgent.cs:501` 與 `:396-400` 是 retarget 還是刪除,需 workflow 側 catalog 拆分結果才能定案。**預決(2026-08-05):retarget 到 Business Workflow invoke,不刪** — 12 個 builtin 全是 flow(見下),聊天路由依 AGENTS.md 本就 kind-agnostic,刪除會直接砍掉聊天的 skill 路由能力。同理 `AppShell.tsx:192` 與 `verify-copilot-shared-core.ps1` C-07 都改打 `/api/business-workflows/rag-qa/invoke`,與 P3-5/P3-6 同 tranche。呼應第 5 節(workflow 報告)§5:12 個 builtin YAML(`analyze-report`、`context-enrichment`、`context-task-local`、`kb-query`、`rag-qa`、`summarize`、`template-compare`、`template-infer`、`template-inspire`、`template-retrieval`、`template-stats`、`triage`)皆未宣告 `kind:`,全部預設 `flow`,故今日全是 Business Workflow、零 builtin Agent Skill;第 7 節(frontend 報告)§2 亦發現 `AppShell.tsx:192` 呼叫 `invokeSkill('rag-qa', …)` 實際打的是一個 Business Workflow。

### 1.3 工作樹前置條件

`git status` 顯示 91 個檔案有未提交的平行變更(JWT 簽章/驗證改動、安全性硬化、CI 工作流等,橫跨 backend/platform/frontend/infra/scripts/plans),屬於本次盤點範圍以外的並行工作;P3 開工前必須由使用者裁決這批變更如何處理(提交、暫存或捨棄),**不得在髒樹上開工**,否則後續 file:line 事實與 diff 基準全部失真。

## 2. 六份盤點與 ledger 的落差(施工前修正清單)

### 2.1 workflow 報告(第 5 節)§「Deltas vs. the ledger worth recording before P3 starts」— 6 條

1. Ledger §3 row "Workflow exception exposure" cites `main.py:168-175` / `main.py:508-512` / `evals/api.py:48-52` — actual post-P1: `main.py:308-323`, `main.py:658-660`, `evals/api.py:51-53`. That row is **done** (P1-05), lines are stale.
2. `compiler._build_graph` is at `:502-505`, not `:503-505`.
3. `EvalCandidate` kind rejection is at `api.py:95-102`, not `api.py:69-79`; the `69-79` range now holds the *artifact*-kind rejection (`api.py:70-80`).
4. `POST /business-workflows/validate` **already exists** (`main.py:550-554`) — §3.3 route 3 is partially landed; P3 only deletes the alias decorator at `main.py:555`.
5. `ActiveSkillScope` does not need two variants (a flow never enters a scope, `graph.py:787` returns before `_enter_skill_scope`) — one rename + one field deletion satisfies P3-D3 for that type. 02-spec §2.3 says all three types "are partitioned into two variants"; the scope type is a genuine exception.
6. `engine/package.py` `_parse_flow` (`:853-907`) has **no P3 consumer** after `/skills/validate-package` → `/agent-skills/validate-package`. It is probably a delete rather than a split; `tests\test_package_parser.py` needs a consumer audit first.

### 2.2 backend 報告(第 4 節)§「Gaps / discrepancies against the ledger worth flagging before P3 starts」— 7 條

1. **`SkillHash` blast radius is ~2.3× the recorded number** — ledger §2 row 51 says "24 files across 10 modules"; HEAD has **56 files / 320 sites** (32 production files). Row 51's "move to a shared namespace" is a 56-file `using` churn, and it now includes `Data\Migrations\MigrationManifest.cs:4,165` (P2, didn't exist when the row was written).
2. **`PostgresFixture` fan-out is 16 files, not 14** — P2 added `Migrations\DbMigrationRunnerTests.cs` and `Migrations\SchemaFingerprintTests.cs`. Ledger §7's "37 test files green-by-skip" warning (02-spec §6.1) and its 14-file list both need refreshing. `DbBootstrap.RunAsync` direct sites are unchanged (32 test + 1 production).
3. **`operations_run_evidence.skill_name` is at `DbBootstrap.cs:532`, not `:531`** (ledger §1 row 23). `:509` and `:515` are still exact.
4. **`SkillMetadata` bidirectional pull (ledger row 54) is unresolved in code but resolved in spec** — 02-spec §3.4's "Decided 2026-08-03" says split into two independent metadata types with no `Kind`. Row 54 still reads "mark as P3 pre-spec decision pending"; the ledger row should be updated to point at §3.4 so the implementer doesn't re-litigate it.
5. **`ProductionManifestAbsenceTests.cs:27-29` effectively names the three P3-1 SQL files** (`architecture_hard_reset`, `target_schema`, `constraints_and_indexes`). Whatever P3-1 names `0001`/`0002`/`0003`, these three `DoesNotContain` filters must be inverted to `Contains` (or the whole fact replaced by a presence test) — otherwise the file names silently drift from the assertions that were supposed to guard them.
6. **`BusinessWorkflowController` has no `/validate` route today** despite 02-spec §3.1 declaring `/api/business-workflows/validate` the canonical replacement for the deleted `/api/skills/validate`. It is a **new** Backend route in P3, not a rename — same for `/api/business-workflows/{name}/revisions/{revision}/execution-artifact` (§3.2) and Business Workflow revisions/restore generally, which exist only on the Skill route at HEAD (`SkillController.cs:88,107,308`).
7. **`agent_run_skill.skill_id` has no FK to `skill(id)`** — only the composite FK to `skill_revision` (`DbBootstrap.cs:697-698`). A `0001` that drops `skill` before `skill_revision` will not be blocked by this table; the ordering constraint comes from `skill_revision.skill_id → skill(id)` at `:127` and `agent_revision_skill.skill_id → skill(id)` at `:230`.

### 2.3 contracts/tests 報告(第 8 節)§1 — 文件與 ledger 矛盾陳述

- `workflow/AGENTS.md` 明文:`POST /skills/validate` 是同 handler 相容 alias,「P5/C8 不移除此 alias」——但 05-ledger §3 行 61 明確標 **P3** 刪除。這是 AGENTS.md(現狀文件)與 05-ledger(P3 目標)之間的已知落差,P3-7 文件同步必須處理此矛盾陳述(AGENTS.md 目前的說法會在 P3 後失真)。
- 未找到任何名稱含 `410` 字面字串的 backend 測試檔(`Grep 410` on `backend/tests` 零結果),表示 410 化仍待 P5/C8,不在 P3 範圍——與 ledger 對「dual-track 收斂」的分期一致,列此處供交叉核對。

### 2.4 platform 報告的規格衝突

已併入第 1.2 節(revisions 歸屬、rag-qa/template-* kind 歸屬),不重複列出。

## 3. SQL 目標 schema(P3-1 輸入)

### 1. 逐表 DDL 全量盤點(baseline: HEAD b93b63c,`DbBootstrap.cs`)

#### 1.0 全域物件

| 類別 | 內容 |
|---|---|
| EXTENSION | 僅 `CREATE EXTENSION IF NOT EXISTS vector`(L23)。**無 `CREATE EXTENSION pgcrypto`**,但 DDL 大量使用 `gen_random_uuid()` → 隱含要求 PostgreSQL ≥ 13(core 內建)。`0002` 必須沿用同一假設或顯式建 pgcrypto(會改變 fingerprint,建議沿用)。 |
| VIEW / MATERIALIZED VIEW | **無**(全檔 0 個 `CREATE VIEW`/`CREATE MATERIALIZED VIEW`) |
| TRIGGER | **無使用者 trigger**。但 pg_trigger 內有大量 `tgisinternal=true` 的 RI trigger(每條 FK 2–3 筆)→ absence 斷言必須 `WHERE NOT tgisinternal`,否則必定誤報。 |
| RLS | **無**。所有表 `relrowsecurity=false`、`relforcerowsecurity=false`,`pg_policy` 空。 |
| FUNCTION / PROCEDURE | **無**。5 個 `DO $$` 匿名區塊(`$app_config_tenant$` L85、`$constraints$` L298、`$orchestrator_child_lineage$` L611、`$agent_run_pinned_identity$` L635、`$agent_run_fencing$` L664、`$agent_run_event_fencing$` L719、`$agent_run_command_type$` L769 — 共 7 個)執行後不留任何 catalog 物件。 |
| 自訂 TYPE / DOMAIN / ENUM | **無**(狀態值全用 text + CHECK) |
| SEQUENCE | **無顯式 `CREATE SEQUENCE`**,但有 **5 個 identity 隱含 sequence**(relkind='S',`pg_depend deptype='a'`):`tenants_id_seq`、`users_id_seq`、`conversations_id_seq`、`operations_release_audit_id_seq`、`agent_run_command_command_sequence_seq`。⚠ 03-design §1.3 的 extras 掃描含 `relkind='S'` → 這 5 個必須進 allowlist,或掃描加 `deptype='a'` 排除。漏掉會讓正確 DB 被判 unknown。 |
| SCHEMA | 只有 `public`(`springaitest_meta` 尚未存在,P2 才建) |

#### 1.1 逐表清單(50 表)

格式:`欄位 型別 [NN=NOT NULL] [DEFAULT]`;`†` = 由後補 ALTER 加入(legacy 與 fresh 的 attnum 順序會不同,見 §1.2 風險)。

**1. tenants** (L24-27)
`id bigint GENERATED ALWAYS AS IDENTITY PK` / `code text NN UNIQUE` / `name text NN` / `invite_code text NN` / `created_at timestamptz NN now()`
UNIQUE: `tenants_code_key(code)`。FK/CHECK/索引:無。

**2. users** (L28-36)
`id bigint IDENTITY PK` / `username text NN UNIQUE` / `password_hash text NN` / `role text NN` / `tenant_id bigint NN` / `capabilities text[] NN '{}'`† / `created_at timestamptz NN now()`
FK: `tenant_id → tenants(id)`(NO ACTION)。UNIQUE: `users_username_key`。索引: `ux_users_id_tenant UNIQUE (id, tenant_id)` ← **必要**,是 `user_group_membership` 複合 FK 的目標。

**3. user_group_membership** (L37-49)
`tenant_id bigint NN` / `user_id bigint NN` / `group_id text NN` / `created_at timestamptz NN now()`
PK `(tenant_id,user_id,group_id)`;FK `tenant_id→tenants(id)`;FK `fk_user_group_membership_user_tenant (user_id,tenant_id)→users(id,tenant_id) **ON DELETE CASCADE**`;CHECK `ck_user_group_membership_group_id`: `char_length(group_id) BETWEEN 1 AND 128 AND group_id ~ '^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$'`。索引 `ix_user_group_membership_user(user_id,tenant_id)`。

**4. conversations** (L50-57)
`id bigint IDENTITY PK` / `prompt text NN` / `reply text NN` / `created_at timestamptz NN now()` / `tenant_id text NN ''`† / `user_id text NN ''`†
索引 `conversations_tenant_user_idx(tenant_id,user_id)`。無 FK/CHECK。

**5. rag_documents** (L58-62,68)
`id uuid PK` / `tenant_id text NN` / `title text NN` / `chunk_count int NN` / `created_at timestamptz NN now()` / `status text NN 'ready'`†
索引 `rag_documents_tenant_idx(tenant_id)`。

**6. rag_chunks** (L63-73)
`id uuid PK` / `document_id uuid NULL` / `tenant_id text NN` / `content text NN` / `embedding vector(1536) NN`
FK `document_id → rag_documents(id) **ON DELETE CASCADE**`。索引 `rag_chunks_tenant_idx(tenant_id)`;**HNSW** `rag_chunks_embedding_hnsw_idx USING hnsw (embedding vector_cosine_ops)`(需 pgvector ≥ 0.5;全 schema 唯一非 btree 索引)。

**7. app_config** (L75-103)
`tenant_id text NN` / `key text NN` / `value text NN` / `updated_at timestamptz NN now()`;PK `(tenant_id,key)`。
附:L85-103 `DO $app_config_tenant$` 舊 shape(單欄 key PK)就地升級 → P3 全刪,`0002` 直接建目標形狀。

**8. skill** (L110-158) — **P3 刪除**
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `name text NN` / `description text NN` / `definition text NN ''` / `required_role text NN 'USER'` / `enabled boolean NN true` / `current_revision int NN 1` / `created_by text NN ''` / `created_at,updated_at timestamptz NN now()` / `package bytea NULL`† / `kind text NN 'flow'`†(L140 加欄→L146 UPDATE 分類→L155-156 SET DEFAULT+NOT NULL) / **`simple_form jsonb NULL`†(L144)**
UNIQUE `uq_skill_tenant_name(tenant_id,name)`;索引 `ix_skill_tenant_enabled(tenant_id,enabled)`。

**9. skill_revision** (L125-158) — **P3 刪除**
`id uuid PK gen_random_uuid()` / `skill_id uuid NN` / `revision int NN` / `definition text NN` / `definition_sha256 text NN` / `created_by text NN` / `created_at timestamptz NN now()` / `package_sha256 text NULL`† / `package bytea NULL`† / `kind text NN 'flow'`†
FK `skill_id→skill(id)`;UNIQUE `uq_skill_revision(skill_id,revision)`(是 `agent_revision_skill`/`agent_run_skill` 複合 FK 的目標);索引 `ix_skill_revision_skill(skill_id)`。

**10. configuration_set** (L162-172)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `name text NN` / `is_active boolean NN false` / `values jsonb NN '{}'` / `created_by text NN ''` / `created_at,updated_at timestamptz NN now()`
UNIQUE `uq_confset_tenant_name(tenant_id,name)`;部分唯一索引 `uq_confset_active(tenant_id) WHERE is_active`。

**11. agent** (L179-198)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `slug text NN` / `name text NN` / `description text NN ''` / `enabled boolean NN true` / `draft_version bigint NN 1` / `draft_definition jsonb NN '{}'` / `draft_definition_canonical bytea NULL`(CREATE+ALTER 兩處) / `draft_definition_sha256 text NN ''` / `draft_validated_version bigint NULL` / `published_revision integer NULL` / `created_by text NN ''` / `created_at,updated_at timestamptz NN now()`
UNIQUE `uq_agent_tenant_slug(tenant_id,slug)`;索引 `ix_agent_tenant_enabled(tenant_id,enabled)`。

**12. agent_revision** (L201-224, 302-306, 326, 924-926)
`id uuid PK gen_random_uuid()` / `agent_id uuid NN` / `revision integer NN` / `status text NN 'published'` / `system_prompt text NN ''` / `execution_roles jsonb NN '[]'` / `capabilities jsonb NN '[]'` / `output_contract jsonb NN '{}'` / `audience jsonb NN '[]'` / `business_rules jsonb NN '{}'` / `allowed_tools jsonb NN '[]'` / `knowledge_sources jsonb NN '[]'` / `runtime_limits jsonb NN '{}'` / `runtime_workflow_id uuid NULL` / `runtime_workflow_revision integer NULL` / `definition_sha256 text NN ''` / `canonical_definition bytea NULL` / `created_by text NN ''` / `created_at timestamptz NN now()` / `prompt_manifest_revision integer NULL`† / `prompt_manifest_sha256 text NULL`†
UNIQUE `uq_agent_revision(agent_id,revision)`(FK 目標);索引 `ix_agent_revision_agent(agent_id)`;
FK `fk_agent_revision_runtime_workflow (runtime_workflow_id,runtime_workflow_revision) → workflow_revision(workflow_id,revision)`(建為 NOT VALID → L326 `VALIDATE` → convalidated=true)。

**13. agent_revision_skill** (L227-234, 309-319, 327-328) — **P3 刪除/重建**
`agent_id uuid NN` / `agent_revision integer NN` / `skill_id uuid NN` / `skill_revision integer NN` / `position integer NN 0` / `enabled boolean NN true`
PK `(agent_id,agent_revision,skill_id)`;FK `skill_id→skill(id)`;FK `fk_agent_revision_skill_agent_revision (agent_id,agent_revision)→agent_revision(agent_id,revision)`;FK `fk_agent_revision_skill_skill_revision (skill_id,skill_revision)→skill_revision(skill_id,revision)`(後兩者 NOT VALID→VALIDATE)。

**14. workflow** (L237-259)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `name text NN` / `kind text NN` / `enabled boolean NN true` / `draft_version bigint NN 1` / `draft_definition jsonb NN '{}'` / `draft_ui_metadata jsonb NN '{}'` / `published_revision integer NULL` / `created_at,updated_at timestamptz NN now()` / `draft_definition_canonical bytea NULL`† / `draft_ui_metadata_canonical bytea NULL`† / `draft_definition_sha256 text NN ''`† / `draft_ui_metadata_sha256 text NN ''`† / `draft_validated_version bigint NULL`† / `created_by text NN ''`† / `system_owned boolean NN false`†
唯一索引 `uq_workflow_tenant_name UNIQUE(tenant_id,name)`(是索引不是 constraint)。無 CHECK。

**15. workflow_revision** (L260-275)
`id uuid PK gen_random_uuid()` / `workflow_id uuid NN` / `revision integer NN` / `schema_version integer NN 1` / `definition jsonb NN '{}'` / `ui_metadata jsonb NN '{}'` / `definition_sha256 text NN ''` / `ui_metadata_sha256 text NN ''` / `compiler_contract_version text NN '1'` / `created_by text NN ''` / `created_at timestamptz NN now()` / `status text NN 'published'`† / `definition_canonical bytea NULL`† / `ui_metadata_canonical bytea NULL`†
FK `workflow_id→workflow(id)`;UNIQUE `uq_workflow_revision(workflow_id,revision)` ← **三個 Harness pin FK 的目標**。

**16. orchestrator** (L278-286)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `name text NN` / `description text NN ''` / `enabled boolean NN true` / `draft_version bigint NN 1` / `draft_validated_version bigint NULL` / `draft_definition jsonb NN '{}'` / `draft_definition_canonical bytea NULL` / `draft_definition_sha256 text NN ''` / `published_revision integer NULL` / `created_by text NN ''` / `created_at,updated_at timestamptz NN now()`
UNIQUE `uq_orchestrator_tenant_name(tenant_id,name)`;索引 `ix_orchestrator_tenant_enabled(tenant_id,enabled)`。

**17. orchestrator_revision** (L287-295)
`id uuid PK gen_random_uuid()` / `orchestrator_id uuid NN` / `revision integer NN` / `status text NN 'published'` / `definition jsonb NN` / `canonical_definition bytea NN` / `definition_sha256 text NN` / `workflow_id uuid NN` / `workflow_revision integer NN` / `verifier_agent_id uuid NN` / `verifier_agent_revision integer NN` / `created_by text NN ''` / `created_at timestamptz NN now()`
FK `orchestrator_id→orchestrator(id)`;UNIQUE `uq_orchestrator_revision(orchestrator_id,revision)`;索引 `ix_orchestrator_revision_orchestrator`。
⚠ **`workflow_id/workflow_revision` 與 `verifier_agent_id/verifier_agent_revision` 目前完全沒有 FK**(L298 `DO $constraints$` 只建 3 條,不含這兩組)。ledger row 17 寫 "Recreate FKs" 對此表其實是**新建**。

**18. agent_run** (L333-403, 406-407, 610-684)
`id uuid PK` / `tenant_id text NN` / `root_run_id uuid NN` / `parent_run_id uuid NULL` / `task_id text NULL` / `run_kind text NN` / `user_id text NN` / `caller_role text NN`(CREATE 為 NN;legacy 走 L626 ALTER 加欄 → L628 backfill → L633 SET NOT NULL) / `conversation_id text NULL` / `agent_id uuid NN` / `agent_revision integer NN` / `workflow_id uuid NN` / `workflow_revision integer NN` / `execution_snapshot jsonb NN` / `execution_snapshot_canonical bytea NULL` / `snapshot_sha256 text NN` / `status text NN` / `state_version bigint NN 1` / `lease_generation bigint NN 0` / `checkpoint_ref text NULL` / `checkpoint_generation bigint NN 0` / `checkpoint_version bigint NN 0` / `event_ack_cursor bigint NN 0` / `pending_input jsonb NULL` / `result jsonb NULL` / `error_code text NULL` / `error_message text NULL` / `cancel_requested_at timestamptz NULL` / `cancel_requested_by text NULL` / `lease_owner text NULL` / `lease_token_sha256 text NULL` / `lease_expires_at timestamptz NULL` / `lease_command_id uuid NULL` / `latest_event_sequence bigint NN 0` / `started_at timestamptz NULL` / `created_at timestamptz NN now()` / `deadline_at timestamptz NN`(legacy: L625 加欄 → L659 backfill `created_at + interval '60 seconds'` → L662 SET NOT NULL) / `updated_at timestamptz NN now()` / `completed_at timestamptz NULL` / `orchestrator_root_run_id uuid NULL`†
CHECK:
- `ck_agent_run_kind`: `run_kind IN ('direct-agent','orchestrator','worker','verifier')`
- `ck_agent_run_status`: `status IN ('queued','running','waiting_input','waiting_approval','completed','failed','cancelled')`(L406-407 DROP+ADD,內容相同)
- `ck_agent_run_pinned_identity_v2`: tenant_id/user_id 非空白且 ≤256、caller_role 非空白且 ≤64,且三者 `IS NOT DISTINCT FROM execution_snapshot->'caller'->>{tenant_id,user_id,role}`
- `ck_agent_run_versions`(CREATE 內)與 `ck_agent_run_fencing_v2`(L671,legacy 路徑):`state_version>=1 AND lease_generation>=0 AND checkpoint_generation>=0 AND checkpoint_generation<=lease_generation AND checkpoint_version>=0 AND event_ack_cursor>=0`。⚠ **fresh DB 有 `ck_agent_run_versions`,legacy DB 兩個都有** → 這是既存的 fresh/legacy fingerprint 分歧點,`0002` 必須二選一(建議只保留 `ck_agent_run_fencing_v2`)。
- `ck_agent_run_direct_lineage`: `run_kind <> 'direct-agent' OR (root_run_id = id AND parent_run_id IS NULL AND task_id IS NULL)`
FK: `fk_agent_run_agent_revision (agent_id,agent_revision)→agent_revision(agent_id,revision)`;`fk_agent_run_workflow_revision (workflow_id,workflow_revision)→workflow_revision(workflow_id,revision)`;`fk_agent_run_orchestrator_root (orchestrator_root_run_id)→orchestrator_run(id)`。
索引:`ix_agent_run_tenant_owner_updated(tenant_id,user_id,updated_at DESC)`;`ix_agent_run_orchestrator_root(orchestrator_root_run_id) WHERE orchestrator_root_run_id IS NOT NULL`;`ix_agent_run_status_lease(status,lease_expires_at) WHERE status IN ('queued','running')`。

**19. agent_run_approval** (L408-418)
`id uuid PK` / `run_id uuid NN` / `tenant_id text NN` / `requested_by text NN` / `required_role text NN` / `action_fingerprint text NN` / `expires_at timestamptz NN` / `self_approval_forbidden boolean NN true` / `status text NN` / `checkpoint_ref text NN` / `checkpoint_version bigint NN` / `decision text NULL` / `decided_by text NULL` / `decided_at timestamptz NULL` / `reason text NULL` / `created_at timestamptz NN now()`
FK `run_id→agent_run(id)`;CHECK(匿名) `status IN ('pending','approved','rejected','consumed','expired','cancelled')`、`action_fingerprint ~ '^[0-9a-f]{64}$'`、`required_role ~ '^[A-Z][A-Z0-9_]{0,63}$'`;索引 `ix_agent_run_approval_pending(run_id,status,expires_at)`。

**20. agent_run_approval_decision** (L419-422)
`approval_id uuid NN` / `idempotency_key_sha256 text NN` / `decision text NN CHECK IN('approved','rejected')` / `approver_id text NN` / `reason text NULL` / `created_at timestamptz NN now()`;PK `(approval_id,idempotency_key_sha256)`;FK `approval_id→agent_run_approval(id)`。

**21. agent_run_write_effect** (L425-430, 440)
`id uuid PK gen_random_uuid()` / `approval_id uuid NN` / `run_id uuid NN` / `action_fingerprint text NN` / `status text NN CHECK IN('reserved','completed','failed')` / `created_at timestamptz NN now()` / `completed_at timestamptz NULL` / `evidence jsonb NULL`†
FK `approval_id→agent_run_approval(id)`、`run_id→agent_run(id)`;UNIQUE `(run_id,action_fingerprint)`。

**22. agent_run_approval_execute** (L431-439)
`approval_id uuid PK` / `run_id uuid NN` / `tenant_id text NN` / `approver_id text NN` / `status text NN` / `claim_token_sha256 text NULL` / `claim_expires_at timestamptz NULL` / `attempts integer NN 0` / `created_at timestamptz NN now()` / `completed_at timestamptz NULL`
FK `approval_id→agent_run_approval(id)`(同時是 PK)、`run_id→agent_run(id)`;CHECK `agent_run_approval_execute_status_check`(**具名**,L438-439 DROP+ADD): `status IN ('queued','claimed','completed','dead_letter')`;索引 `ix_agent_run_approval_execute_recovery(status,claim_expires_at,created_at) WHERE status IN ('queued','claimed')`。

**23. agent_run_write_outbox** (L441-445)
`effect_id uuid PK` / `run_id uuid NN` / `tenant_id text NN` / `payload jsonb NN` / `status text NN CHECK IN('pending','delivered')` / `created_at timestamptz NN now()` / `delivered_at timestamptz NULL`
FK `effect_id→agent_run_write_effect(id)`、`run_id→agent_run(id)`;索引 `ix_agent_run_write_outbox_pending(status,created_at) WHERE status='pending'`。

**24. orchestrator_run** (L448-471, 564-566)
`id uuid PK` / `tenant_id text NN` / `user_id text NN` / `caller_role text NN` / `orchestrator_id uuid NN` / `orchestrator_revision integer NN` / `conversation_id text NN` / `workflow_id uuid NN` / `workflow_revision integer NN` / `execution_snapshot jsonb NN` / `execution_snapshot_canonical bytea NN` / `snapshot_sha256 text NN` / `workflow_dispatch_snapshot jsonb NULL`† / `workflow_dispatch_snapshot_canonical bytea NULL`† / `workflow_dispatch_snapshot_sha256 text NULL`† / `request_sha256 text NN` / `idempotency_key_sha256 text NN` / `status text NN` / `state_version bigint NN 1` / `cancel_requested_at timestamptz NULL` / `result jsonb NULL`† / `error_code text NULL`† / `error_message text NULL`† / `completed_at timestamptz NULL`† / `checkpoint_ref text NULL`† / `checkpoint_version bigint NN 0`† / `deadline_at timestamptz NN` / `created_at,updated_at timestamptz NN now()`
UNIQUE `(tenant_id,user_id,idempotency_key_sha256)`;CHECK `orchestrator_run_status_check`(**具名**): `status IN ('queued','running','waiting_input','completed','failed','cancelled','timed_out')`;部分唯一索引 `uq_orchestrator_run_active_conversation(tenant_id,user_id,conversation_id,orchestrator_id) WHERE status IN ('queued','running','waiting_input')`。
⚠ **`orchestrator_id/orchestrator_revision`、`workflow_id/workflow_revision` 亦無 FK**。

**25. tenant_runtime_binding** (L473-481)
`tenant_id text PK` / `enabled boolean NN false` / `default_orchestrator_id uuid NULL` / `default_orchestrator_revision integer NULL` / `canary_user_ids jsonb NN '[]'::jsonb` / `updated_at timestamptz NN now()`
CHECK(匿名 ×2): `(NOT enabled) OR (default_orchestrator_id IS NOT NULL AND default_orchestrator_revision >= 1)`;`jsonb_typeof(canary_user_ids)='array'`。無 FK。

**26. operations_regression_result** (L485-491)
`id uuid PK` / `tenant_id text NN` / `suite text NN` / `passed boolean NN` / `evidence_ref text NN` / `recorded_by text NN` / `recorded_at timestamptz NN now()`
CHECK ×3: `char_length(suite) BETWEEN 1 AND 128`、`evidence_ref BETWEEN 1 AND 256`、`recorded_by BETWEEN 1 AND 256`;索引 `ix_operations_regression_result_tenant_latest(tenant_id,recorded_at DESC,id DESC)`。

**27. operations_regression_override** (L492-498)
`tenant_id text NN` / `idempotency_key_sha256 text NN` / `regression_id uuid NN` / `reason text NN` / `actor_id text NN` / `created_at timestamptz NN now()`
PK `(tenant_id,idempotency_key_sha256)`;FK `regression_id→operations_regression_result(id)`;CHECK: `idempotency_key_sha256 ~ '^[0-9a-f]{64}$'`、`char_length(reason) BETWEEN 8 AND 1000`、`char_length(actor_id) BETWEEN 1 AND 256`;索引 `ix_operations_regression_override_gate(tenant_id,regression_id)`。

**28. operations_release_audit** (L499-506)
`id bigint IDENTITY PK` / `tenant_id text NN` / `kind text NN` / `outcome text NN` / `actor_id text NN` / `detail text NN` / `occurred_at timestamptz NN now()`
CHECK: `kind IN ('regression','regression_override','rollout','rollback')`、`outcome` 1..64、`actor_id` 1..256、`detail` 1..256;索引 `ix_operations_release_audit_tenant_time(tenant_id,occurred_at DESC,id DESC)`。

**29. operations_execution_metric** (L507-515) — **P3 改欄**
`tenant_id text NN` / `run_id uuid NN` / `event_id uuid NN` / `kind text NN CHECK IN('model','tool','node')` / `node_id text NULL` / `tool_name text NULL` / **`skill_name text NULL`†** / **`skill_revision integer NULL`†** / `agent_id text NULL`† / `agent_revision integer NULL`† / `usage_units bigint NULL` / `cost_units numeric(18,6) NULL` / `latency_ms bigint NULL` / `observed_at timestamptz NN now()`
PK `(tenant_id,run_id,event_id)`;FK `run_id→agent_run(id)`;CHECK ×3 非負;索引 `ix_operations_execution_metric_tenant_time(tenant_id,observed_at DESC)`。

**30. operations_run_evidence** (L524-563) — **P3 改欄**
`tenant_id text NN` / `run_id uuid NN` / `event_id uuid NN` / `root_run_id uuid NULL` / `child_id uuid NULL` / `occurred_at timestamptz NN now()` / `kind text NN CHECK IN('model','tool','node')` / `outcome text NN CHECK IN('success','failure','unknown')` / `error_class text NULL` / `snapshot_sha256 text NULL` / `agent_id text NULL` / `agent_revision integer NULL` / `orchestrator_revision integer NULL` / **`skill_name text NULL`** / **`skill_revision integer NULL`** / `prompt_manifest_sha256 text NULL` / `context_revision integer NULL` / `role_view text NULL` / `policy_revision integer NULL` / `model_provider/model_deployment/model_id/model_fingerprint/model_settings_hash text NULL` / `tool_name text NULL` / `tool_revision integer NULL` / `node_id text NULL` / `trace_id text NULL` / `span_id text NULL` / `reserved_budget_units bigint NULL` / `usage_units bigint NULL` / `cost_units numeric(18,6) NULL` / `latency_ms bigint NULL` / `observation_quality text NN CHECK IN('measured','estimated','unknown')` / `verifier_verdict text NULL` / `case_verdict text NULL` / `redaction_note text NULL`
PK `(tenant_id,run_id,event_id)`;FK `run_id→agent_run(id)`;CHECK ×17(見 L539-556),其中 `operations_run_evidence_error_class_check`(**具名**,L561-563 DROP+ADD): `error_class IS NULL OR error_class ~ '^[A-Za-z0-9_.:+-]{1,200}$'`;索引 `ix_operations_run_evidence_tenant_time(tenant_id,occurred_at DESC)`。

**31. orchestrator_run_event** (L567-570)
`run_id uuid NN` / `sequence bigint NN` / `event_type text NN` / `snapshot_sha256 text NN` / `payload jsonb NN '{}'` / `created_at timestamptz NN now()`;PK `(run_id,sequence)`;FK `run_id→orchestrator_run(id)`。

**32. orchestrator_run_command** (L571-597)
`id uuid PK gen_random_uuid()` / `run_id uuid NN` / `command_type text NN` / `dispatch_attempt integer NN 0` / `claim_owner text NULL`† / `claim_token_sha256 text NULL` / `claim_expires_at timestamptz NULL` / `dispatch_completed_at timestamptz NULL`† / `completed_at timestamptz NULL` / `lease_generation bigint NN 0` / `idempotency_key_sha256 text NULL`† / `command_input jsonb NULL`† / `command_input_sha256 text NULL`† / `created_at timestamptz NN now()`
FK `run_id→orchestrator_run(id)`;CHECK `orchestrator_run_command_command_type_check`(**具名**,最終值) `command_type IN ('start','resume','cancel')`;
⚠ **原 CREATE 的 `UNIQUE(run_id,command_type)` 於 L587 被 DROP**,改以 3 個部分唯一索引取代:`uq_orchestrator_run_command_start(run_id) WHERE command_type='start'`、`uq_orchestrator_run_command_cancel(run_id) WHERE command_type='cancel'`、`uq_orchestrator_run_command_resume_idempotency(run_id,idempotency_key_sha256) WHERE command_type='resume' AND idempotency_key_sha256 IS NOT NULL`;另 `ix_orchestrator_run_command_reclaim(claim_expires_at,created_at) WHERE completed_at IS NULL`、`ix_orchestrator_run_command_idempotency_lookup(idempotency_key_sha256,run_id) WHERE idempotency_key_sha256 IS NOT NULL`。
⚠ **fresh DB 亦會執行 DROP** → 最終形狀一致(無 `UNIQUE(run_id,command_type)`)。`0002` 必須直接寫最終形狀。

**33. orchestrator_run_child** (L598-609)
`id uuid PK` / `orchestrator_root_run_id uuid NN` / `task_id text NN` / `attempt integer NN CHECK(attempt>=1)` / `run_kind text NN CHECK IN('worker','verifier')` / `agent_id uuid NN` / `agent_revision integer NN` / `workflow_id uuid NN` / `workflow_revision integer NN` / `agent_snapshot_sha256 text NN` / `agent_run_id uuid NULL`† / `command_id uuid NULL`† / `task_envelope jsonb NN '{}'`† / `dispatch_artifact jsonb NN` / `status text NN CHECK IN('queued','running','completed','failed','cancelled')` / `created_at,updated_at timestamptz NN now()`
FK `orchestrator_root_run_id→orchestrator_run(id)`;UNIQUE `(orchestrator_root_run_id,task_id,attempt)`;部分唯一索引 `uq_orchestrator_run_child_agent_run(agent_run_id) WHERE agent_run_id IS NOT NULL`;索引 `ix_orchestrator_run_child_active(orchestrator_root_run_id,status)`。

**34. agent_run_skill** (L686-698) — **P3 刪除/typed 化**
`run_id uuid NN` / `position integer NN` / `skill_id uuid NN` / `skill_revision integer NN` / `skill_name text NN` / **`kind text NN`** / `definition_sha256 text NN` / `package_sha256 text NULL`
PK `(run_id,skill_id)`;UNIQUE `uq_agent_run_skill_position(run_id,position)`;FK `run_id→agent_run(id)`;FK `fk_agent_run_skill_revision (skill_id,skill_revision)→skill_revision(skill_id,revision)`。

**35. agent_run_event** (L700-733)
`run_id uuid NN` / `sequence bigint NN` / `event_id uuid NN` / `event_type text NN` / `node_id text NULL` / `lease_generation bigint NULL`† / `event_cursor bigint NULL`† / `snapshot_sha256 text NN` / `payload jsonb NN '{}'` / `created_at timestamptz NN now()`
PK `(run_id,sequence)`;UNIQUE `uq_agent_run_event_id(run_id,event_id)`;FK `run_id→agent_run(id)`;CHECK `ck_agent_run_event_fencing`: `(lease_generation IS NULL AND event_cursor IS NULL) OR (lease_generation>=1 AND event_cursor>=0)`(NOT VALID→VALIDATE);部分唯一索引 `uq_agent_run_event_cursor(run_id,event_cursor) WHERE event_cursor IS NOT NULL`。

**36. agent_run_command** (L735-798 + `MigrateAgentRunCommandInputHashesAsync` L961-1021)
`id uuid PK gen_random_uuid()` / `command_sequence bigint GENERATED ALWAYS AS IDENTITY`(非 PK,獨立 sequence) / `tenant_id text NN` / `user_id text NN` / `run_id uuid NN` / `command_type text NN` / `idempotency_key_sha256 text NN` / `request_sha256 text NN` / `command_input jsonb NN '{}'` / **`command_input_sha256 char(64) NN`**(唯一使用 `char(n)` 的欄位之一) / `dispatch_claim_owner text NULL` / `dispatch_claim_token_sha256 text NULL` / `dispatch_claim_expires_at timestamptz NULL` / `dispatch_attempts integer NN 0` / `dispatch_completed_at timestamptz NULL` / `execution_recovery_lease_token_sha256 text NULL` / `last_dispatch_at timestamptz NULL` / `created_at timestamptz NN now()`
FK `run_id→agent_run(id)`;UNIQUE `uq_agent_run_command_idempotency(tenant_id,user_id,command_type,idempotency_key_sha256)`;CHECK `ck_agent_run_command_type`: `command_type IN ('start','resume','cancel','deadline_cleanup')`;索引:`uq_agent_run_deadline_cleanup UNIQUE(run_id) WHERE command_type='deadline_cleanup'`、`ix_agent_run_command_recovery(dispatch_claim_expires_at,created_at) WHERE dispatch_completed_at IS NULL`、`ix_agent_run_command_run_sequence(run_id,command_sequence DESC)`。
⚠ `command_input_sha256` 的 NOT NULL 由 C# migration(L1015-1019)施加,不在 Ddl 字串中 → `0002` 必須直接 `NOT NULL`。

**37. context_policy** (L800-807)
`id uuid PK` / `tenant_id text NN` / `name text NN` / `is_active boolean NN false` / `values jsonb NN '{}'` / `created_by text NN 'system'` / `created_at,updated_at timestamptz NN now()`
UNIQUE `uq_context_policy_tenant_name(tenant_id,name)`;部分唯一索引 `uq_context_policy_active(tenant_id) WHERE is_active`。

**38. source_catalog** (L808-818)
`source_id text PK` / `tenant_id text NULL`(顯式 `NULL`) / `source_type text NN` / `evidence_type text NN 'document'`† / `authority_class text NN` / `date_coverage jsonb NULL` / `freshness_sla jsonb NULL` / `adapter_id text NN` / `acl_policy_id text NULL` / `enabled boolean NN true` / `required boolean NN true`† / `timeout_seconds integer NN 15`† / `minimum_deadline_seconds integer NN 2`†。無 FK/CHECK/額外索引。

**39. metric_definition** (L819-822)
`metric_id text NN` / `definition_version text NN` / `pack_id text NULL` / `tenant_id text NULL` / `formula jsonb NULL` / `unit text NULL` / `aggregation text NULL` / `grain text NULL` / `dimensions jsonb NULL`;PK `(metric_id,definition_version)`。無 FK/CHECK/索引。**目前無任何 seed、無 repository 寫入路徑**。

**40. context_revision** (L823-833)
`context_id uuid NN` / `revision integer NN` / `tenant_id text NN` / `root_run_id uuid NULL` / `status text NN` / `canonical bytea NN` / `sha256 char(64) NN` / `definition jsonb NN` / `as_of timestamptz NN` / `readiness numeric NN`(無精度) / `policy_id uuid NN` / `selected_source_id text NULL`† / `adapter_id text NULL`† / `unmet_requirements jsonb NN '[]'` / `created_at timestamptz NN now()` / `expires_at timestamptz NULL`
PK `(context_id,revision)`;FK `root_run_id→orchestrator_run(id)`、`policy_id→context_policy(id)`;索引 `ix_context_revision_tenant_root(tenant_id,root_run_id)`。

**41. context_evidence** (L834-840)
`evidence_id uuid PK` / `context_id uuid NN` / `revision integer NN` / `tenant_id text NN` / `evidence_type text NN` / `source_id text NN` / `snapshot_id text NN` / `content_ref text NN` / `content_hash char(64) NN` / `scope jsonb NULL` / `observations jsonb NULL` / `acl_decision_id text NULL` / `observed_at timestamptz NN` / `lineage jsonb NULL`
FK `fk_context_evidence_revision (context_id,revision)→context_revision(context_id,revision)`;UNIQUE `uq_context_evidence_snapshot_ref(context_id,revision,snapshot_id,content_ref)`。

**42. context_view** (L841-845)
`view_id uuid PK` / `context_id uuid NN` / `revision integer NN` / `tenant_id text NN` / `view_type text NN` / `canonical bytea NN` / `sha256 char(64) NN` / `definition jsonb NN`
FK `fk_context_view_revision (context_id,revision)→context_revision(...)`;UNIQUE `uq_context_view_type(context_id,revision,view_type)`。

**43. context_request** (L848-856)
`id uuid PK` / `orchestrator_root_run_id uuid NN` / `orchestrator_child_id uuid NN` / `context_id uuid NN` / `task_id text NN` / `role text NN CHECK IN('worker','verifier')` / `base_context_ref jsonb NULL` / `current_context_ref jsonb NULL` / `version bigint NN 1` / `created_at,updated_at timestamptz NN now()`
FK `orchestrator_root_run_id→orchestrator_run(id)`、`orchestrator_child_id→orchestrator_run_child(id)`;UNIQUE `(orchestrator_child_id)`;索引 `ix_context_request_root_child(orchestrator_root_run_id,orchestrator_child_id)`。

**44. context_delta** (L857-862)
`id uuid PK` / `context_request_id uuid NN` / `version bigint NN` / `context_id uuid NN` / `revision integer NN` / `created_at timestamptz NN now()`
FK `context_request_id→context_request(id)`;UNIQUE `(context_request_id,version)`、UNIQUE `(context_id,revision)`。

**45. eval_suite** (L868-872)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `suite_id text NN` / `current_revision integer NN 0` / `created_at,updated_at timestamptz NN now()`;UNIQUE `uq_eval_suite_tenant_suite(tenant_id,suite_id)`。

**46. eval_suite_revision** (L873-879)
`id uuid PK gen_random_uuid()` / `suite_id uuid NN` / `revision integer NN` / `cases_canonical text NN` / `cases_sha256 text NN` / `case_count integer NN` / `created_by text NN ''` / `created_at timestamptz NN now()`
FK `suite_id→eval_suite(id)`;UNIQUE `uq_eval_suite_revision(suite_id,revision)`;索引 `ix_eval_suite_revision_suite(suite_id)`。

**47. eval_run** (L887-896) — **P3 改欄**
`id uuid PK` / `tenant_id text NN` / `suite_id text NN`(text,非 FK) / `suite_revision integer NN` / **`candidate_kind text NN CHECK IN('skill','agent')`** / **`candidate_ref jsonb NN`** / **`candidate_pins jsonb NULL`** / **`candidate_identity_sha256 text NN`** / `required_case_ids text[] NN '{}'` / `freshness_seconds bigint NULL` / `runner_version text NN` / `started_at timestamptz NN` / `completed_at timestamptz NN` / `actor_id text NN` / `idempotency_key_sha256 text NN` / `created_at timestamptz NN now()`
UNIQUE `uq_eval_run_idempotency(tenant_id,idempotency_key_sha256)`;索引 `ix_eval_run_tenant_suite(tenant_id,suite_id,suite_revision)`。無 FK。

**48. eval_case_result** (L897-901)
`run_id uuid NN` / `case_id text NN` / `canonical_identity text NULL` / `verdict text NN CHECK IN('PASS','FAIL','ERROR')` / `metrics jsonb NULL` / `failure_reason text NULL`;PK `(run_id,case_id)`;FK `run_id→eval_run(id)`。

**49. prompt_component_revision** (L906-911)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `kind text NN CHECK IN('governance_frame','guard','routing','summary','persona','memory_policy')` / `revision integer NN` / `content text NN` / `content_sha256 text NN` / `created_by text NN ''` / `created_at timestamptz NN now()`
UNIQUE `uq_prompt_component_revision(tenant_id,kind,revision)`。

**50. prompt_manifest_revision** (L915-921)
`id uuid PK gen_random_uuid()` / `tenant_id text NN` / `revision integer NN` / `manifest_canonical text NN` / `manifest_sha256 text NN` / `created_by text NN ''` / `created_at timestamptz NN now()`
UNIQUE `uq_prompt_manifest_revision(tenant_id,revision)`;索引 `ix_prompt_manifest_revision_sha(tenant_id,manifest_sha256)`。

#### 1.2 給 0002/0003 作者的 5 個具體陷阱

1. **匿名 CHECK 名稱**:約 40 條 CHECK 是系統自動命名(`<table>_<column>_check`、`<table>_check`)。DbBootstrap 有 5 處**依名字**做 DROP+ADD(`operations_run_evidence_error_class_check`、`orchestrator_run_status_check`、`agent_run_approval_execute_status_check`、`orchestrator_run_command_command_type_check`、`ck_agent_run_status`)。多條匿名 CHECK 在同一表會拿到 `_check1`/`_check2` 後綴,順序敏感 → `0002/0003` **必須全部顯式命名**,否則 fingerprint 不可重現。
2. **`agent_run` 有兩條語意相同的 fencing CHECK**(`ck_agent_run_versions` fresh-only、`ck_agent_run_fencing_v2` legacy-only,fresh DB 兩條都會有因為 CREATE 內含前者、DO 區塊補後者)→ 這是既存的 fresh/legacy 差異,`0002` 必須明確二選一。
3. **ALTER 後補欄的 attnum 順序**在 legacy 與 fresh 不同(如 `skill.kind`、`workflow.system_owned`、`orchestrator_run.result` 等 30+ 欄)。03-design §1.3 的 fingerprint 排序鍵是 `(schema, object_type, name)`,若欄位投影也按 name 排序即安全;**若按 attnum 排序則 0001 重置後與 fresh 不收斂** → 需在 fingerprint 腳本明確固定為按欄名排序。
4. **`orchestrator_run_command` 的 `UNIQUE(run_id,command_type)` 在 fresh 路徑也被 DROP**(L587)→ `0002` 直接寫 3 個部分唯一索引,不要建那條 constraint。
5. **NOT VALID / VALIDATE**:5 條 constraint 走 `NOT VALID → VALIDATE`,最終 `convalidated=true`。`0003` 直接建 VALID 即可,但 fingerprint 必須把 `convalidated` 納入投影並斷言全部為 true。

---

### 2. P3 目標差異表

#### 2.1 刪除(4 表 + 其 kind/mixed 欄位)

| 物件 | 依據 | 連帶必須同交易處理 |
|---|---|---|
| `skill`(含 `kind`、`package`、`simple_form`、`definition`) | ledger §1 row 12/14/25;02-spec §2 目標無此表 | `ix_skill_tenant_enabled`、`uq_skill_tenant_name` |
| `skill_revision`(含 `kind`、`package`、`package_sha256`) | ledger row 13 | 3 條指向它的 FK(`agent_revision_skill` ×1、`agent_run_skill` ×1)必須同交易 drop |
| `agent_revision_skill`(含 `enabled`、`position`) | ledger row 15 | `fk_agent_revision_skill_agent_revision`、`fk_agent_revision_skill_skill_revision` |
| `agent_run_skill`(含 `kind`、`package_sha256`) | ledger row 16 | `fk_agent_run_skill_revision`、`uq_agent_run_skill_position` |
| `skill.simple_form` 欄位 | ledger row 25 → **02-spec §2.2 Decided 2026-08-03 已裁決**:移入 `business_workflow`,不進 revision hash,不上 `agent_skill` | Platform `SkillDtos.cs:23,48,62` 的 `simpleForm` 欄位同期處理 |
| `DbBootstrap` 全部 inline migration(`app_config` DO 區塊、skill kind backfill、`MigrateSkillPackagesAndNamesAsync`、`MigrateAgentRunCommandInputHashesAsync`) | ledger row 9/19 | `0002` 直接寫終態,不搬遷任何 legacy 形狀 |

#### 2.2 新建

| 目標表 | 02-spec 依據 | 形狀(spec 明文 + 推導) |
|---|---|---|
| `agent_skill` | §2.1 | `id uuid PK` / `tenant_id text NN` / `name text NN` / `description text NN` / `required_role text NN` / `enabled boolean NN` / `current_revision integer NN` / `created_by text NN` / `created_at,updated_at timestamptz NN`。UNIQUE(tenant_id,name);CHECK: canonical name、required_role 允許值、`current_revision >= 0`。⚠ **spec 無 `definition` 欄**(今日 `skill.definition` 消失)、無 `kind`、無 `package`(package 只在 revision) |
| `agent_skill_revision` | §2.1 | `id uuid PK` / `agent_skill_id uuid NN FK→agent_skill(id) **ON DELETE CASCADE**` / `revision integer NN CHECK(>=1)` / `package bytea` / `package_sha256 text` / **`instruction_sha256 text`(全新欄,今日不存在)** / `created_by text` / `created_at timestamptz`。UNIQUE(agent_skill_id,revision) ← pin/link FK 目標 |
| `business_workflow` | §2.2 | 同 `agent_skill` 欄位集,**加 `simple_form jsonb NULL`**;無 `package`、無 `kind` |
| `business_workflow_revision` | §2.2 | `id uuid PK` / `business_workflow_id uuid NN FK→business_workflow(id) **ON DELETE CASCADE**` / `revision integer NN` / `definition text NN` / `definition_sha256 text NN` / `created_by` / `created_at`。UNIQUE(business_workflow_id,revision) |
| Agent↔Skill pin/link 表(**spec 未命名**;建議 `agent_revision_agent_skill`) | ledger row 15 "Rebuild as an Agent-Skill-only pin/link with FK to `agent_skill_revision`" | `agent_id uuid` / `agent_revision integer` / `agent_skill_id uuid` / `agent_skill_revision integer` / `position integer NN` / `enabled boolean NN`;PK(agent_id,agent_revision,agent_skill_id);FK→`agent_revision(agent_id,revision)`、FK→`agent_skill_revision(agent_skill_id,revision)` |
| typed run snapshot 關聯(**spec 未命名**;建議 `agent_run_agent_skill` + `agent_run_business_workflow`) | ledger row 16 + 02-spec §2.3 Decided:「任一 execution snapshot 可同時 pin 兩種 artifact,使用獨立強型別 `agentSkills`/`businessWorkflows` 集合,非 kind 判別聯集」 | 兩張表各自 `run_id/position/{artifact}_id/{artifact}_revision/name/…_sha256`;各自 PK(run_id, artifact_id)+UNIQUE(run_id,position)。**兩表的 position 是否共用序號需裁決** |

目標表數:50 − 4 + 6 = **52 表**(若 run pin 只做一張則 51)。

#### 2.3 改欄(表保留、欄位/約束變更)

| 表 | 變更 | 依據 |
|---|---|---|
| `eval_run` | 刪 `candidate_kind text CHECK IN('skill','agent')` 判別聯集;`candidate_ref`/`candidate_pins`/`candidate_identity_sha256` 拆為顯式 candidate type + typed ref | ledger row 24(`DbBootstrap.cs:889-890`);同步 `EvalController`/`EvalRepository`/`InMemoryEvalRepository`/`EvalDtos.cs:99-124`;Workflow 側 `evals/models.py:33-43` 對應項在 §3 |
| `operations_execution_metric` | `skill_name`/`skill_revision` → typed(`agent_skill_name/_revision` + `business_workflow_name/_revision`) | ledger row 23(`DbBootstrap.cs:509,515`) |
| `operations_run_evidence` | 同上(`DbBootstrap.cs:531`) | ledger row 23 |
| `agent_revision` | `runtime_workflow_id/_revision` **FK 重建**至 `workflow_revision(workflow_id,revision)`;絕不改指 `business_workflow_revision` | ledger row 17;02-spec §2.3 |
| `agent_run` | `workflow_id/workflow_revision` FK 重建至 `workflow_revision` | ledger row 17 |
| `orchestrator_revision` | `workflow_id/workflow_revision` → `workflow_revision`。⚠ **今日無此 FK,是新增而非重建**;`verifier_agent_id/_revision` → `agent_revision(agent_id,revision)` 亦缺,ledger 未提及,需裁決是否補 | ledger row 17 + 本次盤點發現 |
| `orchestrator_run` | `orchestrator_id/orchestrator_revision`、`workflow_id/workflow_revision` 同樣**無 FK**;ledger 未涵蓋,需裁決 | 本次盤點發現 |

---

### 3. 0001 分類 allowlist 終稿

#### 3.1 relkind='r' — 54 項

**50 應用表**(05-ledger §1.1 逐字核對,本次讀檔完全吻合):
`tenants, users, user_group_membership, conversations, rag_documents, rag_chunks, app_config, skill, skill_revision, configuration_set, agent, agent_revision, agent_revision_skill, workflow, workflow_revision, orchestrator, orchestrator_revision, agent_run, agent_run_approval, agent_run_approval_decision, agent_run_write_effect, agent_run_approval_execute, agent_run_write_outbox, orchestrator_run, tenant_runtime_binding, operations_regression_result, operations_regression_override, operations_release_audit, operations_execution_metric, operations_run_evidence, orchestrator_run_event, orchestrator_run_command, orchestrator_run_child, agent_run_skill, agent_run_event, agent_run_command, context_policy, source_catalog, metric_definition, context_revision, context_evidence, context_view, context_request, context_delta, eval_suite, eval_suite_revision, eval_run, eval_case_result, prompt_component_revision, prompt_manifest_revision`

**4 LangGraph checkpointer 表**(Decision 2026-08-03, ledger §1.1):
`checkpoints, checkpoint_blobs, checkpoint_writes, checkpoint_migrations` — 由 workflow `AsyncPostgresSaver.setup()` 建立,**可選存在**(僅曾啟用 D3/D5 的機器才有)。分類時必須「存在則允許、不存在也允許」,絕不可當必要 marker。

#### 3.2 relkind='S' — 5 項(本次新增發現,ledger §1.1 未列)

`tenants_id_seq, users_id_seq, conversations_id_seq, operations_release_audit_id_seq, agent_run_command_command_sequence_seq`
另:checkpointer 表若含 identity/serial 欄則有額外 sequence(需在目標機器實測確認;LangGraph 官方 schema 使用複合文字 PK,預期為 0)。
**建議實作**:extras 掃描對 `relkind='S'` 額外排除 `pg_depend deptype='a'`(欄位自動擁有的 sequence),即可不必列舉這 5 個名字,也自動涵蓋 checkpointer 的任何 sequence。

#### 3.3 relkind IN ('v','m') — 預期 0 項(空 allowlist,任何一筆即 abort)

#### 3.4 EXTENSION allowlist

`vector`(唯一)。所有 `pg_depend deptype='e'` 連到 `vector` 的 type/operator/opclass/cast/function(`vector`、`halfvec`、`sparsevec`、`<->`、`<=>`、`<#>`、`vector_cosine_ops`、`hnsw`/`ivfflat` access method 等)一律排除於表/視圖/sequence allowlist 比對之外(03-design §1.3 明文)。
若目標庫另有 `plpgsql`(PostgreSQL 預設安裝)→ 必須一併加入 extension allowlist,否則永遠判 unknown。**這點三份文件都未提及,是實作必踩的坑。**

#### 3.5 分類 marker 判定

| 分類 | 判定條件 |
|---|---|
| `empty` | `public` 內非 extension-owned 的 `relkind IN ('r','v','m','S')` 計數 = 0 |
| `known legacy SpringAITest` | **`to_regclass('public.skill') IS NOT NULL`**(最強 legacy marker:自 DbBootstrap 第一版即存在,且目標 schema 明確刪除)AND `to_regclass('public.agent_skill') IS NULL` AND(`springaitest_meta.schema_migration` 不存在 或 無任何 completed row)AND 實際物件集合 ⊆ §3.1–3.4 allowlist。次要佐證 marker(不單獨判定):`skill_revision`、`agent_revision_skill`、`agent_run_skill` 三者同時存在 |
| `known current target` | **`to_regclass('public.agent_skill') IS NOT NULL` AND `to_regclass('public.business_workflow') IS NOT NULL`**(current marker,僅 `0002` 產生)AND `to_regclass('public.skill') IS NULL` AND `springaitest_meta.schema_migration` 含連續 `0001,0002,0003` 且 checksum 全部相符 AND 全部 `0003` postconditions 通過 |
| `unknown nonempty` | 其餘一切,含:allowlist 外的表/視圖/sequence;`skill` 與 `agent_skill` **同時存在**(半套遷移);ledger 有 row 但 `skill` 仍在(ledger/schema 不一致,fail closed 且**永不自動修復**) |

註:允許 legacy 分類「部分表缺席」(舊版 backend 建立的 DB 沒有後期新增的表),但**不允許 allowlist 外的多餘表**。

---

### 4. 0003 postconditions 草案

#### 4.1 存在斷言(每張目標表)

52 張目標表逐一 `to_regclass('public.<t>') IS NOT NULL`。分組:

- 身分/租戶(4):`tenants, users, user_group_membership, conversations`
- RAG/設定(4):`rag_documents, rag_chunks, app_config, configuration_set`
- **新 artifact(4)**:`agent_skill, agent_skill_revision, business_workflow, business_workflow_revision`
- Agent/Harness(6):`agent, agent_revision, agent_revision_agent_skill, workflow, workflow_revision, orchestrator, orchestrator_revision`(7)
- D3 run(5):`agent_run, agent_run_event, agent_run_command, agent_run_agent_skill, agent_run_business_workflow`
- D7 approval/effect(5):`agent_run_approval, agent_run_approval_decision, agent_run_approval_execute, agent_run_write_effect, agent_run_write_outbox`
- D5 root(5):`orchestrator_run, orchestrator_run_event, orchestrator_run_command, orchestrator_run_child, tenant_runtime_binding`
- Operations(5):`operations_regression_result, operations_regression_override, operations_release_audit, operations_execution_metric, operations_run_evidence`
- Context(8):`context_policy, source_catalog, metric_definition, context_revision, context_evidence, context_view, context_request, context_delta`
- Eval/Prompt(6):`eval_suite, eval_suite_revision, eval_run, eval_case_result, prompt_component_revision, prompt_manifest_revision`

#### 4.2 關鍵 FK 斷言(`pg_constraint contype='f'` + `confdeltype` + `convalidated`)

必須逐條斷言名稱、來源欄、目標欄、`confdeltype`、`convalidated=true`:

| 名稱(建議) | 定義 | ON DELETE |
|---|---|---|
| `fk_agent_skill_revision_skill` | `agent_skill_revision(agent_skill_id)→agent_skill(id)` | **CASCADE**(02-spec §2.1) |
| `fk_business_workflow_revision_workflow` | `business_workflow_revision(business_workflow_id)→business_workflow(id)` | **CASCADE**(§2.2) |
| `fk_agent_revision_agent_skill_agent_revision` | `(agent_id,agent_revision)→agent_revision(agent_id,revision)` | NO ACTION |
| `fk_agent_revision_agent_skill_skill_revision` | `(agent_skill_id,agent_skill_revision)→agent_skill_revision(agent_skill_id,revision)` | NO ACTION |
| `fk_agent_revision_runtime_workflow` | `agent_revision(runtime_workflow_id,runtime_workflow_revision)→workflow_revision(workflow_id,revision)` | NO ACTION |
| `fk_agent_run_workflow_revision` | `agent_run(workflow_id,workflow_revision)→workflow_revision(...)` | NO ACTION |
| `fk_agent_run_agent_revision` | `agent_run(agent_id,agent_revision)→agent_revision(...)` | NO ACTION |
| `fk_orchestrator_revision_workflow` | `orchestrator_revision(workflow_id,workflow_revision)→workflow_revision(...)` | NO ACTION(**新增**) |
| `fk_agent_run_orchestrator_root` | `agent_run(orchestrator_root_run_id)→orchestrator_run(id)` | NO ACTION |
| `fk_user_group_membership_user_tenant` | `(user_id,tenant_id)→users(id,tenant_id)` | **CASCADE** |
| `fk_rag_chunks_document` | `rag_chunks(document_id)→rag_documents(id)` | **CASCADE** |

**額外斷言**:`SELECT count(*) FROM pg_constraint WHERE contype='f' AND confdeltype='c'` = 4(僅上表 4 條 CASCADE)。`SELECT count(*) FROM pg_constraint WHERE NOT convalidated` = 0。

#### 4.3 UNIQUE 斷言

`uq_agent_skill_tenant_name(tenant_id,name)`、`uq_agent_skill_revision(agent_skill_id,revision)`、`uq_business_workflow_tenant_name`、`uq_business_workflow_revision`、`uq_agent_revision(agent_id,revision)`、`uq_workflow_revision(workflow_id,revision)`、`uq_orchestrator_revision`、`ux_users_id_tenant`、`uq_eval_run_idempotency`、`uq_agent_run_command_idempotency`、`uq_agent_run_event_id`、`uq_prompt_component_revision`、`uq_prompt_manifest_revision`、`uq_context_evidence_snapshot_ref`、`uq_context_view_type`。
部分唯一索引(`indpred IS NOT NULL`)7 條:`uq_confset_active`、`uq_context_policy_active`、`uq_orchestrator_run_active_conversation`、`uq_orchestrator_run_command_{start,cancel,resume_idempotency}`、`uq_orchestrator_run_child_agent_run`、`uq_agent_run_event_cursor`、`uq_agent_run_deadline_cleanup`(共 9 條,逐條斷言名稱 + `pg_get_expr(indpred)` 正規化文字)。

#### 4.4 關鍵 CHECK 斷言(逐條比對 `pg_get_constraintdef` 正規化文字)

`ck_agent_run_kind`、`ck_agent_run_status`、`ck_agent_run_pinned_identity_v2`、`ck_agent_run_fencing_v2`、`ck_agent_run_direct_lineage`、`ck_agent_run_event_fencing`、`ck_agent_run_command_type`、`ck_user_group_membership_group_id`、`orchestrator_run_status_check`、`orchestrator_run_command_command_type_check`、`agent_run_approval_execute_status_check`、`operations_run_evidence_error_class_check`、`tenant_runtime_binding` 兩條、`operations_release_audit_kind_check`。
**新增必斷言**:`eval_run` 上**不存在** `candidate_kind IN ('skill','agent')` 的 CHECK(判別聯集已刪);任何 CHECK 定義文字中**不出現** `'agentic'`、`'flow'` 字面量。

#### 4.5 absence 斷言(fingerprint postcondition)

```
1. 舊表 = 0:  to_regclass('public.skill') IS NULL
              to_regclass('public.skill_revision') IS NULL
              to_regclass('public.agent_revision_skill') IS NULL
              to_regclass('public.agent_run_skill') IS NULL
              to_regclass('public.checkpoints') / checkpoint_blobs
              / checkpoint_writes / checkpoint_migrations IS NULL
2. 舊欄 = 0:  無任何 pg_attribute 名為 'kind' 位於 artifact 表
              無 'simple_form' 位於 agent_skill / *_revision
              (business_workflow.simple_form 必須存在 — 正向斷言)
              operations_execution_metric / operations_run_evidence 無 skill_name/skill_revision
3. VIEW/MATVIEW: count(pg_class WHERE relnamespace='public' AND relkind IN ('v','m')) = 0
4. TRIGGER:  count(pg_trigger t JOIN pg_class c ... WHERE NOT t.tgisinternal
             AND c.relnamespace='public') = 0        ← 必須加 NOT tgisinternal
5. RLS:      count(pg_class WHERE relnamespace='public' AND relrowsecurity) = 0
             count(pg_policy) = 0
6. FUNCTION: count(pg_proc WHERE pronamespace='public'
             AND oid NOT IN (extension-owned via pg_depend deptype='e')) = 0
7. 自訂 TYPE/DOMAIN/ENUM: count(pg_type WHERE typnamespace='public'
             AND typtype IN ('d','e','c') AND NOT extension-owned
             AND NOT 表的 composite rowtype (typrelid 對應 relkind='r')) = 0
8. SEQUENCE: 僅 deptype='a' 的 identity sequence,無獨立 sequence:
             count(pg_class c WHERE relkind='S' AND NOT EXISTS
               (pg_depend WHERE deptype='a')) = 0
9. EXTENSION: 恰為 {vector} ∪ {plpgsql}(名稱、版本、installation schema 全記入 fingerprint)
10. SCHEMA:  public 與 springaitest_meta 存在;springaitest_meta 未被 0001 觸及
```

---

### 5. seed 全量需求(02-spec §7)

#### 5.1 `SeedAsync` 現產出(逐筆精確值,L1218-1404)

**A. tenants**(L1221-1228,`ON CONFLICT (code) DO NOTHING`)

| code | name | invite_code |
|---|---|---|
| `demo-a` | `示範租戶 A` | `demo-a-invite` |
| `demo-b` | `示範租戶 B` | `demo-b-invite` |

**B. users**(L1231-1269,`ON CONFLICT (username) DO NOTHING`;tenant_id 以 `SELECT t.id FROM tenants WHERE code=@TenantCode` 反查)

| username | role | tenant | capabilities |
|---|---|---|---|
| `admin-a` | ADMIN | demo-a | `{workflow.manage}` |
| `user-a` | USER | demo-a | `{}` |
| `user-b` | USER | demo-b | `{}` |

密碼:`DbBootstrap.DefaultPassword = "password123"`(L17)。hash 來源:**`BCrypt.Net.BCrypt.HashPassword(DefaultPassword)` 在每次啟動即時計算**(L1264),**非硬編碼常數** — 每次啟動 salt 不同,但因 `ON CONFLICT DO NOTHING` 只有首次寫入生效。lite 模式的 `InMemoryAuthRepository` 另有一組**硬編碼**且相符的 hash。⚠ P3 seed 命令必須沿用「執行時計算」而非複製既有 hash,否則 lite/Dapper 兩側漂移。

**C. user_group_membership**(L1271-1282,`ON CONFLICT DO NOTHING`)
`(demo-a, admin-a, operations)`、`(demo-a, user-a, analysts)`、`(demo-b, user-b, analysts)`

**D. Default Agent-Runtime Workflow**(`SeedDefaultWorkflowAsync` L1329-1404)
- 前置:`AgentDefaults.ValidateRuntimeWorkflowFixture()` 必須零錯誤,否則 throw。
- `workflow` row(`ON CONFLICT (id) DO UPDATE` 全欄 reconcile):
  `id='00000000-0000-4000-8000-000000000001'` / `tenant_id='__system__'` / `name='Default Agent-Runtime Workflow'` / `kind='agent-runtime'` / `enabled=true` / `draft_version=1` / `draft_definition = AgentDefaults.RuntimeWorkflowDefinition::jsonb` / `draft_ui_metadata='{}'` / `draft_definition_canonical=convert_to(def,'UTF8')` / `draft_ui_metadata_canonical=convert_to('{}','UTF8')` / `draft_definition_sha256=SkillHash.Sha256(def)` / `draft_ui_metadata_sha256=SkillHash.Sha256("{}")` / `published_revision=3` / `system_owned=true` / `created_by='system'`
- `workflow_revision` row(`ON CONFLICT (workflow_id,revision) DO NOTHING` + 事後全欄相等驗證,不符即 throw):
  `revision=3`(`AgentDefaults.RuntimeWorkflowRevision`;rev1/rev2 為歷史快照,絕不原地修) / `status='published'` / `schema_version=1` / `definition/ui_metadata/canonical/sha` 同上 / `compiler_contract_version=WorkflowCompilerContracts.Current`(= `d4-graph-ir-1`) / `created_by='system'`
- definition 內容(`AgentDefaults.cs:39`,canonical 單行):`kind=agent-runtime`、`schemaVersion=1`、`runtimeVariant=**worker**`、`governance{maxConcurrency:1,maxSteps:40}`、9 節點 `start→preflight(dependency_and_capability_preflight)→inject_context(inject_authorized_context)→initial_checkpoint(checkpoint)→agent_loop(bounded_agent_loop, maxIterations:8, children: model_step / load_skill[optional] / tool_gate / tool_call / checkpoint_budget)→validate_output→repair(bounded_repair_or_controlled_failure, maxRepairRounds:2)→end`,7 條線性 edge `e0..e6`。
- 存在防呆:若同 id 的 workflow 屬非 `__system__` 租戶 → throw。

**E. Context Enrichment 預設**(`SeedContextEnrichmentAsync` L1312-1321)
- `source_catalog` 單筆(全域,`tenant_id=NULL`,`ON CONFLICT(source_id) DO UPDATE`):
  `source_id='backend_documents'` / `source_type='knowledge-source'` / `evidence_type='document'` / `authority_class='server-owned'` / `date_coverage='{}'` / `freshness_sla='{}'` / `adapter_id='backend.retrieval_search'` / `acl_policy_id=NULL` / `enabled=true` / `required=true` / `timeout_seconds=15` / `minimum_deadline_seconds=2`
- `context_policy` **每租戶一筆**(`SELECT ... FROM tenants`,`ON CONFLICT(tenant_id,name) DO UPDATE ... WHERE created_by='system'`):
  `name='default'` / `is_active=true` / `created_by='system'` / `values` =
  ```json
  {"readiness":{"ready_threshold":0.85,"assumptions_min":0.70,"optional_failure_penalty":0.10},
   "bootstrap_requirements":[{"name":"document","evidence_type":"document","mandatory":true}],
   "source_requirements":[{"source_id":"backend_documents","required":true}],
   "source_precedence":["backend_documents"]}
  ```

**F. eval suite**(`SeedEvalSuiteAsync` L1298-1310)
- `suite_id='csr-eval-001'`,**每租戶各一份**,經 `EvalRepository.PublishSuiteRevisionAsync(tenantCode, SuiteId, canonical, "system", ct)`(與正式 publish 端點同一寫入路徑,不用 raw SQL)。
- `canonical = AgentCanonicalizer.CanonicalizeDefinition(CsrEval001Suite.CasesJson)`;hash 為 `SkillHash.Sha256`。
- 內容:`policy.required_case_ids=["chit-chat-01","retrieval-01"]`、`policy.freshness_seconds=2592000`;**16 個 case**:`retrieval-01/02`、`compare-01/02`、`infer-01/02`、`inspire-01/02`、`stats-01/02`、`tenant-custom-01/02`、`chit-chat-01/02`、`ambiguous-01/02`,全部 `mode=deterministic`,`expected.category` ∈ {retrieval, compare, infer, inspire, stats, tenant_custom_skill, chit_chat, ambiguous},`acceptable_skills` 分別為 `kb_query`/`compare_metrics`/`infer_trend`/`inspire_ideas`/`compute_stats`/`tenant_custom_search`/`[]`。
- 寫入 `eval_suite`(current pointer)+ `eval_suite_revision`(revision 1、`cases_canonical`、`cases_sha256`、`case_count=16`、`created_by='system'`)。

**⚠ 目前 SeedAsync 完全不產出**:`app_config`(刻意無預設,靠 frontend `AGENT_DEFAULT_CONFIG_FALLBACKS`)、`configuration_set`、`agent`/`agent_revision`、`orchestrator`/`orchestrator_revision`、`tenant_runtime_binding`、`metric_definition`、任何 skill/business workflow。

#### 5.2 P3 新增:D5 Root Workflow / Worker / Verifier / runtime binding

02-spec §7「genuinely new content」。從 D5/D6 契約 + 現有表形狀推導的**最小可行 seed**(每租戶 demo-a/demo-b 各一套,或僅 demo-a + demo-b 共用 system workflow):

**G1. Root Harness Workflow**(新 `workflow` + `workflow_revision`)
必要:`kind='agent-runtime'`(Root/Worker/Verifier 都走同一 Graph IR 契約)、`tenant_id='__system__'`、`system_owned=true`、`enabled=true`、`published_revision=1`、`compiler_contract_version=d4-graph-ir-1`、`status='published'`、canonical bytes + sha 齊備。definition 需含 Root 專屬 stage(context acquire / parallel worker dispatch / verifier / PASS-only aggregation),**目前 repo 無此 fixture** — 需新增(可比照 `plans/agent-platform-redesign/fixtures/default-agent-runtime-workflow.json` 的 byte-for-byte 檢查模式)。

**G2. Worker Harness**:可直接重用既有 `Default Agent-Runtime Workflow` rev3(`runtimeVariant='worker'`),不需新 workflow row。

**G3. Verifier Harness**(新 `workflow` + `workflow_revision`)
必要:`runtimeVariant='verifier'`、read-only(D4 契約:「verifier Agents must pin a published read-only verifier harness;Worker harnesses are rejected」、「no worker/write stages」)。**目前 repo 無 verifier fixture** — 需新增。

**G4. Worker Agent + Verifier Agent**(各一組 `agent` + `agent_revision`)
`agent`:`tenant_id=<租戶 code>`、`slug`(如 `demo-worker`/`demo-verifier`)、`enabled=true`、`draft_definition`/`draft_definition_canonical`/`draft_definition_sha256` 齊備、`draft_validated_version=draft_version`、`published_revision=1`。
`agent_revision` rev1:`status='published'`、`system_prompt`、`execution_roles/capabilities/output_contract/audience/business_rules/allowed_tools/knowledge_sources/runtime_limits` 皆給合法預設(空集合即可)、`definition_sha256` + `canonical_definition` 齊備、**`runtime_workflow_id/_revision` 必填**(Worker → G2;Verifier → G3;受 `fk_agent_revision_runtime_workflow` 約束)、`prompt_manifest_revision/sha256` 留 NULL。

**G5. Orchestrator + orchestrator_revision**(D5 root)
`orchestrator`:`tenant_id=<租戶 code>`、`name`、`enabled=true`、draft 三件組齊備、`published_revision=1`。
`orchestrator_revision` rev1:`status='published'`、`definition` + `canonical_definition`(NOT NULL)+ `definition_sha256`(NOT NULL)、**`workflow_id/workflow_revision` = G1 Root Workflow**、**`verifier_agent_id/verifier_agent_revision` = G4 Verifier**。
註:Orchestrator publish 的正規路徑會 revalidate context tools 對 Workflow `/tools` 的 risk metadata(只允許 `read`/`low`)並驗 Verifier Agent 的 `runtime_workflow` hash — seed 命令若直接 INSERT 會繞過這些檢查,建議**走 repository publish 路徑**(與 `SeedEvalSuiteAsync` 同一理由),但那需要 Workflow 服務在 seed 時可達。**此為 P3 需裁決點。**

**G6. tenant_runtime_binding**(D6 rollout,表形狀 L473-481)
每租戶一筆:`tenant_id=<code>` / `enabled=true` / `default_orchestrator_id=<G5 id>` / `default_orchestrator_revision=1` / `canary_user_ids`(D6 預設路由要求 canary membership → 至少 `["user-a"]`/`["user-b"]`,或若 P4 移除 canary 機制則 `[]`)。
CHECK 硬性要求:`enabled=true` 時 `default_orchestrator_id IS NOT NULL AND default_orchestrator_revision >= 1`;`canary_user_ids` 必須是 JSON array。
⚠ 02-spec §8 刪除 `AGENT_CHAT_ENABLED`/`AGENT_CHAT_TENANT_ALLOWLIST`,但 `canary_user_ids` 屬 DB 欄位不是 flag,ledger 未提及是否保留 → **需裁決**;若保留但語意改為「空陣列 = 全租戶」,seed 給 `[]` 即可。

**依賴順序**(seed 命令必須遵守,否則撞 FK):
`tenants → users → user_group_membership` → `workflow(G1/G2/G3) → workflow_revision` → `agent → agent_revision(pin workflow_revision)` → `orchestrator → orchestrator_revision(pin workflow_revision + agent_revision)` → `tenant_runtime_binding(pin orchestrator_revision)` → `source_catalog → context_policy` → `eval_suite → eval_suite_revision`。

**冪等性要求**(02-spec §7「idempotent」):tenants/users/membership 用 `ON CONFLICT DO NOTHING`;mutable pointer(workflow/agent/orchestrator base row、tenant_runtime_binding)用 `ON CONFLICT DO UPDATE`;**所有 `*_revision` 表一律 `ON CONFLICT DO NOTHING` + 事後內容相等驗證,不符即 fail**(沿用 `SeedDefaultWorkflowAsync` L1385-1401 的 pattern,避免既有 pin 在 restart 後漂移)。

---

#### 待裁決事項(阻擋 SQL 撰寫,共 8 項)

1. Agent↔Skill pin 表與兩張 typed run pin 表的**正式表名**(02-spec 未命名)。
2. 兩張 run pin 表的 `position` 是否共用序號空間。
3. `agent_skill` 是否真的無 `definition` 欄(spec §2.1 未列,但今日 `skill.definition` 是引擎事實來源)。
4. `agent_skill_revision.instruction_sha256` 的計算來源定義(全新欄位,repo 無既有實作)。
5. `orchestrator_revision.verifier_agent_*` 與 `orchestrator_run.*` 的缺失 FK 是否補建(ledger 未涵蓋)。
6. `agent_run` 兩條重複 fencing CHECK 保留哪一條。
7. `tenant_runtime_binding.canary_user_ids` 在 P4 刪 canary flag 後的去留。
8. D5 seed 走 raw SQL 還是 repository publish 路徑(後者需 Workflow 服務在 seed 時可達)。

---

## 4. backend 消費圖(P3-3 輸入)

HEAD verified `b93b63c` (branch `feat/dotnet-backend`); codebase-memory index is at the same `head_sha`. All line numbers below are read directly from HEAD.

---

### P3 Backend Inventory

#### 1. `skill` / `skill_revision` full consumption graph

##### 1.1 DDL (all in `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\DbBootstrap.cs`)

| Object | Lines |
| --- | --- |
| `CREATE TABLE skill` | `DbBootstrap.cs:110-122` (`uq_skill_tenant_name` at :122) |
| `CREATE INDEX ix_skill_tenant_enabled` | `DbBootstrap.cs:123` |
| `CREATE TABLE skill_revision` | `DbBootstrap.cs:125-133` (`uq_skill_revision UNIQUE(skill_id,revision)` at :133; FK `skill_id → skill(id)` at :127) |
| `CREATE INDEX ix_skill_revision_skill` | `DbBootstrap.cs:134` |
| `ALTER skill ADD package bytea` | `DbBootstrap.cs:137` |
| `ALTER skill_revision ADD package_sha256` / `package` | `DbBootstrap.cs:138-139` |
| `ALTER skill ADD kind` / `skill_revision ADD kind` | `DbBootstrap.cs:140-141` |
| `ALTER skill ADD simple_form jsonb` | `DbBootstrap.cs:144` (ledger §1.1 note confirmed — post-column, not in CREATE body) |
| `kind` backfill UPDATEs | `DbBootstrap.cs:146-154` |
| `kind` SET DEFAULT/NOT NULL ×4 | `DbBootstrap.cs:155-158` |

**Dependent FK tables**

| Table | DDL | FK to skill family |
| --- | --- | --- |
| `agent_revision_skill` | `DbBootstrap.cs:227-234` | inline `skill_id uuid NOT NULL REFERENCES skill(id)` at :230; composite `fk_agent_revision_skill_skill_revision (skill_id, skill_revision) → skill_revision(skill_id,revision)` created NOT VALID at `:314-320`, validated at `:328`. Sibling `fk_agent_revision_skill_agent_revision` at `:307-313`, validated `:327`. |
| `agent_run_skill` | `DbBootstrap.cs:686-698` | `fk_agent_run_skill_revision (skill_id, skill_revision) → skill_revision(skill_id,revision)` at `:697-698`; also `run_id → agent_run(id)` `:687`, `uq_agent_run_skill_position` `:696`. Note: `skill_id` here has **no** direct FK to `skill(id)` — only through `skill_revision`. |

`agent_revision_skill` + `agent_run_skill` are the only two FK dependents. Both are in the `MigrationManifest.SpringAITestLegacyObjects` allowlist (`c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\Migrations\MigrationManifest.cs:64,69`).

##### 1.2 `ISkillRepository` — complete public surface

`c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Skills\ISkillRepository.cs` — 13 members (4 are default-interface-method kind overloads):

| Member | Line | Note |
| --- | --- | --- |
| `ListAsync(tenant, ct)` | `:7` | |
| `ListAsync(tenant, kind, ct)` | `:10-11` | **default impl** — filters `item.Kind == kind` in memory |
| `GetAsync(tenant, name, ct)` | `:14` | |
| `GetAsync(tenant, name, kind, ct)` | `:17-21` | **default impl** |
| `CreateAsync(tenant, skill, createdBy, ct)` | `:28` | |
| `CreateAsync(tenant, expectedExistingKind, skill, createdBy, ct)` | `:33-34` | kind-fenced revive |
| `UpdateAsync(tenant, name, skill, updatedBy, ct)` | `:37` | |
| `UpdateAsync(tenant, name, expectedKind, skill, updatedBy, ct)` | `:42-43` | |
| `ImportAsync(tenant, skill, package, packageSha256, createdBy, ct)` | `:52-53` | upsert, always bumps revision |
| `DeleteAsync(tenant, name, ct)` | `:56` | |
| `DeleteAsync(tenant, name, kind, ct)` | `:62` | |
| `ListRevisionsAsync(tenant, name, ct)` | `:68` | |
| `GetRevisionAsync(tenant, name, revision, ct)` | `:71-72` | |

Implementations:
- `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Skills\SkillRepository.cs:13` (Dapper) — method bodies at `:37, :40, :58, :61, :81, :84, :132, :136, :194, :224, :227, :242, :260`
- `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\InMemory\InMemorySkillRepository.cs:12` — method bodies at `:28, :31, :49, :52, :81, :84, :136, :139, :181, :223, :226, :250, :261`; plus an extra non-interface member `TryGetSnapshotSource(... out SkillSnapshotSource?)` at `:304-321` used by the in-memory Agent/AgentRun repos.

**Callers (production):**

| Caller | File:line |
| --- | --- |
| `SkillController` (ctor field) | `Skills\SkillController.cs:31,36` |
| `BusinessWorkflowController` (ctor field) | `BusinessWorkflows\BusinessWorkflowController.cs:16,19` |
| `InMemoryAgentRepository` (ctor) | `Data\InMemory\InMemoryAgentRepository.cs:21` |
| `InMemoryAgentRunRepository` (ctor ×2 overloads) | `Data\InMemory\InMemoryAgentRunRepository.cs:48,55` |
| DI registration | `Program.cs:91` (InMemory), `Program.cs:112` (Dapper, scoped) |

**Callers (tests)** — `TestWebAppFactory.cs:57-58` (swaps in `FakeSkillRepository`), `BusinessWorkflowApiTests.cs:90,179,227,250,280,300`, `SkillsBusinessWorkflowsDualTrackConsistencyTests.cs:112,146,183`, `SkillImportTests.cs:124,143,203,639,716,787`, `SkillExportTests.cs:28`, `SkillExecutionArtifactTests.cs:188,200,201,207`.

The Dapper `SkillRepository` is the only path that touches `skill`/`skill_revision` SQL directly outside `DbBootstrap`; `AgentRepository`, `AgentRunRepository` and `OrchestratorRunRepository` join those tables with their own SQL (see §1.4).

##### 1.3 Controller route inventory

**`SkillController`** — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Skills\SkillController.cs`, base route `api/skills` (`:20`). Dependencies: `ISkillRepository`, `ISkillValidator`, `ISkillPackageValidator` (`:31-41`).

| Route | Line | Auth | Repo/collab calls |
| --- | --- | --- | --- |
| `GET /api/skills` | `:44-46` | USER | `ListAsync(tenant)` |
| `GET /api/skills/{name}` | `:49-52` | USER | `GetAsync` |
| `GET /api/skills/{name}/export` | `:60-65` | USER | `GetAsync` + `SkillExporter.ToZip` (`:64`) |
| `GET /api/skills/{name}/package` | `:73-85` | internal-token only | `GetAsync`; hard `kind=="agentic"` gate at `:78` |
| `GET /api/skills/{name}/revisions` | `:88-100` | USER | `ListRevisionsAsync` |
| `GET /api/skills/{name}/revisions/{revision:int}/execution-artifact` | `:107-138` | `[AdminOnly]` `:108` | `GetRevisionAsync`; `SkillHash.Sha256` verify `:118,123`; emits `SkillExecutionArtifact(name, revision, kind, definition, definitionSha, packageSha, base64package)` `:130-137` |
| `POST /api/skills` | `:141-161` | `[AdminOnly]` | `ValidateAsync` → `SkillNameRules.ReservedBusinessWorkflowNames` `:147` → `CreateAsync(tenant,"flow",…)` `:152` |
| `PUT /api/skills/{name}` | `:168-201` | `[AdminOnly]` | `GetAsync` → `RejectAgenticDefinitionOnly` `:175` → `ValidateAsync` → `UpdateAsync(…, "flow", …)` `:192` |
| `POST /api/skills/{name}/import` | `:212-215` | `[AdminOnly]` | → `ImportCoreAsync(name)` |
| `POST /api/skills/import` | `:221-224` | `[AdminOnly]` | → `ImportCoreAsync(null)` (server-derived name) |
| `POST /api/skills/{name}/revisions/{revision:int}/restore` | `:308-369` | `[AdminOnly]` | `GetAsync`+`GetRevisionAsync`; agentic branch re-validates package `:334`; flow branch re-validates definition `:353`; writes via `ImportAsync` `:366` |
| `DELETE /api/skills/{name}` | `:372-383` | `[AdminOnly]` | `DeleteAsync(tenant,name)` (kind-unfenced) |

Private helpers: `ImportCoreAsync` `:226-301`, `ValidateAsync` `:390-405`, `ToFieldErrors` `:408-423`, `RejectAgenticDefinitionOnly` `:426-434`, `SimpleFormText` `:442-443`, `ToSkill` `:447-457`. Transport cap `MaxImportUploadBytes = 25 MiB` `:29`.

**`BusinessWorkflowController`** — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\BusinessWorkflows\BusinessWorkflowController.cs`, base route `api/business-workflows` (`:13`). Dependencies: `ISkillRepository`, `ISkillValidator` only (`:16-23`) — **no** `ISkillPackageValidator`.

| Route | Line | Repo call |
| --- | --- | --- |
| `GET /api/business-workflows` | `:25-27` | `ListAsync(tenant, "flow")` |
| `GET /api/business-workflows/{name}` | `:29-31` | `GetAsync(tenant,name,"flow")` |
| `GET /api/business-workflows/{name}/export` | `:33-38` | `GetAsync(…,"flow")` + `SkillExporter.ToZip` `:37` |
| `POST /api/business-workflows` | `:40-63` | `ValidateAsync` + `ReservedBusinessWorkflowNames` `:46` + `CreateAsync(…,"flow",…)` `:51` |
| `PUT /api/business-workflows/{name}` | `:65-93` | `ValidateAsync` + `UpdateAsync(…,"flow",…)` `:85` |
| `DELETE /api/business-workflows/{name}` | `:95-105` | `DeleteAsync(…,"flow")` `:99` |

No `/validate`, no `/revisions*`, no `/execution-artifact`, no import/restore on this controller today — 02-spec §3.2 requires `/api/business-workflows/{name}/revisions/{revision}/execution-artifact`, which is a **new** route, not a move.

##### 1.4 Snapshot / run repos reading & writing skill pins

**`AgentRunSnapshotBuilder`** — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\AgentRuns\AgentRunSnapshotBuilder.cs`

| Concern | Line |
| --- | --- |
| `ValidateExecutionContract(…, IReadOnlyList<SkillSnapshotSource> skills, …)` | `:33-47` (overload), `:49-104` (impl) |
| skill count / dedupe / name / description bounds | `:69-87` |
| **kind gate** `skill.Kind is not ("agentic" or "flow")` | `:88-92` ← P3 must retype |
| `Build(...)` skill array emit (`name/revision/kind/description/definition_sha256/package_sha256/allowed_tools`) | `:185-200`; ordered by `agent.SkillBindings.First(...).Position` `:186-187` |
| snapshot root `["skills"] = skillArray` | `:241` |
| snapshot shape check requires `skills` array | `:559` |
| D5 root pin `["skill_revisions"]` map (name→revision) inside `ToRootWorkerPin` | `:394-398` |
| `SkillDescriptionOf(immutableDefinition)` — YAML parse of the immutable revision to recover `description` | `:496-522` |
| `SkillHash` uses | `:273, :352, :353, :373, :452` |

Types: `SkillSnapshotSource` at `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\AgentRuns\AgentRunSnapshotSources.cs:23`; `PublishedAgentSnapshotSource.SkillBindings` at `AgentRunSnapshotSources.cs:15`.

**`AgentRunRepository`** (Dapper) — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\AgentRuns\AgentRunRepository.cs`

| Concern | Line |
| --- | --- |
| run projection subquery `FROM agent_run_skill p WHERE p.run_id=agent_run.id` → `SkillPins` json | `:64` |
| pin resolution SELECT joining `agent_revision_skill` → `skill` → `skill_revision` with `FOR SHARE OF s, sr` | `:244-256` |
| disabled-skill fail-closed | `:257-261` |
| per-revision definition/package hash verify | `:263-278` |
| `AgentRevisionSkillInfo` projection into `PublishedAgentSnapshotSource` | `:288-291` |
| `SkillSnapshotSource` list build (calls `SkillDescriptionOf`) | `:301-309` |
| `INSERT INTO agent_run_skill (run_id,position,skill_id,skill_revision,skill_name,kind,definition_sha256,package_sha256)` | `:366-385` |
| `SkillPins` deserialize → `AgentRunSkillPinResponse` | `:2584-2585`; row record field `:2672` |

**`InMemoryAgentRunRepository`** — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\InMemory\InMemoryAgentRunRepository.cs:159-160` (direct-agent path), `:325-326` (orchestrator-child path), pin projection `:1897`, state `Skills` list `:1997`.

**`InMemoryAgentRepository`** — `Data\InMemory\InMemoryAgentRepository.cs:286` (`AgentRevisionSkillInfo` projection), `:399` (publish signature), `:440-463` (`ResolvedSkillBinding` resolution), `:522` (`Bindings` state), `:536`.

**`AgentRepository`** (Dapper) — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Agents\AgentRepository.cs`
- read revision bindings `FROM agent_revision_skill rs JOIN skill s ON s.id = rs.skill_id` `:353`, grouped/projected `:362-368`
- restore copy `INSERT INTO agent_revision_skill … FROM agent_revision_skill WHERE agent_id=@id AND agent_revision=@revision` `:484-486`
- publish insert `INSERT INTO agent_revision_skill (…)` `:544`
- pin resolution `SkillPinRow(Guid Id, int CurrentRevision)` `:616-627`, record `:677`
- `AgentCanonicalizer.SkillBindingsOf` `:606`

**`OrchestratorRunRepository`** — `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\OrchestratorRuns\OrchestratorRunRepository.cs`
- `:275` (long line — child skill pin SQL), `:407`, `:409` (`AgentRevisionSkillInfo` build), `:410`
- `:427` `SELECT s.name Skill, ars.skill_revision … FROM agent_revision_skill ars JOIN skill s ON s.id=ars.skill_id … FOR SHARE OF ars,s`
- `ChildSource(… IReadOnlyList<SkillSnapshotSource> Skills)` `:461`

**`InMemoryOrchestratorRunRepository`** — `OrchestratorRuns\InMemoryOrchestratorRunRepository.cs:61`, `:476`.

**DTOs** — `AgentRuns\AgentRunDtos.cs:176` (`["skills"] IReadOnlyList<AgentRunSkillPinResponse>`), `:182` (`AgentRunSkillPinResponse`); `Agents\AgentDtos.cs:23,31,115,124,131`.

##### 1.5 execution-artifact routes at HEAD (3 backend routes + 1 workflow consumer)

| Route | File:line |
| --- | --- |
| `GET /api/skills/{name}/revisions/{revision:int}/execution-artifact` | `Skills\SkillController.cs:107` |
| `GET /api/agent-runs/{runId:guid}/execution-artifact` | `AgentRuns\AgentRunController.cs:105` |
| `GET /api/orchestrator-runs/{runId:guid}/execution-artifact` | `OrchestratorRuns\OrchestratorRunController.cs:25` |
| Workflow-side consumer of the Skill route | `c:\Users\a8022\Desktop\SpringAITest\workflow\app\runtime\artifacts.py:221` |
| Workflow-side consumer of the agent-run route | `c:\Users\a8022\Desktop\SpringAITest\workflow\app\runtime\backend.py:134` |

Backend tests pinning the Skill artifact route: `backend\tests\Backend.Api.Tests\SkillExecutionArtifactTests.cs:16,57,78,81,107,130,157,180`.

##### 1.6 import / restore pipeline (`ISkillPackageValidator` chain)

```
POST /api/skills/{name}/import  (SkillController.cs:212)
POST /api/skills/import         (SkillController.cs:221)
        └─► SkillController.ImportCoreAsync           SkillController.cs:226-301
              ├─ multipart guard / 25MiB cap          :231-247
              ├─ ISkillPackageValidator.ValidatePackageAsync   :256-258
              │     └─ WorkflowSkillPackageValidator            Skills\WorkflowSkillPackageValidator.cs:26-72
              │           ├─ POST {WORKFLOW_BASE_URL}/skills/validate-package   :42
              │           ├─ UseInternalIdentity(token, tenant, user, role)      :46
              │           └─ ValidateSuccessContract (name/kind/required_role/
              │              canonical-name/canonical-kind cross-checks) :77-137
              ├─ 422 on !Valid  :260-266   /  502 contract violation :269-274
              ├─ SkillNameRules.IsStandard (server-derived name)  :280
              ├─ SkillNameRules.ReservedBusinessWorkflowNames     :287
              ├─ SkillHash.Sha256(bytes)                          :294
              └─ ISkillRepository.ImportAsync(...)                :296-297

POST /api/skills/{name}/revisions/{rev}/restore  (SkillController.cs:308)
        ├─ agentic branch → ISkillPackageValidator.ValidatePackageAsync(target.Package, …)  :334-336
        │     (409 when target.Package is null — pre-snapshot legacy revision)  :327-332
        ├─ flow branch → ISkillValidator.ValidateAsync via ValidateAsync(…)     :353
        └─ ISkillRepository.ImportAsync(...)                                    :366
```

- Interface + result record: `Skills\ISkillPackageValidator.cs:9-13` (`SkillPackageValidationResult`), `:21-30` (interface).
- DI: `Program.cs:155` (`AddScoped<ISkillPackageValidator>(… new WorkflowSkillPackageValidator(…))`).
- `ISkillValidator` DI: `Program.cs:137` (`new WorkflowSkillValidator(…)`), impl at `BusinessWorkflows\WorkflowSkillValidator.cs:14`.
- One-time legacy migration helper `SkillPackageMigration` (`Data\SkillPackageMigration.cs:17`) is called **only** from `DbBootstrap.MigrateSkillRowAsync` — `DbBootstrap.cs:1090,1092,1094,1099,1100,1109,1132,1134,1140,1142,1148,1151`. Zero other callers ⇒ deleting `DbBootstrap` orphans this file entirely (ledger §1 row 19 confirmed).

---

#### 2. governance / eval skill-shaped fields

##### 2.1 Schema

| Item | File:line |
| --- | --- |
| `eval_run.candidate_kind text NOT NULL CHECK(candidate_kind IN ('skill','agent'))` | `DbBootstrap.cs:889` |
| `candidate_ref jsonb NOT NULL, candidate_pins jsonb, candidate_identity_sha256 text NOT NULL` | `DbBootstrap.cs:890` |
| `eval_run` table body / uniq / index | `DbBootstrap.cs:887-896` |
| `eval_case_result` | `DbBootstrap.cs:897-901` |
| `eval_suite` / `eval_suite_revision` | `DbBootstrap.cs:868-879` |
| `operations_execution_metric` … `skill_name text, skill_revision integer` in CREATE body | `DbBootstrap.cs:509` |
| `ALTER operations_execution_metric ADD skill_name/skill_revision/agent_id/agent_revision` | `DbBootstrap.cs:515` |
| `operations_run_evidence … skill_name text, skill_revision integer` | `DbBootstrap.cs:532` (ledger §1 row 23 cites `:531`; at HEAD it is **:532**) |

##### 2.2 Repository read/write points

**Dapper `OperationsGovernanceRepository`** (`backend\src\Backend.Api\OperationsGovernance\OperationsGovernanceRepository.cs`):
- `RecordTelemetryAsync` → `INSERT INTO operations_execution_metric(…,skill_name,skill_revision,…)` — `:103`
- `RecordEvidenceAsync` → `INSERT INTO operations_run_evidence(… agent_id,agent_revision,orchestrator_revision,skill_name,skill_revision, …)` — column list `:118`, param bind `:139`
- skill aggregate read `SELECT skill_name AS Name, skill_revision AS Revision … FROM operations_execution_metric … GROUP BY skill_name,skill_revision` — `:174` → `SkillRevisionMetric`

**`InMemoryOperationsGovernanceRepository`** (`OperationsGovernance\InMemoryOperationsGovernanceRepository.cs`): skill grouping/projection `:123-125` (matches ledger row 55).

**Contracts** (`OperationsGovernance\IOperationsGovernanceRepository.cs`):
- `OperationsTelemetry(… string? SkillName, int? SkillRevision, …)` — `:35`
- `RunEvidenceEnvelope(… string? SkillName, int? SkillRevision, …)` — `:46`
- `OperationsMetrics(… IReadOnlyList<SkillRevisionMetric> Skills …)` — `:65`
- `SkillRevisionMetric(string Name, int Revision, …)` — `:68`

**Wire/controller** (`OperationsGovernance\OperationsTelemetryController.cs`): validation bounds `:20`, metric write `:33`, evidence write `:63`, request DTO `[JsonPropertyName("skill_name")] / ("skill_revision")` `:100-101`.

**Eval — Dapper `EvalRepository.cs`:**
- `INSERT INTO eval_run(… candidate_kind, candidate_ref, candidate_pins, candidate_identity_sha256, …)` — `:121-131`
- replay/conflict identity compare — `:142`
- read projection `r.candidate_kind AS CandidateKind, r.candidate_ref::text …` — `:223-225`
- summary mapper — `:252-253`; row record fields — `:274-277`

**Eval — `InMemoryEvalRepository.cs`:** identity compare `:94`, summary projection `:177-178`.

**Eval — `EvalController.cs`:** idempotency identity compare `:126`, `EvalRunCandidateResponse(CandidateKind, CandidateRefJson, CandidatePinsJson, CandidateIdentitySha256)` `:202`, doc note about jsonb round-trip `:211`.

**Eval DTO contracts — `OperationsGovernance\EvalDtos.cs`:**
- `EvalRunWrite(… string CandidateKind, string CandidateRefJson, string? CandidatePinsJson, string CandidateIdentitySha256, …)` — `:95-109` (fields at `:99-102`)
- `EvalRunSummary(… CandidateKind/CandidateRefJson/CandidatePinsJson/CandidateIdentitySha256 …)` — `:117-130` (fields at `:121-124`)
- `EvalCaseResultRecord` `:91-92`; `EvalRunWriteStatus` `:111`; `EvalRunWriteResult` `:113`; `EvalRunDetail` `:132`

Ledger row 24's cited range `EvalDtos.cs:99-124` is still accurate at HEAD.

---

#### 3. Shared helper status

##### 3.1 `SkillHash` (`c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Skills\SkillHash.cs`)

**320 call sites across 56 files** (ledger said "24 files across 10 modules" — that count is now stale by more than 2×). Production files (32) with occurrence counts:

| File | Count |
| --- | --- |
| `AgentRuns\AgentRunRepository.cs` | 24 |
| `OrchestratorRuns\OrchestratorRunRepository.cs` | 22 |
| `Data\InMemory\InMemoryAgentRunRepository.cs` | 14 |
| `OrchestratorRuns\InMemoryOrchestratorRunRepository.cs` | 8 |
| `Data\DbBootstrap.cs` | 7 |
| `Data\InMemory\InMemorySkillRepository.cs` | 7 |
| `AgentRuns\AgentRunSnapshotBuilder.cs` | 5 |
| `Agents\AgentRepository.cs` | 5 |
| `Agents\AgentController.cs` | 5 |
| `Data\InMemory\InMemoryAgentRepository.cs` | 5 |
| `Skills\SkillController.cs` | 5 |
| `Skills\SkillRepository.cs` | 3 |
| `Orchestrators\OrchestratorRepository.cs` | 3 |
| `Data\InMemory\InMemoryAgentRunApprovalRepository.cs` | 3 |
| `PromptArtifacts\PromptManifestResolutionController.cs` | 3 |
| `AgentRuns\AgentRunCommandInput.cs` | 2 |
| `Contexts\ContextCanonicalizer.cs` | 2 |
| `Contexts\ContextRepository.cs` | 2 |
| `Contexts\ContextAcquireProjection.cs` | 2 |
| `OperationsGovernance\EvalController.cs` | 2 |
| `PromptArtifacts\PromptArtifactRepository.cs` | 2 |
| `PromptArtifacts\InMemoryPromptArtifactRepository.cs` | 2 |
| `AgentRuns\AgentRunApprovalRepository.cs` | 6 |
| `AgentRuns\AgentRunPolicies.cs` | 1 |
| `Agents\AgentCanonicalizer.cs` | 1 |
| **`Data\Migrations\MigrationManifest.cs`** | **1** ← P2 dependency, see below |
| `Workflows\WorkflowCanonicalizer.cs` | 1 |
| `Data\InMemory\InMemoryRagRepository.cs` | 1 |
| `OrchestratorRuns\OrchestratorRunDtos.cs` | 1 |
| `Contexts\ContextTaskEnvelopeProjection.cs` | 1 |
| `OperationsGovernance\EvalRepository.cs` | 1 |
| `OperationsGovernance\OperationsGovernanceController.cs` | 1 |
| `OperationsGovernance\InMemoryEvalRepository.cs` | 1 |

Test files (24): `SkillRepositoryTests.cs` 33, `AgentRunSnapshotContractTests.cs` 28, `AgentRunRepositoryTests.cs` 22, `OrchestratorRunRepositoryTests.cs` 10, `InMemoryRepositoriesTests.cs` 9, `AgentRepositoryTests.cs` 8, `BusinessWorkflowApiTests.cs` 7, `InMemorySkillRepositoryConcurrencyTests.cs` 7, `SkillExecutionArtifactTests.cs` 7, `AgentsApiTests.cs` 6, `SkillsApiTests.cs` 5, `InMemoryAgentRunRecoveryTests.cs` 5, `ContextRepositoryTests.cs` 3, `PromptArtifactsPostgresTests.cs` 3, `OrchestratorRepositoryTests.cs` 3, `SkillsBusinessWorkflowsDualTrackConsistencyTests.cs` 3, `SkillImportTests.cs` 3, `AgentRunsApiTests.cs` 2, `PromptArtifactsApiTests.cs` 2, `SharedRunPolicyTests.cs` 2, `AgentRunApprovalInMemoryTests.cs` 1, `OperationsGovernancePostgresApiTests.cs` 1, `RunEvidenceEnvelopePostgresApiTests.cs` 1.

**P2's `MigrationManifest` dependency is confirmed and load-bearing:** `MigrationManifest.cs:4` (`using Backend.Api.Skills;`) and `:165` (`SkillHash.Sha256(sql)` producing each script's immutable `Checksum`). Moving `SkillHash` out of `Skills/` in P3 changes the `using` in the migration runner — but *not* the checksum value, since the algorithm is unchanged. Nothing in the manifest persists the namespace.

##### 3.2 `SkillNameRules` (`backend\src\Backend.Api\Skills\SkillNameRules.cs`) — `internal static`

| Member | Line | Consumers |
| --- | --- | --- |
| `ReservedBusinessWorkflowNames` (13 entries) | `:13-18` | `SkillController.cs:147`, `SkillController.cs:287`, `BusinessWorkflowController.cs:46` |
| `IsStandard(name)` | `:20-31` | `SkillController.cs:280`, `DbBootstrap.cs:1069` (in `MigrateSkillRowAsync`) |

Ledger row 52 line reference `Data/DbBootstrap.cs:1069` is **still exact at HEAD**. After `SkillController` + `DbBootstrap` are deleted, `BusinessWorkflowController.cs:46` is the only surviving consumer — and only of `ReservedBusinessWorkflowNames`, not `IsStandard`. `IsStandard` becomes dead unless a new Agent Skill name validator adopts it.

##### 3.3 `SkillExporter` (`backend\src\Backend.Api\Skills\SkillExporter.cs`) — `public static`

Single public entry `ToZip(Skill)` `:23-32`; privates `WriteEntry` `:34`, `BuildManifest` `:43-53`, `YamlScalar` `:61-84`, `ImplicitNonStrings` `:91-94`, `IsPlainSafe` `:96-133`.

Callers: exactly two — `SkillController.cs:64` and `BusinessWorkflowController.cs:37`. Ledger row 53's `BusinessWorkflowController.cs:37` is exact at HEAD. Since 02-spec §2.1 says Agent Skill export uses the stored `package`, the surviving consumer is the Business Workflow one only.

##### 3.4 `ISkillValidator` / `SkillMetadata` (`backend\src\Backend.Api\Skills\ISkillValidator.cs`)

| Type | Line | Current usage face |
| --- | --- | --- |
| `SkillValidationError(Code, Message, Line)` | `:4` | `ISkillPackageValidator.cs:11`, `SkillValidationResult`, both controllers' `ToFieldErrors` |
| **`SkillMetadata(Name, Description, RequiredRole, Kind = "flow")`** | `:14` | **Both sides**: Agent Skill package path — `ISkillPackageValidator.cs:12` (`SkillMetadata? Skill`), `WorkflowSkillPackageValidator.cs:63-68`, `SkillController.cs:276,320,346,447`; Business Workflow path — `WorkflowSkillValidator` result, `BusinessWorkflowController.cs:107,113,138` |
| `SkillValidationResult(Valid, Errors, Skill)` | `:17-18` | both controllers |
| `ISkillValidator` interface | `:26-31` | impl `BusinessWorkflows\WorkflowSkillValidator.cs:14`; injected into `SkillController.cs:32` **and** `BusinessWorkflowController.cs:17`; DI `Program.cs:137` |

**Confirms ledger row 54's flagged conflict is real and unresolved at HEAD.** 02-spec §3.4 ("Decided 2026-08-03: `SkillMetadata` as a union with `Kind` is removed; each side owns independent metadata types") resolves it: two independent records. The `Kind` default `"flow"` at `:14` is exactly the discriminator to delete; note `SkillMetadata.Kind` is consumed at `SkillController.cs:456` (`Kind: meta.Kind` when persisting an import) and cross-checked against `canonical_definition` at `WorkflowSkillPackageValidator.cs:102-105,130-136`.

---

#### 4. P3-1 / P3-2 contact points

##### 4.1 `MigrationManifest` definitions + test assertions

`c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\Migrations\MigrationManifest.cs`:

| Symbol | Line |
| --- | --- |
| `MigrationScript(Version, Name, Sql, Checksum)` | `:13` |
| `MigrationPostcondition(Name, Sql)` | `:19` |
| `ScriptFileName` regex `^(\d{4})_([a-z0-9_]+)\.sql$` | `:31` |
| `NonTransactional` blacklist regex | `:43-46` |
| `EnvironmentSubstitution` regex | `:52` |
| `SpringAITestLegacyObjects` (50 tables + 4 checkpoint tables = 54 entries) | `:60-80` |
| `BundleThroughVersion` property | `:106` (ctor assign `:91`) |
| `LegacyObjectAllowlist` / `AllowedExtensions` | `:112` / `:118` |
| `MaxVersion` | `:120` |
| **`Production`** static — `bundleThroughVersion: 0`, prefixes `Backend.Api.Data.Migrations.Sql.` / `.Postconditions.`, `allowedExtensions: ["vector"]` | `:126-132` |
| `FromAssembly(assembly, scriptPrefix, postconditionPrefix, bundleThroughVersion, legacyObjectAllowlist, allowedExtensions)` | `:138-193` (contiguity check `:169-177`) |
| `ReadCanonical` (CRLF→LF once) | `:195-202` |
| `ValidateSql` | `:207-221` |

**Consumers of `Production`:** `Data\Migrations\MigrationCommand.cs:36` (only production call site), doc-refs `DbMigrationRunner.cs:42,46`; runner batch logic `DbMigrationRunner.cs:148` (`TakeWhile(s => s.Version <= _manifest.BundleThroughVersion)`) and `:202` (`highestVersion > 0 && highestVersion >= _manifest.BundleThroughVersion` — postcondition gate).

**Tests that will flip when P3-1 registers `0001`–`0003` and sets `bundleThroughVersion: 3`:**

`c:\Users\a8022\Desktop\SpringAITest\backend\tests\Backend.Api.Tests\Migrations\ProductionManifestAbsenceTests.cs` — **all three facts invert**:

| Fact | Line | Assertion that breaks |
| --- | --- | --- |
| `P2_17_RegisteredProductionManifestContainsNoMigrationSql` | `:14-19` | `Assert.Empty(Production.Scripts)` `:16`; `Assert.Empty(Production.Postconditions)` `:17`; `Assert.Equal(0, Production.MaxVersion)` `:18` |
| `P2_17_ShippedAssemblyEmbedsNoHardResetOrProductionSqlResource` | `:22-30` | `Assert.DoesNotContain(resources, r => r.EndsWith(".sql"))` `:26`; and the three name filters `architecture_hard_reset` `:27`, `target_schema` `:28`, `constraints_and_indexes` `:29` — these three strings are effectively the mandated **file names** for `0001`/`0002`/`0003` |
| `P2_17_RunnerIsNotRegisteredInTheApplicationServiceProvider` | `:37-43` | `Assert.Null(GetService<DbMigrationRunner>())` `:41`, `Assert.Null(GetService<MigrationManifest>())` `:42` — **may survive** if P3 keeps the runner off the DI graph and startup calls it statically; decide explicitly |

Fixture-manifest builders (unaffected, they pass their own prefixes): `Migrations\MigrationFixtures.cs:43`, `Migrations\DbMigrationRunnerTests.cs:309`, `:412`. Fixture SQL sets already exist at `backend\tests\Backend.Api.Tests\Migrations\Fixtures\Sql\0001_fixture_hard_reset.sql`, `0002_fixture_target_schema.sql`, `0003_fixture_constraints.sql` and `Fixtures\BundleBoundarySql\0001..0003`, `Fixtures\Postconditions\target_tables_present.sql`, `Fixtures\FailingPostconditions\never_satisfied.sql`, plus negative fixtures `InvalidSql/`, `NonTransactionalSql/`, `TransactionControlSql/`.

Also relevant to P3-1: `Migrations\SchemaFingerprint.cs` + `SchemaFingerprint.sql` + `SchemaFingerprintTests.cs` (the drift-detection harness the 0002/0003 output must satisfy).

##### 4.2 `PostgresFixture` + `[Collection("Postgres")]` + `DbBootstrap.RunAsync` — **P0 numbers have drifted**

- `PostgresFixture` class: `c:\Users\a8022\Desktop\SpringAITest\backend\tests\Backend.Api.Tests\ConfigurationSetRepositoryTests.cs:16-…` (`InitializeAsync` `:30-46`, `DbBootstrap.RunAsync` call at **`:38`**, `ConnectionString` raw-string property `:28`, `SkipIfUnavailable` `:55`). Collection definition: `c:\Users\a8022\Desktop\SpringAITest\backend\tests\Backend.Api.Tests\PostgresCollection.cs:6`.

- `[Collection("Postgres")]`: **17 occurrences across 16 files** (P0 ledger §7 said 14 files). Delta = the two P2 files.

| File | Occurrences |
| --- | --- |
| `AgentRepositoryTests.cs` | 1 |
| `AgentRunRepositoryTests.cs` | 1 |
| `AuthRepositoryTests.cs` | 1 |
| `ConfigRepositoryTests.cs` | **2** (two test classes in one file) |
| `ConfigurationSetRepositoryTests.cs` | 1 |
| `ContextRepositoryTests.cs` | 1 |
| `EvalGovernancePostgresApiTests.cs` | 1 |
| `OperationsGovernancePostgresApiTests.cs` | 1 |
| `OrchestratorRepositoryTests.cs` | 1 |
| `OrchestratorRunRepositoryTests.cs` | 1 |
| `PromptArtifactsPostgresTests.cs` | 1 |
| `RagRepositoryTests.cs` | 1 |
| `RunEvidenceEnvelopePostgresApiTests.cs` | 1 |
| `SkillRepositoryTests.cs` | 1 |
| **`Migrations\DbMigrationRunnerTests.cs`** | 1 ← new in P2 |
| **`Migrations\SchemaFingerprintTests.cs`** | 1 ← new in P2 |

- `DbBootstrap.RunAsync`: grep reports **34 occurrences across 6 files**, of which **1 is a comment** (`Program.cs:24`, the P2 doc note). Real call sites = **33 across 6 files**:

| File | Call sites |
| --- | --- |
| `tests\…\SkillRepositoryTests.cs` | 14 |
| `tests\…\OrchestratorRepositoryTests.cs` | 9 |
| `tests\…\ConfigRepositoryTests.cs` | 4 |
| `tests\…\AgentRepositoryTests.cs` | 3 |
| `tests\…\ConfigurationSetRepositoryTests.cs` | 2 (incl. the shared fixture at `:38`) |
| `src\Backend.Api\Program.cs` | 1 (**`Program.cs:215`**, guarded by `!IsEnvironment("Testing") && !useInMemoryDb` at `:211`) |

Ledger §7 said "5 files call `DbBootstrap.RunAsync` directly at 32 sites … plus production Program.cs 1 site" — **32 test sites confirmed unchanged**; the file/collection count is what moved (14→16).

Also: `Program.cs:26-28` is the `migrate-db` command branch — `MigrationCommand.RunAsync(args)` + `Environment.Exit` before the host is built, so the runner is genuinely off the startup path today.

##### 4.3 `DbBootstrap` DDL full inventory — the input for `0002`/`0003`

All in `c:\Users\a8022\Desktop\SpringAITest\backend\src\Backend.Api\Data\DbBootstrap.cs`. `Ddl` raw string spans `:22-927`.

**Extensions (1):** `CREATE EXTENSION IF NOT EXISTS vector` `:23` — matches `MigrationManifest.Production` `allowedExtensions: ["vector"]`.

**CREATE TABLE (50, in DDL order):**

| # | Table | Line | # | Table | Line |
| --- | --- | --- | --- | --- | --- |
| 1 | `tenants` | `:24` | 26 | `operations_regression_result` | `:485` |
| 2 | `users` | `:28` | 27 | `operations_regression_override` | `:492` |
| 3 | `user_group_membership` | `:37` | 28 | `operations_release_audit` | `:499` |
| 4 | `conversations` | `:50` | 29 | `operations_execution_metric` | `:507` |
| 5 | `rag_documents` | `:58` | 30 | `operations_run_evidence` | `:524` |
| 6 | `rag_chunks` | `:63` | 31 | `orchestrator_run_event` | `:567` |
| 7 | `app_config` | `:75` | 32 | `orchestrator_run_command` | `:571` |
| 8 | `skill` | `:110` | 33 | `orchestrator_run_child` | `:598` |
| 9 | `skill_revision` | `:125` | 34 | `agent_run_skill` | `:686` |
| 10 | `configuration_set` | `:162` | 35 | `agent_run_event` | `:700` |
| 11 | `agent` | `:179` | 36 | `agent_run_command` | `:735` |
| 12 | `agent_revision` | `:201` | 37 | `context_policy` | `:800` |
| 13 | `agent_revision_skill` | `:227` | 38 | `source_catalog` | `:808` |
| 14 | `workflow` | `:237` | 39 | `metric_definition` | `:819` |
| 15 | `workflow_revision` | `:260` | 40 | `context_revision` | `:823` |
| 16 | `orchestrator` | `:278` | 41 | `context_evidence` | `:834` |
| 17 | `orchestrator_revision` | `:287` | 42 | `context_view` | `:841` |
| 18 | `agent_run` | `:333` | 43 | `context_request` | `:848` |
| 19 | `agent_run_approval` | `:408` | 44 | `context_delta` | `:857` |
| 20 | `agent_run_approval_decision` | `:419` | 45 | `eval_suite` | `:868` |
| 21 | `agent_run_write_effect` | `:425` | 46 | `eval_suite_revision` | `:873` |
| 22 | `agent_run_approval_execute` | `:431` | 47 | `eval_run` | `:887` |
| 23 | `agent_run_write_outbox` | `:441` | 48 | `eval_case_result` | `:897` |
| 24 | `orchestrator_run` | `:448` | 49 | `prompt_component_revision` | `:906` |
| 25 | `tenant_runtime_binding` | `:473` | 50 | `prompt_manifest_revision` | `:915` |

Count = 50, **exactly matching** `MigrationManifest.SpringAITestLegacyObjects[0..49]` (`MigrationManifest.cs:62-73`) and ledger §1.1. The 4 LangGraph checkpoint tables (`MigrationManifest.cs:79`) are created by workflow, not here.

**CREATE INDEX / CREATE UNIQUE INDEX (43):**
`:36` ux_users_id_tenant(U) · `:48` ix_user_group_membership_user · `:57` conversations_tenant_user_idx · `:68` rag_documents_tenant_idx · `:69` rag_chunks_tenant_idx · `:72` rag_chunks_embedding_hnsw_idx (HNSW/`vector_cosine_ops`) · `:123` ix_skill_tenant_enabled · `:134` ix_skill_revision_skill · `:172` uq_confset_active(U, partial `WHERE is_active`) · `:196` ix_agent_tenant_enabled · `:222` ix_agent_revision_agent · `:259` uq_workflow_tenant_name(U) · `:286` ix_orchestrator_tenant_enabled · `:295` ix_orchestrator_revision_orchestrator · `:402` ix_agent_run_tenant_owner_updated · `:418` ix_agent_run_approval_pending · `:436` ix_agent_run_approval_execute_recovery (partial) · `:445` ix_agent_run_write_outbox_pending (partial) · `:490` ix_operations_regression_result_tenant_latest · `:497` ix_operations_regression_override_gate · `:505` ix_operations_release_audit_tenant_time · `:513` ix_operations_execution_metric_tenant_time · `:557` ix_operations_run_evidence_tenant_time · `:564` uq_orchestrator_run_active_conversation(U, partial) · `:577` ix_orchestrator_run_command_reclaim (partial) · `:588` uq_orchestrator_run_command_start(U, partial) · `:590` uq_orchestrator_run_command_cancel(U, partial) · `:592` uq_orchestrator_run_command_resume_idempotency(U, partial) · `:595` ix_orchestrator_run_command_idempotency_lookup (partial) · `:605` ix_orchestrator_run_child_active · `:609` uq_orchestrator_run_child_agent_run(U, partial) · `:618` ix_agent_run_orchestrator_root (partial) · `:682` ix_agent_run_status_lease (partial) · `:716` uq_agent_run_event_cursor(U, partial) · `:791` uq_agent_run_deadline_cleanup(U, partial) · `:794` ix_agent_run_command_recovery (partial) · `:797` ix_agent_run_command_run_sequence · `:806` uq_context_policy_active(U, partial) · `:831` ix_context_revision_tenant_root · `:855` ix_context_request_root_child · `:879` ix_eval_suite_revision_suite · `:896` ix_eval_run_tenant_suite · `:920` ix_prompt_manifest_revision_sha

> ⚠️ None uses `CONCURRENTLY`, so all survive `MigrationManifest.ValidateSql`'s `NonTransactional` blacklist (`MigrationManifest.cs:43-46`) unchanged.

**ALTER TABLE — additive columns (grouped by statement):**
`:35` users.capabilities · `:55,56` conversations.tenant_id/user_id · `:62` rag_documents.status · `:91` app_config.tenant_id (inside DO block) · `:137` skill.package · `:138,139` skill_revision.package_sha256/package · `:140,141` skill.kind/skill_revision.kind · `:144` skill.simple_form · `:197-198` agent.draft_definition_canonical · `:223-224` agent_revision.canonical_definition · `:252-258` workflow ×7 (draft_definition_canonical, draft_ui_metadata_canonical, draft_definition_sha256, draft_ui_metadata_sha256, draft_validated_version, created_by, system_owned) · `:273-275` workflow_revision ×3 (status, definition_canonical, ui_metadata_canonical) · `:440` agent_run_write_effect.evidence · `:461-469` orchestrator_run ×9 · `:515` operations_execution_metric ×4 (skill_name, skill_revision, agent_id, agent_revision) · `:578-582` orchestrator_run_command ×5 · `:606-608` orchestrator_run_child ×3 (agent_run_id, command_id, task_envelope) · `:610` agent_run.orchestrator_root_run_id · `:620-627` agent_run ×7 · `:713-715` agent_run_event ×2 · `:758-768` agent_run_command ×10 · `:815-818` source_catalog ×4 · `:832-833` context_revision ×2 · `:924-926` agent_revision ×2 (prompt_manifest_revision, prompt_manifest_sha256)

**ALTER TABLE — constraint churn (DROP/ADD/VALIDATE) and PL/pgSQL blocks:**
`:85-103` `DO $app_config_tenant$` (pre-tenant→composite-PK backfill; the "never roll back" migration) · `:146-158` skill/skill_revision `kind` backfill+tighten · `:298-322` `DO $constraints$` — 3 NOT VALID FKs (`fk_agent_revision_runtime_workflow`, `fk_agent_revision_skill_agent_revision`, `fk_agent_revision_skill_skill_revision`) · `:326-328` 3× VALIDATE · `:406-407` agent_run status CHECK drop/add · `:438-439` agent_run_approval_execute status CHECK drop/add · `:470-471` orchestrator_run status CHECK drop/add · `:561-563` operations_run_evidence error_class CHECK drop/add · `:583-584` orchestrator_run_command command_type CHECK drop/add (adds `'resume'`) · `:587` drop `orchestrator_run_command_run_id_command_type_key` · `:611-617` `DO $orchestrator_child_lineage$` FK · `:628-634` agent_run.caller_role backfill + SET NOT NULL · `:635-658` `DO $agent_run_pinned_identity$` + VALIDATE · `:659-663` deadline_at backfill + SET NOT NULL · `:664-681` `DO $agent_run_fencing$` + VALIDATE · `:719-733` `DO $agent_run_event_fencing$` + VALIDATE · `:769-790` `DO $agent_run_command_type$` + VALIDATE

> Note for the `NonTransactional` regex: the blacklist deliberately excludes bare `BEGIN` (`MigrationManifest.cs:41`), so the 6 `DO $…$ BEGIN … END $…$` blocks above transfer to `.sql` files unmodified. `SAVEPOINT` **is** blacklisted (`:45`) — the row-level savepoint logic at `DbBootstrap.cs:1043-1056` is C#, not SQL, and is deleted with the file anyway.

**Imperative C# migrations (not in the `Ddl` string) that P3 must consciously drop, not port:**
- `MigrateAgentRunCommandInputHashesAsync` — `DbBootstrap.cs:961-1021` (backfills `agent_run_command.command_input_sha256`, then `SET NOT NULL` at `:1015-1019`)
- `MigrateSkillPackagesAndNamesAsync` — `DbBootstrap.cs:1028-1063` (+ `MigrateSkillRowAsync` `:1065-1192`), ledger §1 row 19 = delete

##### 4.4 `DbBootstrap.SeedAsync` full content

Entry: `DbBootstrap.cs:1218-1287`. Called from `RunAsync` at `:943` (after both C# migrations).

| Seed | Line | Content |
| --- | --- | --- |
| Tenants | `:1221-1228` | `demo-a`/`示範租戶 A`/`demo-a-invite`, `demo-b`/`示範租戶 B`/`demo-b-invite`; `ON CONFLICT (code) DO NOTHING` |
| Users | `:1231-1269` | `admin-a` ADMIN@demo-a caps `["workflow.manage"]`; `user-a` USER@demo-a caps `[]`; `user-b` USER@demo-b caps `[]`. Password constant `DefaultPassword = "password123"` at `:17`, hashed per-row via `BCrypt.Net.BCrypt.HashPassword` `:1264`; `ON CONFLICT (username) DO NOTHING` |
| Group memberships | `:1271-1282` | `(demo-a, admin-a, operations)`, `(demo-a, user-a, analysts)`, `(demo-b, user-b, analysts)`; `ON CONFLICT DO NOTHING` |
| → `SeedDefaultWorkflowAsync` | `:1284` → impl `:1329-1404` | System-owned `Default Agent-Runtime Workflow`: fixture pre-validated by `AgentDefaults.ValidateRuntimeWorkflowFixture()` `:1331-1336`; tenant-collision guard `:1351-1358`; `workflow` row upserted (mutable pointer/draft, full `DO UPDATE`) `:1361-1375`; `workflow_revision` row `INSERT … ON CONFLICT (workflow_id,revision) DO NOTHING` `:1377-1383`; then a **content-equality assertion** on the existing published revision (definition, canonical bytes, both SHAs, `compiler_contract_version`) `:1385-1401` that throws if a rev exists with different content. Hashes via `SkillHash.Sha256` `:1346-1347`. Own transaction `:1350`/`:1403`. |
| → `SeedContextEnrichmentAsync` | `:1285` → impl `:1312-1321` | Global `source_catalog` row `backend_documents` (`tenant_id NULL`, adapter `backend.retrieval_search`, required, 15s/2s) with `ON CONFLICT(source_id) DO UPDATE` `:1315-1317`; one `context_policy` named `default`, `is_active=true`, per tenant via `SELECT … FROM tenants`, `ON CONFLICT(tenant_id,name) DO UPDATE … WHERE created_by='system'` `:1318-1320`. Policy `values` JSON literal at `:1314` (readiness thresholds 0.85/0.70/0.10, one `document` bootstrap requirement, one `backend_documents` source requirement + precedence). |
| → `SeedEvalSuiteAsync` | `:1286` → impl `:1298-1310` | `AgentCanonicalizer.CanonicalizeDefinition(CsrEval001Suite.CasesJson)` `:1300`; per-tenant loop over `SELECT code FROM tenants` `:1301-1302`; publishes through the **real** write path `new EvalRepository(dataSource).PublishSuiteRevisionAsync(tenantCode, CsrEval001Suite.SuiteId, canonical, "system", ct)` `:1304-1309`. Suite content lives at `backend\src\Backend.Api\OperationsGovernance\CsrEval001Suite.cs`. |

02-spec §7's list of "everything `SeedAsync` produces today" is **complete and accurate** against HEAD — all six bullets map 1:1 to the rows above.

---

#### 5. P1 changes P3 must preserve when rebuilding these files

P1 tranche = commits `d33bf7b`…`828cfa6`; the substantive backend/platform commit is `d51e351` ("complete ApiError envelope, decouple dispatch from designer gate") plus `2f53f72` (InMemory cascade parity). Backend `src` files touched (19):

##### 5.1 Error envelope (must survive verbatim)

| File | Line | New behavior |
| --- | --- | --- |
| `backend\src\Backend.Api\Common\ApiError.cs` | `:8-14` | `ApiError` record gained **`Code`** and **`CorrelationId`** → shape is now `{ timestamp, status, code, message, correlationId, fieldErrors }` |
| same | `:20-38` | New `ApiErrorCodes.ForStatus(int)` — the single status→code table: 400 `validation_failed`, 401 `authentication_required`, 403 `forbidden`, 404 `not_found`, 409 `version_conflict`, 413 `payload_too_large`, 422 `unprocessable_entity`, 428 `precondition_required`, 429 `rate_limited`, 500 `internal_error`, 502 `upstream_unavailable`, fallback `internal_error`/`request_failed`. **Declared a byte-for-byte mirror of `platform/src/Platform.Web/Errors/ApiError.cs`** (comment at `:16-19`) |
| `Common\ApiErrorWriter.cs` | modified | writes the new fields |
| `Common\GlobalExceptionHandler.cs` | modified | populates code + correlation id |
| `Common\ValidationErrorResponse.cs` | modified | same envelope for 400 model-validation |

##### 5.2 Draft ETag on 409 (must survive verbatim)

New helper: `backend\src\Backend.Api\Common\ApiErrors.cs:22-36` — `VersionConflict(HttpContext, message, currentVersion)` returns an `ObjectResult` (**not** an exception) that sets the ETag header first. The doc comment at `:14-21` records the load-bearing reason: `UseExceptionHandler`'s `ClearCacheHeaders` wipes `ETag` on the exception path, so a conflict that must carry an ETag *cannot* go through `ApiException`. `ContentTypes = { "application/json" }` (no explicit charset — would cause 406).

ETag primitive: `backend\src\Backend.Api\Common\VersionEtags.cs:13` (`SetVersionETag`).

Repos/DTOs that now carry the current version out of the conflicting write (**and must keep doing so** after P3 rebuilds them):

| Contract | File:line |
| --- | --- |
| `AgentDraftResult(Status, Agent?, long? CurrentDraftVersion = null)` | `Agents\IAgentRepository.cs:108-109` (doc at `:32-34`) |
| `OrchestratorWriteResult(…, long? CurrentDraftVersion = null)` | `Orchestrators\OrchestratorDtos.cs:15-16` |
| `WorkflowWriteResult(…, long? CurrentDraftVersion = null)` | `Workflows\WorkflowDtos.cs:87-88` |

Producers: `Agents\AgentRepository.cs` (P1-modified), `Orchestrators\OrchestratorRepository.cs:15`, `Workflows\WorkflowRepository.cs:19`, `Data\InMemory\InMemoryAgentRepository.cs`, `Data\InMemory\InMemoryOrchestratorRepository.cs:14`, `Data\InMemory\InMemoryWorkflowRepository.cs:41` (`new WorkflowWriteResult(VersionConflict, CurrentDraftVersion: e.DraftVersion)`).

Consumers (controllers): `Agents\AgentController.cs:98-100` (conditional `when result.CurrentDraftVersion is long current`), `:136`, `:174`, `:209`, `:322` (`SetETag`), `:352` (`VersionConflict()` fallback without ETag), `:357` (`ApiErrors.VersionConflict(HttpContext, DraftConflictMessage, currentVersion)`); `Workflows\WorkflowController.cs:20,23,29,39`; `Orchestrators\OrchestratorController.cs:13,14,15,24`.

Already-existing ETag emitters not part of P1 but on the same header contract: `Contexts\ContextController.cs:15,28,36`, `OrchestratorRuns\OrchestratorRunController.cs:62,69,82`.

**P3 impact.** None of the P1 files are on the `skill`/`skill_revision` deletion path — `SkillController`/`BusinessWorkflowController`/`SkillRepository`/`InMemorySkillRepository` were untouched by P1 and today emit **no** ETag and **no** `If-Match`. So the risk is the reverse of what the task assumes: the new Agent Skill / Business Workflow controllers P3 writes must be authored **against the P1 envelope from the start** (`ApiErrorCodes.ForStatus`, `correlationId`, `fieldErrors` always present) — they inherit it automatically through `ApiException` → `GlobalExceptionHandler`, but any hand-built 409 must use `ApiErrors.VersionConflict`, not `throw`.

##### 5.3 Other P1 behavior in files P3 will touch

- `Program.cs` (backend `:12` lines changed) and `platform\src\Platform.Web\Program.cs:7` — `MULTI_AGENT_DISPATCH_ENABLED` decoupled from `WORKFLOW_DESIGNER_ENABLED` (ledger §4 row 100). P3 rebuilds of Skill DI registration (`Program.cs:91,112,137,155`) sit in the same file — do not revert the gate wiring while editing.
- `OrchestratorRuns\InMemoryOrchestratorRunRepository.cs` (+80/−…, `2f53f72`) — `CancelAsync` all-or-nothing child cascade parity with Dapper. That file also holds skill-pin code at `:61,:476`, so a P3 edit lands in the same file; preserve the cascade semantics.
- `platform\src\Platform.Web\Controllers\ChatOrchestratorController.cs:4` — bare `NotFound()` replaced by the envelope (ledger §4 row 95, now closed).

---

#### 6. Gaps / discrepancies against the ledger worth flagging before P3 starts

1. **`SkillHash` blast radius is ~2.3× the recorded number** — ledger §2 row 51 says "24 files across 10 modules"; HEAD has **56 files / 320 sites** (32 production files). Row 51's "move to a shared namespace" is a 56-file `using` churn, and it now includes `Data\Migrations\MigrationManifest.cs:4,165` (P2, didn't exist when the row was written).
2. **`PostgresFixture` fan-out is 16 files, not 14** — P2 added `Migrations\DbMigrationRunnerTests.cs` and `Migrations\SchemaFingerprintTests.cs`. Ledger §7's "37 test files green-by-skip" warning (02-spec §6.1) and its 14-file list both need refreshing. `DbBootstrap.RunAsync` direct sites are unchanged (32 test + 1 production).
3. **`operations_run_evidence.skill_name` is at `DbBootstrap.cs:532`, not `:531`** (ledger §1 row 23). `:509` and `:515` are still exact.
4. **`SkillMetadata` bidirectional pull (ledger row 54) is unresolved in code but resolved in spec** — 02-spec §3.4's "Decided 2026-08-03" says split into two independent metadata types with no `Kind`. Row 54 still reads "mark as P3 pre-spec decision pending"; the ledger row should be updated to point at §3.4 so the implementer doesn't re-litigate it.
5. **`ProductionManifestAbsenceTests.cs:27-29` effectively names the three P3-1 SQL files** (`architecture_hard_reset`, `target_schema`, `constraints_and_indexes`). Whatever P3-1 names `0001`/`0002`/`0003`, these three `DoesNotContain` filters must be inverted to `Contains` (or the whole fact replaced by a presence test) — otherwise the file names silently drift from the assertions that were supposed to guard them.
6. **`BusinessWorkflowController` has no `/validate` route today** despite 02-spec §3.1 declaring `/api/business-workflows/validate` the canonical replacement for the deleted `/api/skills/validate`. It is a **new** Backend route in P3, not a rename — same for `/api/business-workflows/{name}/revisions/{revision}/execution-artifact` (§3.2) and Business Workflow revisions/restore generally, which exist only on the Skill route at HEAD (`SkillController.cs:88,107,308`).
7. **`agent_run_skill.skill_id` has no FK to `skill(id)`** — only the composite FK to `skill_revision` (`DbBootstrap.cs:697-698`). A `0001` that drops `skill` before `skill_revision` will not be blocked by this table; the ordering constraint comes from `skill_revision.skill_id → skill(id)` at `:127` and `agent_revision_skill.skill_id → skill(id)` at `:230`.

---

## 5. workflow 拆分縫(P3-4 輸入)

### P3 Pre-Implementation Inventory — workflow/ @ b93b63c


---

#### 1. Routing layer — `c:\Users\a8022\Desktop\SpringAITest\workflow\app\main.py` (746 lines)

##### Current skill-related routes (post-P1 drift, refreshed)

| Route | Decorator | Handler | Notes |
|---|---|---|---|
| `GET /skills` | `main.py:525` | `list_skills` `main.py:526-547` | Unified catalog. builtin from `skills.all_skills()` (`:532-546`, `bindable=False`, carries `definition`), + `custom.catalog(ctx)` (`:547`). Emits `kind` per entry. |
| `POST /business-workflows/validate` | `main.py:550-554` | `validate_skill` `main.py:556-576` | **Already exists** — the §3.3 target route is landed as the *primary* decorator. |
| `POST /skills/validate` (alias) | `main.py:555` | same handler | Stacked second decorator on the same function. Ledger §3 row: delete route + alias tests. |
| `POST /skills/validate-package` | `main.py:579-583` | `validate_package` `main.py:584-621` | Multipart; `_read_package_upload` `main.py:132-156`; `_PackageUploadParser` `main.py:42-130`. Returns `PackageSkillMeta(kind=parsed.kind)` at `:610-616`. |
| `POST /skills/{name}/invoke` | `main.py:624` | `invoke_skill` `main.py:625-746` | kind branches at `:697` (agentic timeout grace) and `:705` (flow → `invoke_flow_with_governance`) / `:739-744` (else → agentic graph + `_public_agentic_output`). |

##### P1 artifacts that moved the lines
- `correlation.CorrelationIdMiddleware` added **last** (outermost) `main.py:375`, with the reasoning comment at `:373-374`.
- `_AGENTIC_DENY_KEYS = PUBLIC_DENY_KEYS - {"trace","fatal_error"}` `main.py:214`; `_public_agentic_output` `main.py:217-233`.
- `correlation.execution_failed(...)` call sites: `main.py:323` (`_run_with_timeout`, was ledger `168-175`), `main.py:660` (custom.load except, was ledger `508-512`), `app/evals/api.py:53` (was ledger `48-52`). **Ledger §3 last row's line refs are all stale.**
- Import of the shared deny list: `main.py:193` (`from app.runtime.flow_harness import PUBLIC_DENY_KEYS, invoke_flow_with_governance`).

##### Most natural landing points for the P3 target routes

| Target route | Natural landing | Rationale |
|---|---|---|
| `POST /business-workflows/validate` | already at `main.py:550-554` — **only delete the alias decorator `main.py:555`** and drop the "相容 alias" paragraph in the docstring (`:562-563`) | zero-move change |
| `POST /agent-skills/validate-package` | rename in place at `main.py:579-583` | handler body is already package-only (`package.parse_package`); the only kind coupling is echoing `parsed.kind` at `:615`, which becomes constant `"agentic"` or is dropped |
| `POST /agent-skills/{name}/invoke` | split out of `invoke_skill`, taking the `else` limb `main.py:739-744` + timeout rule `:697-699` + `_public_agentic_output` | needs: hidden-name guard `:639-640` (both internal names are *flows* → this guard belongs on the business-workflow route only), `config_apply.resolve` `:647`, role `:672`, input `:673`, state seed `:684-693` |
| `POST /business-workflows/{name}/invoke` | takes `main.py:705-738` verbatim + `:701` timeout | keeps `retrieval_top_k` seed `:692-693` and builtin recompile `:678-679` (builtins are all flows — see §5, so `:678-679` belongs *only* here) |
| `GET /agent-skills/catalog` | new handler beside `list_skills` | custom-only: builtin list contributes nothing (all 12 builtins are flows) |
| `GET /business-workflows/catalog` | new handler beside `list_skills` | takes `main.py:532-546` builtin branch wholesale + the flow subset of `custom.catalog` |

Shared helpers both invoke routes need (currently module-level, no move required): `_clean_skill_input` `:199-209`, `_require_role` `:236-245`, `_validate_input` `:272-296`, `_run_with_timeout` `:308-323`, `AGENT_INVOKE_TIMEOUT_GRACE_S` `:305`.

---

#### 2. The five kind-dispatch seams — current values + minimal P3-D3 shape

##### Seam 1 — `app\engine\compiler.py:502-505` (`_build_graph`)
```python
502 def _build_graph(skill: Skill, deps: Any) -> CompiledStateGraph:
504     if skill.kind == "agentic":
505         return agent_skill_graph.build(skill, deps)
507     audit_spec = skill_mod.resolve_node(AUDIT_NODE)
```
(Ledger cites `503-505`; actual `502-505`.) Note: the *other* ~30 `kind ==` hits in this file (`:78-101`, `:142-174`, `:329-330`, `:390-406`) are **step kinds** (`node`/`script`/`tool`/`sequence`/`branch`/`loop`), orthogonal to artifact kind — do not touch.

**Minimal shape:** two module-level entry points `compile_agent_skill(skill, deps)` → `agent_skill_graph.build(...)` and `compile_business_workflow(skill, deps)` → current `:507+` body; `_build_graph` disappears, but the memo cache (`compiler.compile`, `id(deps)` FIFO 32) must stay one shared cache or the eval/config-apply cache accounting changes. Cheapest: keep `compile()` signature, add a `builder` callable param defaulted per-module.

##### Seam 2 — `app\engine\package.py:962-968` (`parse_package` dispatch)
```python
962 # 分派：frontmatter metadata.kind == agentic → agentic；否則 flow
964 if isinstance(metadata, dict) and metadata.get("kind") == "agentic":
965     return _parse_agentic(entries, expected_name, sha256, meta, body, scripts, resources)
```
Pipelines: `_parse_agentic` **`:813-851`** (returns `ValidatedPackage(kind="agentic")` at `:844`), `_parse_flow` **`:853-907`** (`kind="flow"` at `:900`), entry `parse_package` **`:909-968`**. Shared prelude that both need: `_read_entries` `:425`, `_strip_single_root_folder` `:522`, `_split_frontmatter` `:560`, `_scan_package_files` `:784-812` (`:960`), `_validate_standard_frontmatter` `:605`. `ValidatedPackage.kind` field at `:127`. Agentic frontmatter guard `:690-694`, `_AGENTIC_METADATA_FIELDS` `:600-602`.

**Minimal shape:** promote `_parse_agentic` → `parse_agent_skill_package(raw, expected_name, author_role)` carrying `:940-960` prelude; the flow branch has **no P3 caller** (`/skills/validate-package` becomes `/agent-skills/validate-package`; Business Workflow import is YAML text, not zip) — check `test_package_parser.py` (1279 lines of flow-package tests) before deleting `_parse_flow`; it is likely a **delete**, not a split.

##### Seam 3 — `app\skills\custom.py` (189 lines)
- `_entry` **`:79-116`**, kind read from backend list at `:86`, branch at `:91-94` (agentic → `package.skill_from_agentic_meta`, else → `skill_mod.parse_source`).
- `load` **`:173-189`**, three-way dispatch at **`:184-189`**.
- Clean seams already present: `load_business_workflow` **`:148-157`**, `load_agent_skill` **`:160-170`**, shared `_compile_loaded` **`:119-145`** (hash verify `:122-127`, compile `:133`).
- Backend URL coupling: `catalog` `:72` `GET /api/skills`; `_entry` `:88` and `load` `:181` `GET /api/skills/{name}`.

**Minimal shape:** two modules `app/skills/agent_skills.py` + `app/skills/business_workflows.py`, each owning its `_fetch` base path and its own `catalog()`/`load()`; keep `_compile_loaded` + `singleton_deps` `:43-52` in a shared `custom_common` (or leave in `custom.py` as the shared base). The `kind` key in the returned catalog dict `:114` disappears once each catalog is single-typed — but `SkillInfo.kind` (`app\schemas.py:27`) is a response contract with a `"flow"` default, so removal is a Platform/Frontend-visible change.

##### Seam 4 — `app\runtime\graph.py` (1393 lines)
| Site | Lines | Current |
|---|---|---|
| `route_satisfied` | **`:552-564`** | `active_scope.get("kind") == pin.kind` at `:559` |
| `_after_policy` | `:568-582` | `kind` here is `RuntimeCommand.kind`, **not** artifact kind — leave alone |
| `_invoke_business_workflow` | **`:585-~660`** | flow execution via `invoke_pinned_flow` `:594` |
| `_enter_skill_scope` | **`:705-771`** | hardcodes `kind="agentic"` at `:722` |
| `_load_skill` | **`:774-788`** | `if artifact.kind == "flow": → _invoke_business_workflow` at `:786-787`, else `_enter_skill_scope` `:788` |
| `_artifact` | **`:1132-1152`** | pin-match assert includes `artifact.kind != pin.kind` at `:1146` |
| `_active_artifact` | `:1155-1170` | no kind check (already scope-typed) |
| `_proposed_action` | **`:1173-1213`** | `skill_kind` from `scope.get("kind")` `:1181`, from `pin.kind` `:1187`, threaded into 3 `ProposedAction`s at `:1195/:1204/:1211` |
| snapshot preflight | `:1367` | `definition.get("kind") != "agent-runtime"` — **Graph IR**, orthogonal, leave |

**Minimal shape:** `snapshot.skill_pin(name)` (`app\runtime\models.py:306-307`) becomes two lookups — `agent_skill_pin(name)` / `business_workflow_pin(name)`. `_load_skill:774-788` then dispatches on *which lookup hit* instead of `artifact.kind`, deleting `:786`. `_enter_skill_scope:722` drops the field. `_artifact:1146` drops that clause (reader is already per-domain — see §3). `_proposed_action` keeps `skill_kind` only if `ProposedAction`/`PreActionPolicy` (`app\runtime\policy.py`) still consume it — otherwise it collapses to a constant per branch. `route_satisfied:559` drops the kind term.

##### Seam 5 — `app\runtime\models.py` (482 lines)
- `PinnedSkillSummary` **`:149-171`**, `kind: Literal["agentic","flow"]` at **`:152`**; validators `:159-171`.
- `DirectAgentExecutionSnapshot.skills` **`:229`** (`max_length=128`); uniqueness validator `:252-256` (`keys = [(skill.name, skill.revision) ...]`); `skill_pin` **`:306-307`**.
- `ActiveSkillScope` **`:429-437`**, `kind` at **`:432`**; `package_sha256`/`instruction_sha256`/`resource_paths` (`:434-437`) are agentic-only fields already.
- `RuntimeState.active_skill_scope` `:449`.
- (`execution_kind` `:232-234`, `RuntimeCommand.kind` `:416` — unrelated, do not split.)

**Minimal shape per P3-D3:** `PinnedAgentSkillSummary` (keeps `package_sha256`) + `PinnedBusinessWorkflowSummary` (drops it, `definition_sha256` only); snapshot gets `agent_skills: list[...]` + `business_workflows: list[...]` replacing `:229`; the `:252-256` uniqueness validator runs twice (and should additionally reject the same name in both collections, since `_load_skill` routes by name). `ActiveSkillScope` becomes agent-skill-only (a flow never opens a scope — `_load_skill:787` returns immediately), so **the lazy split is: rename `ActiveSkillScope` → `ActiveAgentSkillScope` and delete `:432`; no second variant is needed.**

---

#### 3. Artifact readers hitting backend `/api/skills*`

| Call site | Line | Current URL | P3 |
|---|---|---|---|
| `app\runtime\artifacts.py` `RevisionArtifactReader.read` `:212-233` | **`:221`** | `/api/skills/{name}/revisions/{pin.revision}/execution-artifact` | splits into `/api/agent-skills/...` and `/api/business-workflows/...` per §3.2; reader becomes two classes, injected via `RuntimeGraphContext.artifact_reader` |
| `app\skills\package_reader.py` `BackendPackageReader.read` `:25-43` | **`:28`** | `/api/skills/{name}/package` | → `/api/agent-skills/{name}/package`. Agentic-only already (`:41-42` rejects non-agentic). Docstring URL at `:4` also needs updating. |
| `app\skills\custom.py` | `:72`, `:88`, `:181` | `/api/skills`, `/api/skills/{name}` | see seam 3 |
| `app\engine\agent_skill_graph.py` | — | **no backend HTTP** | it resolves `deps.agent_package_reader` at `:35-38`; the ledger row's inclusion of this file is about the *port*, not a call site. |
| `app\runtime\backend.py:134` | — | `/api/agent-runs/...` | unrelated |

Also kind-coupled in `artifacts.py`: `verify_and_load_artifact` flow branch returning `kind=pin.kind` at **`:180-191`**, `_flow_contains_script` **`:194-209`**, `read_artifact_resource` guard `if artifact.kind != "agentic"` at **`:236-238`**. After the split, `:237` becomes structurally impossible (business-workflow artifacts are a different type) — delete rather than keep as a runtime guard.

---

#### 4. Evals

- `EvalCandidate` `app\evals\models.py:33-45`; `kind: Literal["skill","agent"]` at **`:36`**; `ref: dict[str,Any]` at `:37` (untyped — Workflow parses `ref["name"]` by convention, documented `models.py:3-4`).
- Rejections in `app\evals\api.py`:
  - **`:95-102`** — `req.candidate.kind != "skill"` → 422 `workflow_eval_unsupported_candidate` (ledger's "kind rejection" — *not* at `69-79`).
  - **`:70-80`** — `loaded.skill.kind != "flow"` → 422, same error code. This is the *artifact* kind rejection and is the one that P3 actually removes/re-shapes.
- Candidate resolution `_resolve_candidate_skill` **`:30-81`**; hidden internal names `:27`, `:44-45`; builtin-then-custom lookup `:47-50` (mirrors `main.py:649-657`).
- Impact surface:
  - **models.py** — replace `kind` + untyped `ref` with an explicit discriminated candidate (`business_workflow` / `agent_skill`), 1 field + 1 typed ref model.
  - **api.py** — `:95-102` becomes "agent-skill candidate not supported yet" and `:70-80` disappears (the resolver is already type-specific).
  - **runner.py** (`app\evals\runner.py`) — takes `skill: Skill` at `:66` and `:137`, compiles via `compiler.compile(skill, deps, cache=False)` at `:89`. If §2 seam 1 splits `compile`, this call site changes to `compile_business_workflow`. Otherwise untouched.
  - **fixtures.py** — `build_fixture_deps` leaves `agent_package_reader`/`agent_chat_model` at dataclass default `None` (documented `fixtures.py:92`), which is exactly why `api.py:70-80` exists. Nothing to change unless agent-skill evals ship in P3.
  - Tests: `tests\test_evals_api.py` (464 lines, 24 tests).

---

#### 5. The 12 builtin YAML artifacts

Location: `c:\Users\a8022\Desktop\SpringAITest\workflow\app\skills\*.yaml` — `analyze-report`, `context-enrichment`, `context-task-local`, `kb-query`, `rag-qa`, `summarize`, `template-compare`, `template-infer`, `template-inspire`, `template-retrieval`, `template-stats`, `triage`. **Confirmed: none declares `kind:` at top level** (grep `^kind:` = 0 hits) → all 12 default to `flow` → all 12 are Business Workflows, zero builtin Agent Skills. Ledger §3 row is accurate.

Loader: `c:\Users\a8022\Desktop\SpringAITest\workflow\app\skills\__init__.py` (134 lines)
- `_SKILL_DIR = Path(__file__).parent` **`:51`** — the directory is derived from the *module* location, so a move to `artifacts/` is a one-line change **plus** the `@node`-registration import block that must move with it.
- `_load_builtin()` **`:103-117`**, `glob("*.yaml")` at `:104`, eager compile at `:111`, module-import-time call at **`:133`**.
- `_TEMPLATE_NAMES` `:57-63`, `_NODE_FIRST_MIGRATION_NAMES` `:69`, `_INTERNAL_SKILL_NAMES` `:72` (hides `context-enrichment`/`context-task-local` from catalog; mirrored by `main.py:639-640` and `evals/api.py:27`), `_DEPS_BUILDERS` `:75-81`.
- Registration side-effect imports that must not break: `:26` (`app.tools`), `:29-31` (kbquery + context_enrichment nodes), `:37-38` (`nl_logic`, `retrieve` — comment at `:33-36` states removing either → cold-start compile failure), `:42-45` (four Node-First node modules), `:49` (`agent_skill_runner`).
- `LoadedSkill` dataclass `:84-97` — `source="builtin"` default `:93`, `definition`/`definition_sha256` `:96-97` (used by `GET /skills` `main.py:544` for frontend template patching).

**Move + P3-D2 接點:** 03-design §5 puts loaders under `artifacts/`. Lazy shape: `app/artifacts/business_workflows/*.yaml` + `app/artifacts/business_workflows/__init__.py` = current `__init__.py` verbatim; `app/artifacts/agent_skills/` starts empty (no builtin agent skills). `GET /business-workflows/catalog` = current `main.py:532-546` builtin branch (unchanged, still `bindable=False`) unioned with the custom flow catalog — P3-D2 "含 builtin" is satisfied by keeping `list_skills`'s builtin limb intact and only re-pointing the import. `GET /agent-skills/catalog` never touches this module. Consumers of `app.skills.get/all_skills` to re-point: `main.py:545/:649/:668`, `evals/api.py:47`, `tests\test_skill_kbquery_parity_e2e.py` (imports `app.skills` directly — see the `__init__.py:33-36` self-sufficiency comment).

Also `app/skills/` currently mixes YAML with `custom.py`, `deps.py`, `config_apply.py`, `package_reader.py` — the move is an opportunity but each of those has its own import fan-out; `deps.py` (`KbQueryDeps`/`_default_deps`) is imported from `__init__.py:29`, `custom.py:26`, `evals/fixtures.py:33`.

---

#### 6. `flow_harness` — the shared points P3 must not break

`c:\Users\a8022\Desktop\SpringAITest\workflow\app\runtime\flow_harness.py` (395 lines)

- **`PUBLIC_DENY_KEYS`** defined **`:48-58`** (= `RESERVED_KEYS | ENGINE_KEYS | RUNTIME_AUTHORITY_KEYS | {audit_trail, issue_label, regression_test_item, improvement_backlog}`). Consumers: `_public_flow_output` **`:102-107`** (flow path) and `main.py:193` → `main.py:214` → `_public_agentic_output` `main.py:217-233` (agentic path, subtracting `{trace, fatal_error}`).
  **Keeping it across the split:** the constant lives in the *flow* module but is consumed by the *agentic* route. When `invoke_skill` splits into two handlers, the agentic handler still needs this import. Cheapest preservation: leave `PUBLIC_DENY_KEYS` where it is and have the new agent-skill route module import it (same as today) — do **not** duplicate the set. If flow_harness later moves to `runtime/flow/` per 03-design §5, the deny-key set is the one symbol that must be re-exported or hoisted to a neutral module (`runtime/output_contract.py` already exists and is a candidate, but hoisting is P4 work, not P3).
- **kb-query import (P4, do not break in P3):** `flow_harness.py:18` — `from app.nodes.kbquery.nodes import audit_feedback as _audit_feedback  # noqa: F401`. Ledger §3 row: P4 moves this to the composition root. If P3 splits files, this import must travel with `flow_harness` unchanged.
- **`compiler._script_contract` private dependency (P4):** `flow_harness.py:372` — `denied = set(compiler._script_contract(body).tools) - effective_tools`. Definition side is `compiler.py:85` / `:163-169` usage. Do not "clean this up" while splitting compiler in seam 1 — if `_script_contract` gets renamed/moved during the `compile_business_workflow` split, `:372` breaks silently at runtime (it is inside a governance denial path, i.e. a fail-open risk if it throws in the wrong place).
- Other flow_harness entry points P3 touches indirectly: `invoke_flow_with_governance` (called `main.py:706`), `invoke_pinned_flow` (called `graph.py:594`). Both stay flow-only after the split — no signature change needed.

---

#### 7. Test blast radius

##### 7a. `tests\test_agent_runtime.py` helpers (1908 lines, 31 tests)
Exported helpers: `snapshot` **`:234`**, `FakeModel` **`:337`**, `FakeArtifactReader` **`:957`**, `request_context` **`:991`**.

Five importer files:

| Importer | Line | Imports | Tests |
|---|---|---|---|
| `tests\test_agent_runtime_manager.py` | `:55-60` | `FakeArtifactReader, FakeModel, request_context, snapshot` | 39 |
| `tests\test_agent_runtime_flow.py` | `:26-31` | `FakeArtifactReader, FakeModel, request_context, snapshot` | 19 |
| `tests\test_agent_runtime_api.py` | `:16` | `snapshot` | 8 |
| `tests\test_d7_write_evidence.py` | `:28` | `snapshot as agent_snapshot` | 16 |
| `tests\test_prompt_manifest_assembler.py` | `:29` | `FakeArtifactReader, snapshot as base_snapshot` | 16 |

`snapshot()` builds `DirectAgentExecutionSnapshot`, so the §2 seam-5 field rename (`skills` → `agentSkills`/`businessWorkflows`) hits all five files transitively — **~98 downstream tests + 31 in-file = ~129 tests** behind one helper signature. `FakeArtifactReader` returns `LoadedSkillArtifact` and is the other split point (per-domain readers). `test_agent_runtime_flow.py:34+` defines a local `flow_artifact(...)` builder — the business-workflow pin path's main fixture.

##### 7b. `tests\test_skills_custom.py`
**668 lines, 27 tests.** Structure: `_FakeResponse` `:76`, `FakeSkillBackend` `:93` (serves `/api/skills` `:111` and `/api/skills/{name}` `:114`), `row()` `:136`, fixtures `fake_deps` `:157-163` / `backend` `:165-178`. Test groups: catalog/bindable `:180-318` (9), invoke `:320-543` (13, incl. `test_invoke_custom_skill_with_unrecognized_kind_fails_closed` `:464` and `test_custom_catalog_kind_does_not_sniff_definition` `:250` — both directly encode the seam being deleted), validate `:545-668` (5). TestClient `/skills*` calls: 14. Backend-fake URLs to re-path: `:111`, `:114`.

##### 7c. Test files calling `/skills/*` through TestClient

**Route-invoking (must be re-pathed at P3):**
| File | `/skills` call sites | Lines |
|---|---|---|
| `tests\test_skills_api.py` (483 L, 21 tests) | 9 | `GET /skills` `:40,54,61,62,68,75,142,149`; `POST /skills/validate` `:103` |
| `tests\test_skills_custom.py` (668 L, 27) | 14 | `:184,211,221,242,264,274,284,309,528,529,536,661,663,664` |
| `tests\test_config_apply.py` | 5 | `POST /skills/cfg-qa/invoke` `:527,528,534,552,553` |
| `tests\test_skill_templates.py` | 2 | `GET /skills` `:146,173` |
| `tests\test_validate_package_api.py` (532 L, 24) | 6 | `POST /skills/validate-package` `:50,94,118,144,150` (+`:478,495` prose) |
| `tests\test_engine_guardrails.py` | 4 | `/skills/validate` `:199,217,310`; `/skills/validate-package` `:239` |
| `tests\test_skill_rag_qa.py` | 6 | `:182,206,222,233,253,274` |
| `tests\test_skill_analyze_report.py` | 5 | `:129,155,182,200,212` |
| `tests\test_skill_triage.py` | 4 | `:153,176,202,215` |
| `tests\test_skill_summarize.py` | 3 | `:79,96,+1` |
| `tests\test_agent_skill_runner.py` | 2 | `POST /skills/sales-helper/invoke` `:261,522` (+backend fake `/api/skills/sales-helper` `:229`) |
| `tests\test_correlation_id.py` | 3 | `:63,156,205` (incl. agentic 500 path `:205`) |
| `tests\test_context_enrichment.py` | 2 | `:688,702` |
| `tests\test_context_task_local.py` | 1 | `:468` |
| `tests\test_config_retrieval_topk.py` | 1 | `:143` |
| `tests\test_input_engine_key_stripping.py` | 2 | `:80,118` |
| `tests\test_nl_logic.py` | 1 | `POST /skills/validate` `:159` |
| `tests\test_nl_extract.py` | 1 | `POST /skills/validate` `:222` |

**Total: 18 files, ~61 TestClient call sites on `/skills*`.** Of these, the invoke calls split by target: `sales-helper` (`test_agent_skill_runner`) is the only **agent-skill** invoke — every other invoke test targets a builtin or custom **flow** and moves to `/business-workflows/{name}/invoke`.

**Prose-only / comment references (no code change, but stale after rename):** `test_evals_api.py:327`, `test_engine_compiler.py:801`, `test_package_parser.py:1279`, `test_script_runner_sandbox.py:85,135`, `workflow\README.md:155,157`, `workflow\AGENTS.md` (APIs section + several Gotchas bullets name `/skills/validate`, `/skills/{name}/invoke`, `GET /skills`).

---

#### Deltas vs. the ledger worth recording before P3 starts

1. Ledger §3 row "Workflow exception exposure" cites `main.py:168-175` / `main.py:508-512` / `evals/api.py:48-52` — actual post-P1: `main.py:308-323`, `main.py:658-660`, `evals/api.py:51-53`. That row is **done** (P1-05), lines are stale.
2. `compiler._build_graph` is at `:502-505`, not `:503-505`.
3. `EvalCandidate` kind rejection is at `api.py:95-102`, not `api.py:69-79`; the `69-79` range now holds the *artifact*-kind rejection (`api.py:70-80`).
4. `POST /business-workflows/validate` **already exists** (`main.py:550-554`) — §3.3 route 3 is partially landed; P3 only deletes the alias decorator at `main.py:555`.
5. `ActiveSkillScope` does not need two variants (a flow never enters a scope, `graph.py:787` returns before `_enter_skill_scope`) — one rename + one field deletion satisfies P3-D3 for that type. 02-spec §2.3 says all three types "are partitioned into two variants"; the scope type is a genuine exception.
6. `engine/package.py` `_parse_flow` (`:853-907`) has **no P3 consumer** after `/skills/validate-package` → `/agent-skills/validate-package`. It is probably a delete rather than a split; `tests\test_package_parser.py` needs a consumer audit first.

---

## 6. platform 拆分(P3-5 輸入)

#### 1. SkillDtos union 全景

##### 1.1 record 清單與消費者

檔案:`c:\Users\a8022\Desktop\SpringAITest\platform\src\Platform.Service\Dtos\SkillDtos.cs`

| record | 定義行 | 消費者(檔案:行) |
|---|---|---|
| `Skill`(10 欄,含 `[JsonRequired] Kind` :23-25、`SimpleForm` :26-28) | :14-28 | `SkillService.cs:105,108,112-123`(Create/Update 回應)、`BusinessWorkflowService.cs:33-45,48-51,80-90,92-98`、`ISkillService.cs:58,61`、`IBusinessWorkflowService.cs:13`、`SkillDtos.cs:52`(被 `BusinessWorkflowCreated` 包住)、`SkillController.cs:156`(`ActionResult<Skill>`)、`BusinessWorkflowController.cs:44`、`Platform.Web.Tests\Fakes.cs:366-372,374-384,568-587,588-595` |
| `SkillUpsert`(`Definition` + `SimpleForm`) | :35-43 | `SkillController.cs:149`(Create)、`:157`(Update)、`:171`(Validate)、`BusinessWorkflowController.cs:35,45,64`、`ISkillService.cs:58,61`、`IBusinessWorkflowService.cs:12,13`、`SkillService.cs:105,109`、`BusinessWorkflowService.cs:24,49`、`Platform.Service.Tests\SkillServiceTests.cs:25,151`、`BusinessWorkflowServiceTests.cs:132,155,169,189` |
| `SkillExport`(byte[]+ContentType+FileName) | :49 | `SkillService.cs:35,51-65`、`BusinessWorkflowService.cs:60-75`、`SkillController.cs:54-59`、`BusinessWorkflowController.cs:55-60`、`Platform.Web.Tests\Fakes.cs:392,520` |
| `BusinessWorkflowCreated`(Skill + Location) | :52 | **BW 專屬**:`IBusinessWorkflowService.cs:11`、`BusinessWorkflowService.cs:23-46`、`BusinessWorkflowController.cs:34-41`、`Platform.Web.Tests\Fakes.cs:361-372` |

`WorkflowInvokeRequest`(invoke body)不在 SkillDtos,住 `Platform.Service\Dtos\WorkflowDtos.cs:8-10`,由 `SkillController.cs:177` 消費。

##### 1.2 SkillService 全部方法(`SkillService.cs`,介面 `ISkillService.cs`)

| 方法 | impl 行 | 介面行 | 下游 path | P3 歸屬 |
|---|---|---|---|---|
| `ListAsync` | :32-34 | :17 | GET `/api/skills` | Agent Skill |
| `GetAsync` | :36-38 | :20 | GET `/api/skills/{name}` | Agent Skill |
| `GetRevisionsAsync` | :40-42 | :23 | GET `/api/skills/{name}/revisions` | Agent Skill |
| `RestoreRevisionAsync` | :44-49 | :28-29 | POST `.../revisions/{rev}/restore` | Agent Skill |
| `ExportAsync` | :51-65 | :35 | GET `/api/skills/{name}/export` | Agent Skill |
| `ImportAsync(name,…)` | :67-70 | :47-48 | POST `/api/skills/{name}/import` | Agent Skill |
| `ImportAsync(bytes,…)` | :72-74 | :54-55 | POST `/api/skills/import` | Agent Skill |
| `ImportCoreAsync`(private) | :76-97 | — | — | 保留 |
| `DeleteAsync` | :99-101 | :64 | DELETE `/api/skills/{name}` | Agent Skill |
| **`CreateAsync`** | :105-106 | :58 | POST `/api/skills` | **P3 刪**(flow 相容窗口) |
| **`UpdateAsync`** | :108-110 | :61 | PUT `/api/skills/{name}` | **P3 刪** |
| `ReadSkillAsync`(private) | :112-123 | — | — | 隨 Create/Update 一起死 |

##### 1.3 BusinessWorkflowService 全部方法

| 方法 | impl 行 | 介面行 | 下游 path |
|---|---|---|---|
| `ListAsync` | :17-18 | :9 | GET `/api/business-workflows` |
| `GetAsync` | :20-21 | :10 | GET `/api/business-workflows/{name}` |
| `CreateAsync` | :23-46 | :11-12 | POST `/api/business-workflows`(取 Location header) |
| `UpdateAsync` | :48-51 | :13 | PUT `/api/business-workflows/{name}` |
| `DeleteAsync` | :53-58 | :14 | DELETE |
| `ExportAsync` | :60-75 | :15 | GET `.../export` |
| `ReadJsonAsync` / `ReadSkillAsync` / `EnsureFlowKind`(private) | :77-78 / :80-90 / :92-98 | — | — |

##### 1.4 kind assertion 點(全部三處)

1. `SkillDtos.cs:23-25` — `Kind` 上的 `[JsonRequired]`,反序列化層強制存在(union 判別欄位本體)。
2. `SkillService.cs:117-120` — `if (skill.Kind is not ("flow" or "agentic")) throw WorkflowInvocationException(…"回應包含無效的 kind")` → 對外 502。呼叫點:`:106`(Create)、`:110`(Update)。
3. `BusinessWorkflowService.cs:92-98` — `EnsureFlowKind`:`if (workflow.Kind != "flow") throw …"回應包含非 flow 的 kind"` → 502。呼叫點:`:44`(CreateAsync)、`:88`(ReadSkillAsync ← UpdateAsync)。

`SkillRoutingAgent` 全檔**不讀 kind**(catalog 只讀 `name`/`source`/`required_role`/`input_schema`/`description`,見 `SkillRoutingAgent.cs:385-423`),故 P3 拆 record family 不觸及它的解析邏輯。

##### 1.5 P3-D1 + P3-D4 的最小拆分形狀

依 `plans\architecture-hard-reset\06-todo.md:71`(D1:`simple_form` 只留 BW)、`:74`(D4:`SkillMetadata` union 刪除)、`05-…ledger.md:101`(「Split into two record family」):

```
AgentSkill      : Name, Description, RequiredRole, Enabled, CurrentRevision, CreatedAt, UpdatedAt
                  ── 無 Kind、無 SimpleForm、無 Definition(package 才是事實來源;
                     現行 Definition 僅 flow 寫回應在用)
BusinessWorkflow: Name, Description, Definition, RequiredRole, Enabled, CurrentRevision,
                  CreatedAt, UpdatedAt, SimpleForm?      ── 無 Kind
BusinessWorkflowUpsert(Definition + SimpleForm)          ← 取代 SkillUpsert 的 BW 用法
BusinessWorkflowCreated(BusinessWorkflow, Location)      ← 只換型別參數
SkillExport                                              ← 兩側共用,不含 kind/simple_form,不拆
```

連帶最小刪除面(全部只在 union 存在時才需要):
- `SkillUpsert` 整個 record(:35-43)— Agent Skill 面 P3 後無 definition-only 寫入(`02-spec.md:98-112`)。
- `SkillService.ReadSkillAsync`(:112-123)含 kind 斷言 → 隨 Create/Update 刪。
- `BusinessWorkflowService.EnsureFlowKind`(:92-98)→ 型別即保證,刪斷言。
- `Skill.Kind` 的 `[JsonRequired]`(:23-25)→ 兩個 family 都不再有此欄。

---

#### 2. IWorkflowEngineClient / WorkflowEngineClient

介面 `Platform.Service\Abstractions\IWorkflowEngineClient.cs`(10 法),impl `Platform.Service\WorkflowEngineClient.cs`。

##### 2.1 union 語意的方法(P3 要動的)

| 方法 | 介面行 | impl 行 | 引擎 path | 呼叫者 |
|---|---|---|---|---|
| `InvokeSkillAsync` | :16-17 | :39-51 | POST `{base}/skills/{name}/invoke` | `SkillController.cs:178`(公開 invoke)、`SkillRoutingAgent.cs:493`(路由執行)、`SkillRoutingAgent.cs:501`(kb-query→rag-qa 兜底) |
| `ValidateSkillAsync` | :21 | :54-56 → `ValidateDefinitionAsync("/skills/validate")` | POST `{base}/skills/validate` | **僅** `SkillController.cs:172`(alias) |
| `GetSkillCatalogAsync` | :28 | :58-59 → `GetCatalogAsync("/skills")` | GET `{base}/skills` | `SkillController.cs:46`(`/api/skills/catalog`)、`SkillRoutingAgent.cs:358` |
| `ValidateBusinessWorkflowAsync` | :24-25 | :144-146 | POST `{base}/business-workflows/validate` | `BusinessWorkflowController.cs:65` |

非 union、P3 不動:`GetNodeCatalogAsync`(:31 / impl :61-62,呼叫者 `NodeController.cs:25`)、`GetToolCatalogAsync`(:37 / :64-65,`ToolController.cs:25`)、`GetBusinessRuleFactsAsync`(:40 / :67-68)、`GetBusinessRuleActionsAsync`(:43 / :70-71)、`ValidateBusinessRulesAsync`(:46-47 / :73-79)、`SimulateBusinessRulesAsync`(:50-51 / :81-87)。

共用私有基礎設施(兩側都會沿用,不需複製):`BuildRequest` :30-31、`SendAsync` :34-36、`ValidateDefinitionAsync` :152-163、`GetCatalogAsync` :166-177、`MapInvokeErrorAsync` :180-188(參數 `kind` 是**顯示字串**不是 union 判別,可原封改傳 `"Agent Skill"`/`"Business Workflow"`)、`MapBadInputAsync` :197-240、`ReadJsonAsync` :243-255。

##### 2.2 P3 後的目標方法面(依 `02-spec.md:113-120` §3.3)

```
InvokeAgentSkillAsync(name, input, ctx)        → POST /agent-skills/{name}/invoke
GetAgentSkillCatalogAsync(ctx)                 → GET  /agent-skills            (只含 Agent Skill)
InvokeBusinessWorkflowAsync(name, input, ctx)  → POST /business-workflows/{name}/invoke   ← 新增
GetBusinessWorkflowCatalogAsync(ctx)           → GET  /business-workflows      ← 新增(含 builtin YAML template)
ValidateBusinessWorkflowAsync(definition, ctx) → POST /business-workflows/validate        ← 已存在,不動
── 刪除:ValidateSkillAsync(union alias)
```
`/agent-skills/validate-package` 是 Backend↔Workflow 內部路徑(`02-spec.md:120`),platform 不代理,故不進此介面。

---

#### 3. SkillController(`Platform.Web\Controllers\SkillController.cs`)路由現況與 P3 處置

| # | 路由 | 行 | 下游 | P3 處置 |
|---|---|---|---|---|
| 1 | `GET /api/skills` | :36-38 | ISkillService.List | **留**(語意收斂為 Agent-Skill-only,由 backend 保證) |
| 2 | `GET /api/skills/catalog` | :44-46 | engine `GET /skills` | **改**:改打 `GetAgentSkillCatalogAsync` → `/agent-skills`;不再合併 flow(`02-spec.md:96`) |
| 3 | `GET /api/skills/{name}` | :49-51 | ISkillService.Get | 留 |
| 4 | `GET /api/skills/{name}/export` | :54-59 | ISkillService.Export | 留(回傳型別 `SkillExport` 不變) |
| 5 | `GET /api/skills/{name}/revisions` | :62-64 | ISkillService.GetRevisions | 留 |
| 6 | `POST /api/skills/{name}/revisions/{revision:int}/restore` | :67-70 | ISkillService.RestoreRevision | 留 |
| 7 | `POST /api/skills/{name}/import` | :79-87 | ISkillService.Import(name) | 留(含 `[AdminOnly]`/`[PackageTooLarge]`/size limits :80-83) |
| 8 | `POST /api/skills/import` | :90-98 | ISkillService.Import(derived) | 留(:91-94 同組 filter) |
| 9 | **`POST /api/skills`(Create)** | :148-152 | ISkillService.Create | **刪**(相容窗口;`05-…ledger.md`(skill-concept)§3.1 已列 P5,hard-reset 提前到 P3) |
| 10 | **`PUT /api/skills/{name}`(Update)** | :155-158 | ISkillService.Update | **刪** |
| 11 | `DELETE /api/skills/{name}` | :161-166 | ISkillService.Delete | 留 |
| 12 | **`POST /api/skills/validate`(alias)** | :169-172 | engine `ValidateSkillAsync` | **刪**(`05-…ledger.md:86`「Delete endpoint and tests」;`02-spec.md:98`) |
| 13 | `POST /api/skills/{name}/invoke` | :175-178 | engine `InvokeSkillAsync` | **改**:retarget 到 `InvokeAgentSkillAsync`;`02-spec.md:98`「never accepts or resolves a Business Workflow」 |

附屬程式:`PackageTooLargeAttribute`(:111-126)、`PackageSizeLimitBytes`(:100)、`ReadPackageAsync`(:128-140)、`PackageFileName`(:142-145)全部隨 import 保留。類別 XML doc `:18-19` 明文提到「catalog、validate 字面段勝過 {name}」,刪 validate 後需同批改文件。

---

#### 4. BusinessWorkflowController 缺口(vs `02-spec.md` §3.1 + ledger :85)

現有(`Platform.Web\Controllers\BusinessWorkflowController.cs`):

| 路由 | 行 |
|---|---|
| `GET /api/business-workflows` | :26-28 |
| `GET /api/business-workflows/{name}` | :30-32 |
| `POST /api/business-workflows`(201 + Location fallback :38-40) | :34-41 |
| `PUT /api/business-workflows/{name}` | :43-46 |
| `DELETE /api/business-workflows/{name}` | :48-53 |
| `GET /api/business-workflows/{name}/export` | :55-60 |
| `POST /api/business-workflows/validate` | :62-66 |

**缺口清單(P3 需新增,四條路由 + 對應 service/client 方法):**

1. `GET /api/business-workflows/{name}/revisions` — 對應 `IBusinessWorkflowService.GetRevisionsAsync`(不存在);backend path `/api/business-workflows/{name}/revisions`(`02-spec.md:88` "revisions")。注意:舊 skill-concept `02-spec.md:95` 曾規定 revisions 一律留 Skill 面,已被 hard-reset `02-spec.md:88` 推翻(BW 有自己的 `business_workflow_revision` 表,見 `02-spec.md:61-68`)。
2. `POST /api/business-workflows/{name}/revisions/{rev}/restore` — `RestoreRevisionAsync`(不存在)。
3. `POST /api/business-workflows/{name}/invoke` — `IWorkflowEngineClient.InvokeBusinessWorkflowAsync`(不存在);body 沿用 `WorkflowInvokeRequest`(`WorkflowDtos.cs:8-10`)。
4. `GET /api/business-workflows/catalog` — `GetBusinessWorkflowCatalogAsync`(不存在);`02-spec.md:96` 明訂含 builtin YAML template + custom flow,且前端**不得**再合併兩份 catalog。路由優先序需比照 SkillController 的字面段 vs `{name}`,並補釘住測試(現有 BW 測試無此保護)。

---

#### 5. SkillRoutingAgent 的最小 retarget 點

檔案 `Platform.Service\SkillRoutingAgent.cs`。P3 定位(`05-…ledger.md:90`):「retarget the still-live legacy path to Agent-Skill-only invoke so the hard cutover compiles; delete the routing brain in P4」。

union 端點呼叫共 **3 處**(全在私有方法內,無外部契約):

| 行 | 呼叫 | retarget 目標 |
|---|---|---|
| :358 | `workflows.GetSkillCatalogAsync(userCtx, ct)`(在 `BuildToolsAsync` :347-369,try/catch best-effort :360-365) | `GetAgentSkillCatalogAsync` |
| :493 | `workflows.InvokeSkillAsync(name, input, userCtx, ct)`(在 `InvokeSkillToolAsync` :483-513) | `InvokeAgentSkillAsync` |
| :501 | `workflows.InvokeSkillAsync("rag-qa", ragInput, userCtx, ct)`(kb-query ABSTAIN 兜底 :495-504) | `InvokeAgentSkillAsync`(前提:`rag-qa` 在 P3 後仍是 Agent Skill 而非 flow — **需向 workflow 側確認**,否則此分支要一併刪) |

其餘取用 `IWorkflowEngineClient` 的位置皆為 DI 解析,不含端點語意:`:78`、`:106`(每次呼叫開 scope)、`:190`(測試專用 `BuildToolsAsync(UserContext?, CancellationToken)` overload :188-192)。

**不需改的**:`SkillCatalogToTools`(:376-426)不讀 `kind`;`SingleRequiredStringKey`(:437-475)、`ExtractSkillAnswer`(:523-543)、`IsFatalRun`(:546-562)、`IsAbstain`(:565-572)只吃 `{skill, output}` 形狀,`02-spec.md:118` 保證 invoke 形狀不變。若 catalog 收斂為 Agent-Skill-only,`:396-400` 的 `template-*` builtin 過濾在 P3 後恐成死分支(template skeleton 依 `02-spec.md:96` 歸 BW catalog)—— 可留待 P4 隨整個 routing brain 一起刪。

DI 註冊點(P4 才動):`Platform.Web\Program.cs:242-244`(AG-UI 鏈)、`:285-287`(ChatAssistant 鏈)。

---

#### 6. 測試面盤點

##### 6.1 打 `/api/skills*` 或 `/api/business-workflows*` / 用 SkillDtos union 的檔案

| 檔案 | 命中數 | 分類 |
|---|---|---|
| `platform\tests\Platform.Web.Tests\SkillApiTests.cs` | 45 | 混合:刪 / 改寫 / 留(逐案見 6.2) |
| `platform\tests\Platform.Web.Tests\BusinessWorkflowApiTests.cs` | 14 | 改寫 + 擴充(4 條新路由) |
| `platform\tests\Platform.Web.Tests\SkillImportPipelineTests.cs` | 13 | **保留**(純 import multipart 管線,無 union) |
| `platform\tests\Platform.Service.Tests\SkillServiceTests.cs` | 12 | 混合:Create/Update 系列刪,其餘留 |
| `platform\tests\Platform.Service.Tests\BusinessWorkflowServiceTests.cs` | 7 | 改寫(換 record family) |
| `platform\tests\Platform.Web.Tests\SecurityIntegrationTests.cs` | 4(`/api/skills/catalog` 當 auth 探針:`:20,:56,:73`) | **保留**(探針路徑仍存在) |
| `platform\tests\Platform.Web.Tests\Fakes.cs` | 1(`:371` Location 字串) | 改寫(fake 型別隨 DTO 拆) |
| `platform\tests\Platform.Service.Tests\WorkflowEngineClientTests.cs` | — | 混合:`ValidateSkillAsync` 兩案刪,invoke/catalog 案改名 |
| `platform\tests\Platform.Service.Tests\ChatSkillRoutingTests.cs` | 30(SkillRoutingAgent/catalog) | 保留至 P4(P3 只隨方法改名機械調整) |

##### 6.2 SkillApiTests 逐案分類

**P3 刪除(dual-track / alias / flow 寫入)**
- `Endpoints_Return401_WithoutToken_AndNeverReachDownstream`(:44)的 InlineData `:36`(POST `/api/skills`)、`:39`(PUT)、`:41`(POST validate)三筆 → 刪 case,方法本體保留。
- `Create_Returns201_WithSkill` :133-142
- `Update_Returns200_WithBumpedRevision` :144-151
- `Create_BackendValidationFailed_Returns422_WithEngineCodes` :335-345
- `Create_Returns409_WithBackendMessage_Unchanged` :348-356
- `Create_BackendBadInputWithFieldErrors_ForwardsFieldErrors` :371-384
- `Create_Returns400_WhenDefinitionBlank_AndNeverReachesBackend` :386-395
- `Validate_Returns200_EvenWhenDefinitionInvalid_AndDoesNotWriteToBackend` :434-457 ← **alias 測試,依 `05-…ledger.md:144` 應替換為「route absence(404/405)」斷言,不是純刪**

**P3 改寫**
- `Catalog_Returns200_MergedListWithSourceAndBindableMetadata` :400-412 — 名稱與斷言的「merged」語意消失,改為 Agent-Skill-only。
- `Catalog_IsNotShadowedBy_SkillNamed_catalog` :417-431 — **路由優先序釘住測試(catalog 勝 `{name}`)**,保留機制、更新 fake catalog。
- `Invoke_Returns200_WithEngineOutput` :460-473、`Invoke_Returns400_WhenInputMissing_AndNeverReachesEngine` :475-486、`Invoke_DownstreamError_MapsToSameStatus` :499-507 — retarget 到 Agent-Skill-only invoke,並新增「BW 名稱不得被 `/api/skills/{name}/invoke` 解析」的負向案。
- `User_CanReadCatalogAndNodes` :525-532 — catalog 語意調整。

**P3 保留(不動)**
- `List_Returns200_SnakeCaseFields` :74、`Get_Returns200_WithDefinition` :92、`Revisions_…` :103、`RestoreRevision_Admin_…` :116、`Delete_Returns204` :153、`Export_…` :161/:175、全部 import 案(:220/:235/:263/:286/:300/:321)、`Get_Returns404_WhenBackendNotFound` :359、`Nodes_Returns200_WithNodeContracts` :510。

##### 6.3 路由優先序釘住測試位置(明確)

- **catalog 勝 `{name}`**:`platform\tests\Platform.Web.Tests\SkillApiTests.cs:414-431`(註解 :414-416、`[Fact]` :417、方法 :418-431;核心斷言 :427-430「回陣列且每筆有 source」+「`FakeSkillService.Calls` 中 `get:catalog` 計數不變」)。
- **validate 勝 `{name}`/Create**:`SkillApiTests.cs:433-457`(註解 :433 明寫「POST /api/skills/validate 不得被 POST /api/skills/{name}/invoke 或 Create 吃掉」;無副作用斷言 :456)。P3 刪 validate 後,**此保護必須以「`POST /api/skills/validate` 回 404/405 且不觸及下游」的 route-absence 測試接手**,否則新增任何名為 `validate` 的 Agent Skill 會靜默被 `{name}` 吃掉。
- BW 面**目前完全沒有**對應的優先序測試(`BusinessWorkflowApiTests.cs` 全檔無 catalog 路由),新增 `/api/business-workflows/catalog` 時必須補一條。

##### 6.4 Service.Tests 逐案

`SkillServiceTests.cs` — 刪:`Create_PostsDefinitionOnlyBody_MapsCreatedSkill` :112、`Create_BackendKindSchemaDrift_ThrowsControlled502` :133(**kind 斷言測試,union 拆掉即消失**)、`Create_ForwardsSimpleForm_WhenPresent` :145(**P3-D1:simpleForm 離開 Agent Skill 面**)、`Update_PutsToNamedPath_SendsDefinition` :161、`Create_BackendError_MapsToSameStatusException_KeepsMessage` :256、`Create_Backend422_KeepsEngineErrorCodes` :269、`Create_Backend400WithFieldErrors_KeepsFieldErrors` :284、`Create_Backend2xxWithoutUsableBody_ThrowsControlled502` :301、`Create_Backend400WithoutFieldErrors_HasNullFieldErrors` :312。
⚠️ :256-330 這批是**唯一**覆蓋 `BackendErrorMapper` 全狀態碼矩陣的 service 層測試(經 CreateAsync 進入);刪 CreateAsync 前必須把同一組斷言搬到仍存活的方法(如 `ImportAsync` 或 `RestoreRevisionAsync`),否則 400/409/422/502 映射失去覆蓋。

保留:List :29、Get :59、GetRevisions :73、RestoreRevision :89、Delete :175、Export :200/:220/:230/:240、全部 Import 案 :332-453、:455、:465。

`BusinessWorkflowServiceTests.cs` — 全檔改寫(換型別):List :17、Get :39、Delete :57、Export :86/:107、`Create_PreservesLocationKindAndSimpleForm` :118(**斷言名含 Kind,拆後改為 simpleForm-only**)、`Create_WithoutLocationHeader_YieldsNullLocation` :148、`Update_PreservesKindAndSimpleForm` :162、**`Create_MissingOrNonFlowKind_ThrowsControlled502` :181-190 → 刪**(`EnsureFlowKind` 消失)。

`WorkflowEngineClientTests.cs` — 刪:`ValidateSkillAsync` 兩案 :154、:171。改名/retarget:invoke 系列 :28、:58、:70、:92、:105、:119、:132、:144;catalog :210、:375、:385。保留不動:BW validate :182/:198、tool :230、node :359/:376-377。

##### 6.5 Fake / 共用基礎設施(改寫,非刪)

- `Platform.Web.Tests\Fakes.cs`:`FakeWorkflowEngineClient` :214-340(`InvokeSkillAsync` :231、`ValidateSkillAsync` :253、`ValidateBusinessWorkflowAsync` :264-269 目前**直接委派給 ValidateSkillAsync**,P3 必須分家)、`FakeBusinessWorkflowService` :342-406(:366-372 手 new `Skill(...)` 帶 `"flow"` kind)、`FakeSkillService` :440-611(:568、:588 Create/Update 隨介面刪)。
- `Platform.Service.Tests\Fakes.cs`:`FakeWorkflowEngineClient` :162-240(`InvokeSkillAsync` :190、`ValidateSkillAsync` :209、`ValidateBusinessWorkflowAsync` :213-215 同樣委派、`GetSkillCatalogAsync` :217)。
- `TestWebAppFactory.cs:155`(`IWorkflowEngineClient`→fake)、`:167`(`ISkillService`)、`:170`(`IBusinessWorkflowService`)— 介面改名時同批。
- `EngineCallsCollection.cs:6` 與 `SecurityIntegrationTests.cs:6-8` 的註解提到 `GetSkillCatalogAsync` 靜態計數,方法改名時需同步文字。

---

#### 7. P1 改動對 P3 的影響 — 改寫測試時必須保留的斷言模式

##### 7.1 `AssertApiError` helper

位置:`c:\Users\a8022\Desktop\SpringAITest\platform\tests\Platform.Web.Tests\ApiTestHelpers.cs:53-63`(XML doc :47-52 標明「完整 ApiError envelope 的唯一斷言點(02-spec §5)」)。

強制形狀(六欄,`Assert.Equal(6, obj.Count)` :56):
```
timestamp(非 null :57) / status(=期望碼 :58) / code(=穩定機器碼 :59)
message(非空白 :60) / correlationId(非空白 :61) / fieldErrors(必存在,即使空物件 :62)
```

##### 7.2 P3 改寫時的保留規則

1. **任何被改寫或新增的 Web.Tests 錯誤路徑案,一律用 `body.AssertApiError(status, code)`,不得退回逐欄 `Assert.Equal`** —— 少了 `code`/`correlationId` 就等於回退 P1(helper doc :51 明寫)。
2. 現行使用點(改寫時原樣搬):`SkillApiTests.cs:64`(401 `authentication_required`,並同時斷言兩個下游計數不變 :67-68)、`:276`(413 `payload_too_large`,含 :278 `Assert.Empty(fieldErrors)`)、`:327`(422 `unprocessable_entity` + fieldErrors 逐鍵)、`:341`(同上,Create 路徑 → **搬到存活方法**)、`:354`(409 `version_conflict`)、`:365`(404 `not_found` + :367 空 fieldErrors)、`:506`(invoke 下游錯誤矩陣,`AssertApiError((int)expected, expectedCode)`)。`SkillImportPipelineTests.cs:198`(400 `validation_failed`)。`SecurityIntegrationTests.cs:25,:77`(401)。
3. **`BusinessWorkflowApiTests.cs` 全檔目前零 `AssertApiError`**(只斷言 `StatusCode`,見 :58-83 的 401/403 案)。P3 新增 list/restore/invoke/catalog 四條路由時,其錯誤路徑必須**新寫**成 `AssertApiError`,否則新路由是 P1 envelope 契約的缺口。
4. Service 層對應模式:backend 錯誤 body 以完整六欄 JSON 餵入 stub,斷言例外型別 + message + FieldErrors —— 見 `SkillServiceTests.cs:272`、`:287`、`:404`、`:420` 的 fixture 字串(含 `"code"` 與 `"correlationId"`)。刪 Create 系列時這些 fixture 字串必須跟著搬,不可簡化成三欄舊格式。
5. 新增的 route-absence 測試(取代 `/api/skills/validate`)若走 404,會經 `GlobalExceptionHandler`/MVC 預設路徑 —— 需確認回的是 ApiError envelope 而非空 body(參照 `05-…ledger.md:95` 對 `ChatOrchestratorController.cs:23` bare `NotFound()` 的同類指摘)。

---

##### 附:兩份 spec 的衝突點(P3 動手前需裁決)

- revisions 歸屬:`plans\skill-concept-realignment\02-spec.md:95`「revisions/restore 一律留在 Skill 面,BW 不提供 revisions 路由」 vs `plans\architecture-hard-reset\02-spec.md:88`「`/api/business-workflows*` … revisions」。後者較新(hard-reset 於 `01-plan.md:3` 明示 supersede),且 `02-spec.md:61-68` 已為 BW 建獨立 revision 表 —— 以 hard-reset 為準,但 skill-concept 那份未加 superseded 標記。
- `rag-qa` / `template-*` 的 kind 歸屬決定 `SkillRoutingAgent.cs:501` 與 `:396-400` 是 retarget 還是刪除,需 workflow 側 catalog 拆分結果才能定案。

---

## 7. frontend 拆分(P3-6 輸入)

### P3 前 Frontend 完整盤點（基準 HEAD `b93b63c`）

依據 `plans/architecture-hard-reset/02-spec.md` §3.1 Decided 段落與 `plans/architecture-hard-reset/05-deletion-and-migration-ledger.md` §5（Frontend）。注意：`plans/skill-concept-realignment/` 系列文件已被 hard-reset 取代（見其檔頭 superseded 註記），僅作歷史對照；本盤點以 hard-reset 02-spec/05-ledger 為權威。P1（`07f79a8`）改動範圍只落在 `Toast.tsx`/`OrchestratorsView.tsx`/`WorkflowsView.tsx`/`ErrorText.tsx`，**未觸碰任何 Skill/BusinessWorkflow 元件**——盤點下方逐項確認。

---

#### 1. `types.ts` Skill* 家族使用圖

`types.ts:13` `SkillKind`、及 05-ledger §5 首列所列九型別：`SkillSimpleForm`(`types.ts:248`)、`SkillInfo`(`:254`)、`Skill`(`:269`)、`SkillRevision`(`:274`)、`SkillInputField`(`:289`)、`SkillCatalogEntry`(`:296`)、`SkillValidationError`(`:315`)、`SkillValidation`(`:322`)、`SkillResult`(`:225`)。

| 消費檔 | import 了什麼 | P3 拆分後改動類別 |
| --- | --- | --- |
| `frontend/src/api/skills.ts:2-8` | `Skill, SkillCatalogEntry, SkillInfo, SkillResult, SkillRevision` | **需重寫**：本檔案本身就是 P3 拆分目標（見 §2），型別隨拆分成 `AgentSkill*` |
| `frontend/src/api/businessWorkflows.ts:2` | `Skill, SkillInfo, SkillSimpleForm, SkillValidation`（**沿用 Skill 表型別，非平行型別**，符合 05-ledger 舊版「禁止另建平行型別」紀律，但 hard-reset 後兩張表已物理拆分，此檔需重寫成 `BusinessWorkflow*` 型別） | **需重寫** |
| `frontend/src/hooks/useSkillSelection.ts:9` | `Skill, SkillKind` | **需重寫**（見 §4，`onHistoryReverted` 本身即混 kind 邏輯） |
| `frontend/src/hooks/useSkillRows.ts:3` | `SkillCatalogEntry, SkillInfo, SkillInputField, SkillKind, SkillSimpleForm` | **需重寫**（單一 hook 靠 `kind` 參數服務兩族，拆分後不再有共同 `SkillInfo`） |
| `frontend/src/components/SkillHome.tsx:3` | `Skill, SkillInfo, SkillKind` | **需重寫**（純呈現殼靠 props 泛型化承接兩族，型別需換成聯集/泛型） |
| `frontend/src/components/SimpleSkillEditor.tsx:9` | `SkillInputField, SkillValidation` | **純改名**（本檔只服務 Business Workflow，型別直接改 `BusinessWorkflow*` 前綴即可，邏輯不變） |
| `frontend/src/components/ConfigView.tsx` | 無直接型別 import，僅 tab 常數 | 不受影響（IA 分頁已完成，見文末） |
| `frontend/src/components/BuiltinAgentSkillView.tsx` | 需核對（見下方 grep 結果，實際只讀 `name`/`definition` 字串 prop，非 types.ts 型別） | 低風險 |
| `frontend/src/components/AppShell.tsx:6` | 不 import Skill 型別，但呼叫 `invokeSkill` | **需分流**（見 §2 的關鍵發現：`invokeSkill('rag-qa', …)` 打的其實是 builtin **flow**） |
| `frontend/src/components/AgentSkillHome.tsx:1` | 無型別 import，只呼叫 API 函式 | **純改名**（呼叫端全部沿用 Agent Skill 面 API，改指向新 client 即可） |
| `frontend/src/components/AgentSkillEditor.tsx:3` | API 函式 import；型別另查 | **純改名** |
| `frontend/src/components/AgentEditor.tsx:338-339` | `catalog.filter((skill) => skill.kind === 'agentic'/'flow')` 分組顯示 Agent 綁定 UI（規格 §4：bindable 判準不變，但 UI 分組邏輯保留） | **需分流**：catalog 若拆成兩個端點，這裡要改成兩次獨立呼叫再各自標籤，而非單一 catalog 用 `kind` 過濾 |
| `frontend/src/components/AdvancedSkillEditor.tsx:10` | `NodeInfo, Skill, SkillValidation` | **純改名**（本檔只服務 Business Workflow：create/edit/view 三態，型別改 `BusinessWorkflow*` 即可，`mode.kind` 是 create/edit/view 三態列舉，與 `SkillKind` 無關，不受影響） |
| `frontend/src/components/SkillHistory.tsx:3` | `Skill, SkillRevision` | **需重寫**（見 §4，元件內部仍有 `r.kind === 'agentic'` 分支） |
| `frontend/src/components/SkillRunPanel.tsx:4` | `SkillInputField, SkillResult` | **需分流**：對 kind 無感、props 只吃 `name`/`inputSchema`（05-ledger 4.2 已驗證），但拆 API 後要決定它是否吃兩種不同的 `invoke` 回應型別，或改參數化 |
| `frontend/src/skills/revision.ts:1` | `Skill, SkillCatalogEntry, SkillRevision` | **需重寫**（`canRestoreRevision`/`isSynchronizedSkillSnapshot` 這兩個純函式的簽章本身建立在「同一 revision 列可能混 kind」上，這個不變式在 P3 拆表後不存在，函式語意需重新定義） |
| `frontend/src/skills/agenticPackage.ts` | 不 import types.ts 型別（純 zip/YAML 字串處理） | 不受影響 |
| `frontend/src/skills/skillName.ts`、`validationLabels.ts` | 需核對（grep 命中但為泛用工具） | 低風險，多半純函式無 kind 耦合 |
| `frontend/src/components/OperationsGovernanceView.tsx` | `OperationsSkillMetric` 等 Operations 型別（非本家族，命名巧合） | 不受影響 |
| `frontend/src/agentBuilder.ts`、`AgentBuilderCopilot.tsx`、`api/agentRuns.ts`、`api/agents.ts` | `AgentSkillBinding`/`AgentRunPinnedSkill` 等 Agent 域型別（非本九型別家族） | 不受影響（05-ledger §2.3「Any execution snapshot may pin both artifact kinds using independent … collections」是 backend/workflow 層事，前端這幾檔目前只存 `{skill, revision_policy}` 字串鍵值，無 kind 欄位，天然已符合目標形狀） |

**小結**：9 個核心消費檔中，3 個可**純改名**（`AdvancedSkillEditor.tsx`、`SimpleSkillEditor.tsx`、`AgentSkillHome.tsx`/`AgentSkillEditor.tsx`——因為它們早已是單 kind 專用元件），4 個**需重寫**（`api/skills.ts`、`api/businessWorkflows.ts`、`useSkillRows.ts`、`skills/revision.ts`——因為簽章本身建立在雙 kind 共存假設上），2 個**需分流**（`useSkillSelection.ts`、`SkillHome.tsx`——因為它們是目前唯一同時持有兩族狀態的共用層）。

---

#### 2. `api/skills.ts` 全部函式與呼叫者

`frontend/src/api/skills.ts` 現有 9 個匯出函式：

| 函式 | 位置 | 呼叫者 |
| --- | --- | --- |
| `listSkillCatalog` | `:11-13` | `useSkillRows.ts:2,21`、`useSkillSelection.ts:3,100`、`SkillHome.tsx:2,100`、`SimpleSkillEditor.tsx:3,74`、`AgentEditor.tsx:13` |
| `invokeSkill` | `:16-24` | `SkillRunPanel.tsx:2,161`、`AppShell.tsx:6,192`（**關鍵**：呼叫 `invokeSkill('rag-qa', …)`，而 `rag-qa` 是 `workflow/app/skills/rag-qa.yaml`——builtin 無 `kind:` 宣告 → 依 05-ledger §3「全部 12 個 builtin 預設 `flow`」→ 這其實是在呼叫一個 **Business Workflow**，不是 Agent Skill！） |
| `listSkills` | `:27-29` | `AgentSkillHome.tsx:1,18` |
| `getSkill` | `:31-33` | `AgentSkillHome.tsx:1,19`、`useSkillSelection.ts:3,99`（flow 分支用 `getBusinessWorkflow`、agentic 分支用 `getSkill`） |
| `listSkillRevisions` | `:36-38` | `SkillHistory.tsx:2,34` |
| `restoreSkillRevision` | `:44-49` | `SkillHistory.tsx:2,67` |
| `deleteSkill` | `:52-54` | `AgentSkillHome.tsx:1,21` |
| `exportSkill` | `:57-68` | `AgentSkillHome.tsx:1,20` |
| `importSkill` | `:76-83` | `AgentSkillHome.tsx:6-7`（`uploadAgentSkill` 包裝） |
| `getSkillPackage` | `:86-88` | `AgentSkillEditor.tsx:3` |

**`invokeSkill` 的 kind 過濾邏輯**：`api/skills.ts` 本身**沒有**任何 kind 過濾——它是純轉發（`POST /api/skills/{name}/invoke`）。真正的 kind 過濾發生在**呼叫端**：`useSkillRows.ts:25,39` 用 `kind` 參數各自過濾 custom/builtin 兩路清單、`useSkillSelection.ts:99` 用 `restored.kind === 'flow'` 三元選 `getBusinessWorkflow` 還是 `getSkill`。這代表 P3 拆分後，`invoke` 端點本身要嘛統一 kind-neutral、要嘛拆成兩支——依 02-spec §3.1/§3.3，目標是拆成 `/agent-skills/{name}/invoke` 與 `/business-workflows/{name}/invoke` 兩支獨立路由，**過濾邏輯要從「呼叫端猜哪個 getter」升級成「呼叫端知道自己是哪個 domain，直接呼叫對應 client」**。

**P3 後兩個 API 客戶端的目標形狀**：
- `api/agentSkills.ts`（新名）：`listAgentSkillCatalog`、`invokeAgentSkill`、`listAgentSkills`、`getAgentSkill`、`listAgentSkillRevisions`、`restoreAgentSkillRevision`、`deleteAgentSkill`、`exportAgentSkill`、`importAgentSkill`、`getAgentSkillPackage`——現有 9 個函式改名+改路徑前綴（`/api/skills*` → 語意收斂，02-spec §3.2）。
- `api/businessWorkflows.ts`（**已存在**，見下）：需補 `invokeBusinessWorkflow`、`listBusinessWorkflowRevisions`、`restoreBusinessWorkflowRevision`（目前這三支**缺失**——`BusinessWorkflowHome.tsx` 目前仍靠共用 `SkillHistory`/`SkillRunPanel` 打 `api/skills.ts` 的 `listSkillRevisions`/`restoreSkillRevision`/`invokeSkill`，這是 P3 必須補的缺口）。

**`api/businessWorkflows.ts` 現況與缺口**：已存在（`frontend/src/api/businessWorkflows.ts`，7 個函式：`listBusinessWorkflows`/`getBusinessWorkflow`/`createBusinessWorkflow`/`updateBusinessWorkflow`/`deleteBusinessWorkflow`/`validateBusinessWorkflow`/`exportBusinessWorkflow`），但**缺 invoke 與 revisions/restore 三支**——目前這三個操作全部繞道 `api/skills.ts`（`SkillRunPanel.tsx` 打 `invokeSkill`、`SkillHistory.tsx` 打 `listSkillRevisions`/`restoreSkillRevision`），這正是 05-ledger 第 110 行「Split or parameterize `SkillHistory.tsx`/`SkillRunPanel.tsx`/`useSkillSelection.ts`/`useSkillRows.ts` with explicit domain contracts」要處理的缺口，也是 02-spec §3.1「`/api/business-workflows*` 增列 revisions/invoke」（若採此形狀）的前端對應面。

---

#### 3. 三處 catalog 呼叫點現值（05-ledger §5 明確列出，行號在本 HEAD 未變）

| 呼叫點 | 現值 | 目標端點（P3-D2：拆兩個 catalog，builtin templates 歸 business-workflows catalog） | 最小改法 |
| --- | --- | --- | --- |
| `SkillHome.tsx:100`（`openBuiltinView`） | `await runWithToast(toast, listSkillCatalog, { onSuccess: (catalog) => {…} })`（`SkillHome.tsx:2,100`）——**不分 kind，統一打 `listSkillCatalog`**，即使是業務流程列也打這支 | Business Workflow 行改打 `listBusinessWorkflowCatalog`（新端點）、Agent Skill 行維持 `listSkillCatalog` | 因為 `SkillHome` 是兩族共用殼、`props.kind` 已知，最小改法是把 `listSkillCatalog` 換成 `props` 注入的 `listCatalog: () => Promise<CatalogEntry[]>`（比照現有 `listCustom`/`getDetail` 的 props 注入模式），由 `AgentSkillHome.tsx`/`BusinessWorkflowHome.tsx` 各自傳入對應 catalog 函式——**不需要在 `SkillHome` 內部判斷 kind**，維持既有「無 kind 決策的共享 presentation」設計（`SkillHome.tsx:41` 檔頭註解） |
| `useSkillSelection.ts:100`（`onHistoryReverted`） | `Promise.all([restored.kind === 'flow' ? getBusinessWorkflow(...) : getSkill(...), listSkillCatalog()])`（`:98-101`）——**detail 已依 kind 分流，但 catalog 呼叫沒有跟著分流**，回溯業務流程時仍打 Agent Skill catalog 找 entry | 依 `kind` 分流 catalog 呼叫：`restored.kind === 'flow' ? listBusinessWorkflowCatalog() : listSkillCatalog()` | 與上面的 detail 三元運算式同一行改成同款三元運算式即可，`useSkillSelection` 已持有 `kind` option（`:16,23`），改動幅度極小（1 行） |
| `SimpleSkillEditor.tsx:74`（`loadSkeleton`） | `const catalog = await listSkillCatalog()`（`:74`）——**本檔案 100% 只服務 Business Workflow**（`renderSimpleEditor`/`renderCreateEditor` 只掛在 `BusinessWorkflowHome.tsx:29-34`），卻讀 Agent Skill catalog 找 `template-*` 骨架 | 改打 `listBusinessWorkflowCatalog()`（`template-*` 骨架本就是 flow，見 05-ledger §3「12 個 builtin YAML 全數 kind=flow」） | 單行替換 import + 呼叫，無需新增判斷邏輯（本檔沒有 kind 分支問題，純粹是打錯 catalog） |

三處共同前提：**P3-D2 定案後 catalog 拆分本身要先在 `api/skills.ts`（新增 `listBusinessWorkflowCatalog` 或搬進 `api/businessWorkflows.ts`）落地**，上述三處才有目標函式可打；目前 `api/businessWorkflows.ts` 完全沒有 catalog 相關函式（見 §2 缺口）。

---

#### 4. kind 分支清單（跨 `SkillHome`/`AgentSkillHome`/`BusinessWorkflowHome`/`SkillRunPanel`/`SkillHistory`/`useSkillSelection`/`useSkillRows`）

| 位置 | 分支內容 | P3 刪除後由哪個 domain 元件承接 |
| --- | --- | --- |
| `useSkillRows.ts:19,25,39`（`useSkillRows(kind, listCustom)`） | 整個 hook 靠 `kind: SkillKind` 參數過濾 custom 列（`:25`）與 builtin catalog 列（`:39`） | 拆成 `useAgentSkillRows()`/`useBusinessWorkflowRows()` 兩支參數化 hook（各自呼叫對應 `listCustom`/`listCatalog`，內部不再需要 `.filter(x => x.kind === kind)`，因為兩個 domain 的 list 端點本身天生單一 kind） |
| `useSkillSelection.ts:16,23`（`Options.kind`） | hook 整體帶一個 `kind` option，用來判斷 `onHistoryReverted` 該打哪個 detail/catalog（`:99`）、以及回溯後 kind 是否漂移（`:115,121`） | 拆成 `useAgentSkillSelection()`/`useBusinessWorkflowSelection()`；**但 `:115,121` 的「回溯後 kind 漂移」分支本身就是「同一 revision 歷史混 kind」這個 P3 要拆掉的不變式的產物**——02-spec §2.2 目標態下 `business_workflow`/`agent_skill` 是物理分離表，revision 歷史不再可能混 kind，這整段跨 kind 提示邏輯（`:115-127`）應**直接刪除**，不是搬家 |
| `useSkillSelection.ts:99`（`restored.kind === 'flow' ? getBusinessWorkflow(...) : getSkill(...)`） | detail 讀取三元分流 | 兩個獨立 hook 各自只呼叫自己的 detail getter，三元運算式消失 |
| `SkillHome.tsx:2,100`（`openBuiltinView` 呼叫 `listSkillCatalog`） | 見 §3 | 由 props 注入的 `listCatalog` 承接（`AgentSkillHome`/`BusinessWorkflowHome` 各自決定打哪支） |
| `SkillHome.tsx:161,222`（`source === 'builtin'` 分支） | **這不是 kind 分支，是 source 分支**（custom vs builtin），兩族都有 builtin，P3 不刪 | 維持在拆分後的兩個 Home 元件內各自保留（builtin/custom 是兩族內部都存在的正交維度，不是 P3 要消滅的 kind 耦合） |
| `SkillHistory.tsx:104-107`（`r.kind === 'agentic' ? 'Agent Skill' : 'Flow'` 徽章 + `r.kind === 'agentic' && !canRestoreRevision(r)` 停用回溯鈕） | 逐 revision 顯示型別徽章、agentic 專屬「未保存套件無法回溯」提示 | 拆成 `AgentSkillHistory.tsx`（永遠顯示「Agent Skill」徽章 + package 保留檢查）/`BusinessWorkflowHistory.tsx`（永遠顯示「Flow」徽章、**`canRestoreRevision` 的 `has_package` 分支整支刪除**，因為 flow revision 只要有 `definition` 就永遠可回溯，`skills/revision.ts:10` 的 `revision.kind === 'flow' ||` 判斷式在拆表後失去意義） |
| `skills/revision.ts:10`（`canRestoreRevision`：`revision.kind === 'flow' \|\| revision.has_package === true`） | 見上；本函式本身建立在混 kind revision 列表的假設上 | `AgentSkillRevision` 一律走 `has_package` 檢查；`BusinessWorkflowRevision` 一律 `true`（甚至可以直接刪函式，因為 flow revision 恆可回溯） |
| `skills/revision.ts:36-48`（`isSynchronizedSkillSnapshot`：比較 `detail.kind === catalog.kind`） | 同步快照比對含 kind 比較 | 兩個獨立版本各自比較 `name`+`revision`，不再需要比 `kind`（型別系統本身已保證同 domain） |
| `SkillRunPanel.tsx` | **無 kind 分支**（05-ledger 4.2 已驗證：只吃 `name`/`inputSchema`，對 kind 無感） | 維持共用，或視 invoke API 拆分結果做參數化（見 §2） |
| `AgentSkillHome.tsx`/`BusinessWorkflowHome.tsx` | 兩者透過 `<SkillHome kind="agentic"/"flow" … />` 傳入 `kind` prop（`AgentSkillHome.tsx:15`、`BusinessWorkflowHome.tsx:16`），但**元件本體不做 `if (kind === …)` 判斷**——`kind` 只是原樣轉交給共用的 `useSkillRows`/`useSkillSelection` | `kind` prop 本身隨 §1 拆分而消失（兩個 Home 各自實例化對應的 domain-specific hook，不再需要傳一個 kind 字面值下去） |

**06-ledger 補充**：05-ledger 第 111 行「Compatibility Skill/Business Workflow UI branching → SkillHome, AgentSkillHome, BusinessWorkflowHome, run panels → P3/P5: Delete kind-routing state; retain only genuinely shared visual components」——上表已逐一標出哪些是「genuinely shared」（`SkillRunPanel` 全部、`SkillHome.tsx:161,222` 的 source 分支）該留，哪些是「kind-routing state」（`useSkillRows`/`useSkillSelection` 的 kind 參數與三元分流、`SkillHistory` 的 kind 徽章）該刪。

---

#### 5. P1 改動的保護面（`07f79a8`，不得被 P3 破壞）

**改動範圍確認**（`git show --stat 07f79a8`）：只碰 `OrchestratorsView.tsx`、`WorkflowsView.tsx`、`Toast.tsx`、`ErrorText.tsx` 四個生產檔 + `agentBuilder.ui.spec.ts`(+2 行)、`orchestratorEditor.ui.spec.ts`（新增 172 行）、`toastConflict.unit.spec.ts`（新增 76 行）、`workflowEditor.ui.spec.ts`（新增 91 行）四個測試檔。**Skill/BusinessWorkflow 相關元件完全未被 P1 觸碰**——這代表 P3 改 Skill 元件時，不會與 P1 產生檔案層級衝突，但仍需遵守以下**行為契約**（因為 P1 建立的是共用層 `Toast.tsx` 的公開契約，Skill 元件目前雖未使用，但 P3 若要收斂 `SkillHome`/`AdvancedSkillEditor` 的錯誤處理去對齊這個新契約，必須遵守）：

1. **`runWithToast` 的 `onConflict`（`Toast.tsx:39-56`）**：`fn` 不得自行吞錯（否則假成功 toast）；409/412 若呼叫端提供 `onConflict` 就只鎖編輯器、不噴 toast；沒提供則走一般錯誤 toast。回歸測試：`toastConflict.unit.spec.ts` 6 個 case（409 鎖定不噴 toast、412 視同衝突、非衝突失敗維持錯誤 toast、無 onConflict 的呼叫端衝突仍走錯誤 toast、`requireLoaded` 守衛失敗噴錯誤而非靜默、成功路徑順序正確）。
2. **`requireLoaded`（`Toast.tsx:63-66`）**：寫入前提不成立必須 throw，不可靜默 `return`（靜默 return 在 `runWithToast` 眼中等於成功）。**目前 Skill 相關元件（`SimpleSkillEditor.tsx`、`AdvancedSkillEditor.tsx`）尚未採用 `runWithToast`/`requireLoaded`**（各自手刻 try/catch，見 `SimpleSkillEditor.tsx:117-163` 的 `onSave`），若 P3 順手把它們搬去共用 `runWithToast`，必須同步補上 `requireLoaded` 守衛，不可留裸 `if (!x) return`。
3. **`OrchestratorsView.tsx` 受控 `JsonField`（`:198-210`）模式**：`{text, error}` 全受控、`onChange` 永遠寫回顯示文字、只有 `JSON.parse` 成功才更新 draft、解析失敗鎖住所有 server 動作按鈕。**Skill 側目前無等價 JSON 欄位**（simple editor 用具名欄位表單，advanced editor 是純文字 YAML textarea 無 JSON 解析），P3 若不新增 JSON 欄位則此契約不適用；若未來新增（例如業務流程進階編輯器要加結構化 JSON 區塊）必須比照此模式。
4. **`AgentEditor.tsx` 的「no bug」結論（05-ledger 第 114 行）**：`AgentEditor.tsx` 不經過 `runWithToast`，其 409 處理是元件自己的 try/catch（`setConflict` 在 `await` 之後），本來就不會有假成功 toast——**這是既有正確行為，P3 不得「順手」把它改成走 `runWithToast` 卻漏掉這個既有正確性**；回歸釘子在 `agentBuilder.ui.spec.ts`（新增 2 行斷言）。
5. **`ErrorText.tsx` 改動**（P1 附帶修改，7 行差異）：需保留（`SkillRunPanel.tsx:6,240`、`SkillHome.tsx:9,208`、`SkillHistory.tsx:7,90` 等 Skill 元件均依賴 `ErrorText` 顯示錯誤），P3 不得改變其 `msg`/`id` props 契約。

---

#### 6. 測試面：`frontend/tests` 涉 Skill/BusinessWorkflow 的 spec

全 27 個測試檔中，涉及本域的：

| 測試檔 | 內容 | P3 處置 |
| --- | --- | --- |
| `skillConcept.ui.spec.ts`（622 行，**既有檔案，非 P1 新增**） | 全面覆蓋：兩入口互斥清單（`:59-73`）、Agent Skill 共用 run/history 流程（`:75-98`）、單筆/無 package revision 邊界（`:101-125`）、builtin 唯讀檢視（`:127-143`）、業務流程回溯打自己的 API 面（`:145-152`）、**跨 kind 回溯行為**（`:154-216`，`signInCrossKind`+for 迴圈 `flow`/`agentic` 兩案）、回溯三次不同步放棄（`:218-258`）、簡單模式建立/儲存/試跑（`:294-419`）、進階模式交棒與 409（`:421-446`）、export/delete/import 失敗面（`:448-544`）、進階編輯儲存後可二次編輯（`:546-566`）、Agent Skill 改名不可變（`:568-622`） | **改**：`api/skills`/`api/business-workflows` 兩面路徑斷言需依 §2 新端點改寫；`signInCrossKind`（`:154-216`）與跨 kind 回溯測試（`:199-216`）整段是**專為「同一 revision 混 kind」不變式而寫**，P3 拆表後該不變式消失，這段**應刪**（不是改斷言，是該場景本身不再存在）；其餘場景（互斥清單、單一 domain 的 run/history/export/import/simple-save/advanced-handoff）應**拆成 `agentSkillHome.ui.spec.ts` / `businessWorkflowHome.ui.spec.ts` 兩檔**，斷言邏輯多數只需改端點路徑字串，不需重寫場景 |
| `toastConflict.unit.spec.ts`（P1 新增，76 行） | 見 §5，純測 `Toast.tsx` 共用層，不含任何 Skill/BusinessWorkflow 字串 | **留**（不動；P3 若讓 Skill 元件採用 `runWithToast` 也只是多幾個呼叫端，不影響本檔既有斷言） |
| `orchestratorEditor.ui.spec.ts`、`workflowEditor.ui.spec.ts`（P1 新增） | 純 Orchestrator/Workflow 編輯器 409/JsonField 場景 | **留**（與 Skill/BusinessWorkflow 無關） |
| `agentBuilder.ui.spec.ts`（P1 加 2 行）、`agentBuilder.unit.spec.ts` | Agent Builder 含 skill binding UI（`AgentEditor.tsx:338-339` 的 kind 分組），grep 命中因涉及 `AgentSkillBinding`/catalog kind 分組 | **改**（小改）：`AgentEditor.tsx:338-339` 的 catalog kind 過濾若因 catalog 拆分改成兩次呼叫，這裡的 fixture mock（單一 `/api/skills/catalog` 回應含兩種 kind）需要拆成兩個 mock 端點；核心 Agent 綁定斷言（`skill_bindings: [{skill, revision_policy}]`）不受影響，因為此形狀本就與 kind 無關（§1 已確認） |
| `workflowDesigner.unit.spec.ts`、`orchestratorRuns.unit.spec.ts`、`orchestratorDurability.unit.spec.ts`、`agentRuns.unit.spec.ts`、`agentRuns.ui.spec.ts` | grep 命中多半是 `pinnedSkills`/`skill_bindings`/文件字串等 Agent Run 域用語，非本文九型別家族 | **留**（不動；這些型別/欄位在 05-ledger §1 屬於 backend/workflow 的 Harness pin 拆分範疇，非本次 frontend types.ts 盤點對象，且 §2.3 已明訂 workflow 側 `PinnedSkillSummary` 拆分不影響前端 wire 形狀——前端這幾個檔案讀的欄位名不變） |

**現況零覆蓋確認**（05-ledger 舊版 4.3 的說法已過時，需更正）：舊 `skill-concept-realignment` 帳本寫「Skill 領域無任何 `*.spec.ts`」，但**現況（HEAD `b93b63c`）`skillConcept.ui.spec.ts` 已存在且覆蓋完整**（P4 IA 拆分後補上）。因此 P3 前置的測試面盤點結論是：**測試覆蓋已充分**，P3 的測試工作是「拆檔 + 刪除混 kind 場景 + 改端點路徑」，不是「從零建立」。

---

#### 附：與舊 `skill-concept-realignment` 計畫的落差對照（供 P3 排程參考）

比對本次盤點與 `plans/skill-concept-realignment/05-dead-code-and-test-ledger.md` §4（該計畫 P4 目標），**現況已超前完成**：`AgentSkillHome.tsx`/`BusinessWorkflowHome.tsx` 已存在並拆分、`ConfigView.tsx` 已是兩個平級 tab（`:15,18-19,174-175`）、`isAgenticDefinition`/`kindOf()` fallback 鏈已不存在（`agenticPackage.ts` 現無此函式）、`api/businessWorkflows.ts` 已存在。這代表實際執行路徑是「先做完舊計畫的 P0-P4 前端 IA 重組，再套用 hard-reset 的物理拆表目標」——本次盤點的六項發現全部是針對 hard-reset 02-spec/05-ledger 的**目標態落差**，不是舊計畫的落差。

---

## 8. contracts/tests 清理面(P3-2/P3-7 輸入)

### P3 前跨服務契約與測試清理面盤點

基準：HEAD `b93b63c`；參考 `plans/architecture-hard-reset/05-deletion-and-migration-ledger.md`（§7 Tests and documents）、`04-acceptance-tests.md`（P3 矩陣 P3-00~P3-11）、`02-spec.md`（§3 HTTP contracts、§6.1 migration handoff）。以下均为**现状确认**（P3 尚未执行，源码/文档仍是当前样貌），不改任何档案。

#### 1. P3 刪除的測試清單(整檔) — 現況：全部仍存在

| 檔案 | 現況 | 對應 ledger 條目 |
| --- | --- | --- |
| `backend/tests/Backend.Api.Tests/SkillsBusinessWorkflowsDualTrackConsistencyTests.cs` | 存在,225 行,7 個 `[Fact]`/`[Theory]`(`DetailReads_AreByteEquivalent`、`ExportReads_AreEntryEquivalent`、`BusinessPost_RevivesSoftDeletedFlow_...`、`BusinessPost_EnabledAgenticName_Returns409_...`、`BusinessWrites_AgainstAgenticOverwrittenFlow_Return404_...`、`FlowOverwrittenByAgenticImport_...`、`BusinessPut_DefinitionNameMismatch_Returns422`)。檔頭註解本身已標註 `/// P2–P5 相容窗口契約;P5 C8 收斂時本檔必須與雙軌一起刪除`,與 05-ledger §7 行 143 一致 | §7「Dual-track equivalence...tests」→ P3 刪除 |
| `backend/tests/Backend.Api.Tests/SkillPackageMigrationTests.cs` | 存在,測試 `SkillPackageMigration.RewriteAsync` 的舊 zip SKILL.md 挑選規則(`Rewrite_DecoySkillMdFirst_...` 等),搭配 `backend/src/Backend.Api/Data/SkillPackageMigration.cs` 生產碼 | §7「Skill package migration tests for old rows」→ P3 刪除;§1 行 19「Package/name rewrite migrations」→ P3 刪除生產碼 |
| `workflow/tests/test_skills_api.py` 內的 alias 案例 | 檔案存在,`test_business_workflow_validate_alias_matches_legacy_path`(第 100 行)比對 `/skills/validate` 與 `/business-workflows/validate` 回應相等;同檔另有 `test_validate_valid_definition`、`test_validate_reports_error_codes_in_body`、`test_validate_bad_yaml_does_not_raise`、`test_validate_has_no_side_effects` 都打 `/skills/validate`(P3 刪路由後這些也需隨之處理,非全部刪除,但都受影響) | §7「Alias-preservation tests」→ P3 刪除並改為 route-absence 斷言;§3 行 61「`/skills/validate` compatibility alias」→ P3 刪路由與 alias 測試 |

**其他名稱/內容屬 dual-track、410、alias 保留目的的測試檔** — 逐檔確認:
- 未找到任何名稱含 `410` 字面字串的 backend 測試檔(`Grep 410` on `backend/tests` 零結果),表示目前尚未有預先寫好的「410 佔位」測試,410 化仍待 P5/C8(依 `backend/AGENTS.md` gotcha,`/api/skills*` flow 寫入變 410 是 **P5 C8** 範圍,不在 P3)。
- `workflow/AGENTS.md` 明文:`POST /skills/validate` 是同 handler 相容 alias,「P5/C8 不移除此 alias」——但 05-ledger §3 行 61 明確標 P3 刪除。**這是 AGENTS.md(現狀文件)與 05-ledger(P3 目標)之間的已知落差**,P3-7 文件同步必須處理此矛盾陳述(AGENTS.md 目前的說法會在 P3 後失真)。
- `docs/agent-platform-contracts.md`、`backend/AGENTS.md` 中對「雙軌」「P5 C8 收斂」的敘述(見下方第 4 節)也是「保留雙軌現況」的文字錨點,P3 後需同步。

#### 2. P3 改寫的測試面

**backend 打 `/api/skills*` 路由的測試檔(7 個,Grep `/api/skills` 於 `backend/tests` 精確定位)：**
- `AgentsApiTests.cs`(行 130、484、1100 — 透過 `/api/skills` 建立/改寫/刪除技能列,供 Agent 綁定測試用,非該檔主體但依賴此路由)
- `SkillsBusinessWorkflowsDualTrackConsistencyTests.cs`(見上,整檔即雙軌契約)
- `SkillsApiTests.cs`
- `SkillImportTests.cs`
- `SkillExportTests.cs`
- `SkillExecutionArtifactTests.cs`
- `BusinessWorkflowApiTests.cs`(業務流程路由測試,但依 backend AGENTS.md「Artifact storage and dual-track contract」現況,仍與 `/api/skills*` 共用同一 repository/revision 序列,可能有交叉斷言)

不打 HTTP 路由、僅測 repository/validator 層(不屬本項改寫範圍,但屬同一 `Skills/` 命名空間、P3 §2 行 42/51/54 會搬遷的相鄰檔案)：`SkillRepositoryTests.cs`、`SkillValidatorTests.cs`、`SkillPackageValidatorTests.cs`、`InMemorySkillRepositoryConcurrencyTests.cs`。

**workflow 以 `/skills/` 打 TestClient 的測試檔(18 個,已逐檔用 Grep 定位並用 Read 核對非路徑字面誤判)：**

| 檔案 | 打的路由 |
| --- | --- |
| `test_skills_api.py` | `/skills`、`/skills/validate`、`/skills/{name}/invoke` |
| `test_skills_custom.py` | `/skills/*`(667 行,05-ledger §7 行 150 另標註要拆成兩個 per-type 檔) |
| `test_validate_package_api.py` | `/skills/validate-package` |
| `test_correlation_id.py` | `/skills/__correlation-boom__/invoke`、`/skills/no-such-skill/invoke`、`/skills/__correlation-agentic-boom__/invoke` |
| `test_skill_triage.py` | `/skills/triage/invoke` |
| `test_skill_templates.py` | `/skills/*`(模板骨架載入) |
| `test_skill_summarize.py` | `/skills/summarize/invoke` |
| `test_skill_rag_qa.py` | `/skills/rag-qa/invoke` |
| `test_skill_analyze_report.py` | `/skills/analyze-report/invoke` |
| `test_nl_logic.py` | `/skills/validate`(決策表 script 白名單驗證) |
| `test_nl_extract.py` | `/skills/__engine-probe__/invoke`、`/skills/__kb_engine-probe__/invoke` |
| `test_input_engine_key_stripping.py` | `/skills/validate`、`/skills/validate-package` |
| `test_engine_guardrails.py` | `/skills/context-task-local/invoke` |
| `test_context_task_local.py` | `/skills/context-task-local/invoke` |
| `test_context_enrichment.py` | `/skills/context-enrichment/invoke`、`/skills/{name}/invoke` |
| `test_config_retrieval_topk.py` | `/skills/{name}/invoke` |
| `test_config_apply.py` | `/skills/{name}/invoke` |
| `test_agent_skill_runner.py` | `/skills/sales-helper/invoke`(同時 mock 呼叫 backend 的 `/api/skills/sales-helper`) |

備註:`test_agent_runtime.py` 的 `snapshot()`/`flow_artifact()` fixture 被 5 個消費檔引用(05-ledger §7 行 149 已列),不含在上表(不是直接打 `/skills/` HTTP,是 pin/artifact 資料模型 fixture),但同屬 P3 改寫範圍。

platform 面依指示不重複盤點(已由另一代理處理)。

#### 3. scripts/verify-*.ps1 的舊路由引用

六支 verify 腳本逐支檢查(`/api/skills`、`skill kind`、`skill_revision` 概念字面定位)：

| 腳本 | 舊路由/概念引用 | P3 後是否會壞 |
| --- | --- | --- |
| `verify-copilot-shared-core.ps1` | 第 307 行:`Invoke-JsonRequest POST "$BaseUrl/api/skills/rag-qa/invoke"`(C-07 案例,對 Platform 公開 API) | **會壞**。`rag-qa` 是內建 12 個 YAML 之一,未宣告 `kind:`,依 05-ledger §3 行 72 全數判為 Business Workflow;02-spec §3.1 明定 P3 後 `/api/skills/{name}/invoke` 不得解析 Business Workflow。此腳本被 `.github/workflows/ci.yml` 的 `full-chain-smoke` job 直接呼叫(`./scripts/verify-copilot-shared-core.ps1 -Rebuild -CiComposeSubset`),**必須同 P3 tranche 改成 `/api/business-workflows/rag-qa/invoke`,否則 CI 該 job 會紅** |
| `verify-copilot-shared-core-evidence.ps1` | `skillName`、`load_skill`(node type)、`chatCapture.skillName`(D6 canary chat 證據擷取欄位) | 不直接打 `/api/skills*` 路由,是 D6 chat 路由證據裡的「被路由到哪個技能」欄位名稱,屬 §4 行 90「Skill-routing compile-time API consumer」/ P4 才會清的 legacy chat brain 範圍,**P3 不受影響**,P4 才需檢視 |
| `verify-agent-runtime-d3.ps1` | `$SkillBindings`(New-Agent 參數)、`skill_bindings`(Agent 定義欄位)、`skill_not_pinned`(錯誤碼) | 這是 Agent 的 `agent_revision_skill` 綁定欄位/錯誤碼,05-ledger §1 行 15 明定「Rebuild as an Agent-Skill-only pin/link」——欄位語意收斂為 Agent-Skill-only,但欄位名稱/錯誤碼字面**未被列為變更項**,**P3 大概率不壞**,但需在 P3 落地時核對欄位名稱是否隨 DTO 重構改名 |
| `verify-multi-agent-d5.ps1` | 無 skill/kind/skill_revision 字面匹配 | 不受影響 |
| `verify-agent-governance-d7.ps1` | 無 skill/kind/skill_revision 字面匹配 | 不受影響 |
| `verify-agent-chat-d6.ps1` | `INSERT INTO workflow(...,kind,...)` 對 `orchestrator`/`agent-runtime` 兩種 `kind` | 這是 D4 Harness `workflow`/`workflow_revision` 表的 `kind` 欄(§8 明文保留、不得改名或併入 `business_workflow`),**與被刪的 `skill.kind` 無關,不受影響** |

**結論:六支中僅 `verify-copilot-shared-core.ps1` 一支需要在 P3 同 tranche 修改**(C-07 案例的路由),且它是 CI `full-chain-smoke` job 的直接依賴,風險等級高。

#### 4. 契約文件面(P3-7 文件同步輸入)

**`docs/agent-platform-contracts.md`**：無 `/api/skills*`、`skill_revision`、`SkillMetadata` 字面匹配,但有兩處含糊的「Skill」措辭待 P3-7 收斂命名：
- 第 9 行(D1 段):「publish rechecks and locks all Workflow/**Skill** references」、「**Skill** catalog exposes Workflow-owned `bindable`」— 語境是 Agent 綁定 Skill 的 current_revision,P3 後應明確改稱 Agent Skill。
- 第 25 行(D5 段):「Every child pins Agent/**Skill**/runtime Workflow revisions」— 同樣需要明確化為 Agent Skill。

**`docs/context-enrichment-contracts.md`**：零 `skill` 字面匹配,無需同步。

**`README.md`**：這是本次盤點中**最大的文件同步面**,集中在兩段:
- 第 214 行:「Dapper 2.x + Npgsql 直連 appdb postgresql（含 skill / skill_revision 表）」— 表名字面引用,P3 後表已刪除,需改寫。
- 第 322–359 行(「受保護的 API」表格 + curl 範例):`/api/skills`(GET)、`/api/skills/{name}/invoke`(POST)、`/api/skills/validate`(POST) 三條路由條目,以及三段 `curl .../api/skills*` 範例(rag-qa、analyze-report)——這些是**當前统一 invoke 端點的示範**,P3 後 Business Workflow 类范例(rag-qa/summarize/triage/analyze-report)須改走 `/api/business-workflows/{name}/invoke`。
- 第 361–419 行(「能力成品：Agent Skill 與 Business Workflow」整節)：
  - 第 363 行明文「兩者目前共用物理 `skill`/`skill_revision` 儲存與統一 invoke 端點」— P3 後失真,需重寫為兩張獨立表。
  - 第 375、390–396、400、406、412、418 行:多條 `/api/skills*`、`/skills/validate` alias 說明、P2–P5 雙軌說明段落(「P2–P5 是刻意的雙軌期間...只有 cleanup C8...才可把 Skill 面收斂為 Agent-Skill-only」)——此段落本身描述的是 P5/C8 里程碑而非 P3,但 P3 執行後「兩者共用同一 revision 序列」这句已經不成立(P3 就是拆表的節點),需要在 P3 tranche 就先修正,不能留到 P5。

**`backend/AGENTS.md`**(附加系統提示已顯示全文)：Gotchas 段「Artifact storage and dual-track contract」與「Business Workflow writes and export」兩段皆完整描述現行 `skill`/`skill_revision` 共用表、`kind` 判別、`/api/skills*` 相容路徑與 P5/C8 410 轉換條件——**這是 backend 側最集中的文件同步點**,P3 落地時需整段重寫(現在的文字是「目前如何」,P3 後會變成歷史陳述)。

**`workflow/AGENTS.md`**：APIs 段落「`POST /skills/validate` 是同一 handler 相容 alias;P5/C8 不移除此 alias」— 與 05-ledger §3(P3 即刪除該 alias)直接衝突,是本次盤點發現的**現存文件矛盾**,P3-7 必須連同 ledger 決策一起修正措辭。Gotchas 段另有「Script authoring gate」提及 `POST /skills/validate` 一處。

#### 5. infra/評證面

- `infra/docker-compose.evidence.yml`：零 `skill` 字面匹配,不依賴舊 skill 路由/表。
- `infra/docker-compose.yml`(base,非 evidence 但供對照)：僅兩處註解提及「Skill 寫入(POST/PUT /api/skills)時呼叫工作流引擎做驗證」(第 246、248 行,純註解說明 `WORKFLOW_BASE_URL` 用途)及一個不相關的 `ISOLATED_SKILL_SCRIPTS_ENABLED` 旗標(腳本隔離開關,05-ledger 未列為刪除項,保留)。這兩處註解在 P3 後語意會分裂(Business Workflow 走 `/business-workflows/validate`、Agent Skill 走 `/agent-skills/validate-package`),屬輕量文件同步,非功能性風險。
- 六支 verify 腳本本身已屬「evidence 鏈」,已在第 3 節逐一核對——僅 `verify-copilot-shared-core.ps1` 的 C-07 案例是真正的路由依賴,且是**唯一** evidence 鏈中因 P3 而會壞的一環。

#### 6. CI 接點(`.github/workflows/ci.yml`)

- **`backend` job**：使用 GitHub Actions `services.appdb`(`pgvector/pgvector:pg17`),`POSTGRES_DB: springaitest`,**每次 run 都是全新容器、全新空庫**(job 內註解第 21 行已自陳此假設:「service container 每次 run 都是全新空 DB」)。這與 02-spec §6.1 對 CI 的要求(「CI provisions a fresh, empty, disposable PostgreSQL instance per run... so CI exercises the `empty` classification branch」)**已經天然吻合**——CI 每次都是空庫,P3 換成 `DbMigrationRunner` 後,CI 会走「empty → 自動初始化」分支,**不需要**02-spec §6.1 提到的「開發者一次性 `migrate-db.* --confirm`」步驟(那是給既有本機/舊庫用的)。**結論:P3 對 `backend` job 本身無需改動**,只要 `PostgresFixture.InitializeAsync` 切到 runner 的 startup-mode 分類邏輯後,對「空庫」的初始化行為與現在的 `DbBootstrap.RunAsync` 等價即可(行為契約,非 CI job YAML 本身)。
- **`full-chain-smoke` job**：直接呼叫 `./scripts/verify-copilot-shared-core.ps1 -Rebuild -CiComposeSubset`——**會受 P3 影響**,因為該腳本 C-07 案例打 `/api/skills/rag-qa/invoke`(第 3 節已列),必須與腳本修正同 tranche 進 CI,否則此 job 在 P3 後會紅。
- **`compose-and-scripts` job**：僅做 compose config 展開與靜態檢查(環境變數、JWT 契約標記、硬化檢查、shell 語法),未展開任何 skill 路由斷言,**不受 P3 影響**。
- **`platform`/`frontend`/`container-hardening` job**：CI 定義層面均與 skill 路由無關,**不受 P3 影響**(platform 內部測試內容本身可能受 P3 影響,但那是 platform 側盤點範圍,非本次任務)。

#### 匯總風險排序(供 P3 排期參考)

1. **高**：`scripts/verify-copilot-shared-core.ps1` C-07 案例 + CI `full-chain-smoke` job — 必須與 P3 路由切分同 tranche 修,否則 CI 直接紅。
2. **高**：`backend/AGENTS.md`「Artifact storage and dual-track contract」/「Business Workflow writes and export」整段、`workflow/AGENTS.md` alias 措辭與 05-ledger 決策矛盾、`README.md` 361–419 行整節——P3-7 文件同步的主要工作量集中處。
3. **中**：`SkillsBusinessWorkflowsDualTrackConsistencyTests.cs`、`SkillPackageMigrationTests.cs`、`test_skills_api.py` alias 案例——ledger 已明確列為刪除,執行面直接依 §7 操作即可。
4. **低**：`docs/agent-platform-contracts.md` 兩處措辭收斂、`infra/docker-compose.yml` 註解——文字精確化,無功能風險。
5. **待確認/非阻塞**：`verify-agent-runtime-d3.ps1` 的 `skill_bindings`/`skill_not_pinned` 欄位名稱是否隨 DTO 重構改名(05-ledger 未明列,需 P3 實作時對照 DTO 變更確認)。

---

## 9. 執行序對照

| 步驟 | 對應章節 | 輸入報告 | 關鍵風險(一行) |
| --- | --- | --- | --- |
| P3-1 runner + SQL bundle(`0001`/`0002`/`0003`) | 第 3 節 | SQL 目標 schema | 8 項待裁決(§1.1)未解前無法定稿 DDL;40 條匿名 CHECK 須顯式命名,否則 fingerprint 不可重現 |
| P3-2 fixture 切換(`PostgresFixture`/`MigrationManifest` 上線) | 第 4 節(§4.1 MigrationManifest/PostgresFixture 部分)+ 第 8 節 | backend 消費圖 + contracts/tests | `ProductionManifestAbsenceTests.cs:27-29` 三個 `DoesNotContain` 斷言需反轉為 `Contains`,且必須與 `0001`/`0002`/`0003` 實際檔名同步(§2.2 第 5 條) |
| P3-3 backend(`skill`/`skill_revision` 拆表 + 相依程式) | 第 4 節 | backend 消費圖 | `SkillHash` 實際 56 檔/320 處呼叫點(非 ledger 記錄的 24 檔),`SkillMetadata` union 需依 02-spec §3.4 拆成兩型別;P1 錯誤 envelope(`ApiErrorCodes.ForStatus`)必須從一開始套用到新路由,不可事後補 |
| P3-4 workflow(路由 + 5 個 kind-dispatch seam 拆分) | 第 5 節 | workflow 拆分縫 | `snapshot()` fixture 改簽名牽動約 129 個測試(5 個 importer 檔的遞移依賴);`POST /business-workflows/validate` 已存在,只需刪 alias decorator,不要整支重寫 |
| P3-5 platform(`SkillDtos` union 拆分 + `BusinessWorkflowController` 補路由) | 第 6 節 | platform 拆分面 | `BusinessWorkflowController` 缺 4 條路由(revisions/restore/invoke/catalog);3 處 kind assertion 拆除後,`SkillServiceTests.cs:256-330` 是唯一覆蓋 `BackendErrorMapper` 全狀態碼矩陣的測試,刪 `CreateAsync` 前必須先搬斷言 |
| P3-6 frontend(型別/API client/元件三層拆分) | 第 7 節 | frontend 拆分面 | 9 個型別消費檔中 4 個需重寫、2 個需分流;`skillConcept.ui.spec.ts` 的跨 kind 回溯測試段(`:154-216`)因不變式消失須整段刪,而非改斷言 |
| P3-7 測試清理 + 文件同步 | 第 8 節 | contracts/tests | `scripts/verify-copilot-shared-core.ps1` C-07 案例是 CI `full-chain-smoke` job 的直接依賴,不同 tranche 修就會讓 CI 直接紅;`README.md` 361-419 行整節與 `backend/AGENTS.md` 兩段是文件同步主要工作量 |
