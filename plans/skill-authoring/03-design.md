# 設計文稿 — Skill 撰寫 × 系統設定

> 狀態: **已封存的設計記錄。** 現行 Skill 作者體驗以 [settings-skill-redesign](../settings-skill-redesign/01-plan.md) 與 [plans README](../README.md) 為準。
> 下文均為封存時的歷史設計，不是現行實作或驗收依據。

## 1. 歷史架構草稿

沿用既有信任邊界,不新增對外面向:

```
瀏覽器(JWT)
   │  apiFetch → /api/*
   ▼
platform :8080  (JWT 驗證)
   ├── /api/config      → backend :8002   (現有)
   ├── /api/skills      → backend :8002   (新增,X-Internal-Token + identity headers,ADMIN)
   └── /api/workflows   → workflow :8001  (現有;P3 清單合併 user skills)
            ▼
        workflow :8001  (P3:invoke user skill 時,向 backend 取 script → 沙箱執行)
            └── sandbox executor  (P3,隔離容器)
   ▼
backend :8002  (Dapper + appdb / PostgreSQL)  ── skill 資料表(新增)
```

- Skill 的**持久化 owner 是 backend**(與 Config 同源同路)。
- workflow 服務只在 invoke 時向 backend 讀 script;不自持有資料。
- 沙箱是獨立元件,workflow 服務呼叫它,不在自身行程內執行使用者碼。

## 2. 歷史 DB Schema 草稿(已由第 0 節取代)

新增資料表。租戶隔離沿用既有欄位慣例。

**本表為過渡設計,已被 [node-first-skill-engine 的 schema](../node-first-skill-engine/03-design.md)(`skill` + `skill_revision`,`flow/logic/script` 收斂為 `definition`)取代。P2 動工時直接建新 schema,勿先建本表再遷移;匯出功能(第 9 節)資料來源對接新 schema。**

```sql
CREATE TABLE skill (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL,
    name          text NOT NULL,
    description   text NOT NULL,
    flow          text NOT NULL DEFAULT '',   -- 流程 (markdown)
    logic         text NOT NULL DEFAULT '',   -- 商業邏輯 (markdown)
    script        text NOT NULL DEFAULT '',   -- Python 原始碼
    required_role text NOT NULL DEFAULT 'USER',
    enabled       boolean NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_skill_tenant_name UNIQUE (tenant_id, name)
);

CREATE INDEX ix_skill_tenant_enabled ON skill (tenant_id, enabled);
```

- `name` 於租戶內唯一(路由鍵)。跨表(vs code 註冊工作流)的唯一性由應用層檢查,不入 DB。
- 遷移方式沿用 backend 現有 schema 管理慣例(見 [backend/AGENTS.md](../../backend/AGENTS.md))。
- 稽核事件另落既有 log/audit 機制,不塞進本表。

## 3. 歷史後端整合草稿(已由第 0 節取代)

新增 feature folder `Skills/`(比照現有 `Config/`):

- `SkillController.cs` — CRUD + `export`;要求 `X-Internal-Token` + identity headers;寫入操作要求角色 ADMIN(比照 Config PUT)。
- `SkillRepository.cs`(Dapper) — 對 `skill` 表 CRUD,租戶過濾。
- `SkillDtos.cs` — `SkillInfo` / `Skill` / `SkillUpsert`(snake_case 序列化)。
- `SkillExporter.cs` — 由 Skill 組 `SKILL.md` 字串 + `scripts/main.py`,打包成 zip(用 .NET `System.IO.Compression`,零新依賴)。

驗證:`name` 正則、長度、租戶內唯一 + 與工作流名不衝突(呼叫 workflow `GET /workflows` 或維護保留字清單);違反回 ApiError `fieldErrors`。

## 4. 歷史 platform 整合草稿(已由第 0 節取代)

- 新增 `SkillController.cs`(比照 [ConfigController.cs](../../platform/src/Platform.Web/Controllers/ConfigController.cs)):JWT 後,代理 backend `/api/skills/*`,原樣轉發 backend 的 ApiError 與狀態碼(403/404/409)。
- `SkillService.cs` + DTO(比照 `WorkflowService`/`ConfigService`)。

## 5. 歷史 workflow 服務整合草稿(作廢)

本節已作廢:invoke 與清單合併由 node-first-skill-engine 的引擎與 `/skills` 端點承載。

## 6. 歷史前端整合草稿(已由第 0 節取代)

- `src/api/skills.ts`(新增,走 `apiFetch`):`listSkills` / `getSkill` / `createSkill` / `updateSkill` / `deleteSkill` / `exportSkill`(回 blob 觸發下載)。
- `src/types.ts`:新增 `SkillInfo` / `Skill` / `SkillUpsert`(snake_case,對齊 workflow 型別風格)。
- `ConfigView.tsx` 改為分頁容器;現有 key/value 表抽成 `<GeneralConfigTab>`,新增 `<WorkflowsConfigTab>`(唯讀,重用 `GET /api/workflows`)、`<SkillsTab>`。
- `SkillsTab`:清單 + `<SkillEditor>`(五欄位表單)。狀態沿用 Toast(儲存成功)、Skeleton(首次載入)、ErrorBoundary。

## 7. 歷史 UI/UX 草稿(已由第 0 節取代)

### 7.1 版面

系統設定頁頂部三個分頁,內容區依選取切換:

```
┌ 系統設定 ────────────────────────────────┐
│ [ 一般設定 ] [ 工作流 ] [ Skill ]         │  ← tabs(ADMIN-only 頁面)
├──────────────────────────────────────────┤
│  (一般設定) 現有 Key/Value 表,原樣         │
│                                            │
│  (工作流)  唯讀清單:                        │
│    ▸ rag_qa      [USER]   檢索增強問答…      │
│    ▸ analyze_..  [ADMIN]  檢索租戶文件…      │
│                                            │
│  (Skill)  左清單 + 右編輯器                 │
│    ┌ 清單 ─┐ ┌ 編輯器 ──────────────────┐  │
│    │ + 新增│ │ 名稱   [___________]      │  │
│    │ echo  │ │ 描述   [___________]      │  │
│    │ ...   │ │ 流程   [textarea md]      │  │
│    │       │ │ 商業邏輯[textarea md]     │  │
│    │       │ │ Script [textarea mono]    │  │
│    │       │ │ 角色 [USER▾] 啟用[✓]      │  │
│    │       │ │ [儲存] [匯出] [刪除]      │  │
│    └───────┘ └──────────────────────────┘  │
└──────────────────────────────────────────┘
```

### 7.2 互動與狀態

- **Tabs**:純前端狀態切換(無 react-router,比照 AppShell 的 useState 慣例)。
- **Skill 編輯器**:`name` 即時驗證(正則 + 重複);未過不可儲存。儲存成功 → Toast「已儲存」。
- **Script 欄位**:MVP 用 monospace `<textarea>`(零依賴);升級 CodeMirror 需授權(見計劃書待決 2)。
- **匯出**:按鈕 → 下載 `<name>.zip`。
- **刪除**:二次確認(重用既有確認樣式);成功 Toast。
- **載入/錯誤**:首次載入 Skeleton;錯誤 `role="alert"` 顯示 ApiError message;非「無資料」誤render。
- **角色閘門**:系統設定整頁 ADMIN-only(側欄過濾);UI 檢查僅 UX,後端角色把關為真正邊界。

### 7.3 無障礙 / 樣式

- 顏色一律用 `index.css` 語意 token(`--danger/--success/--warning` 等),明暗雙模式對比達 WCAG AA 4.5:1。
- 動畫在 `prefers-reduced-motion` 保護內;互動元件靠全域 `:focus-visible`。
- 角色徽章沿用工作流頁既有 ADMIN/USER 樣式。

## 8. 執行 runtime 與沙箱設計(P3,核心)(作廢)

> **本節已作廢**:沙箱設計移至 [node-first-skill-engine 規格](../node-first-skill-engine/02-spec.md) 第 5 節。以下保留供歷史對照。
> 動工前須先拍板沙箱路線(計劃書待決 1)。以下為預設方案。

### 8.1 執行流

```
workflow :8001  invoke(name, input)
  → 取 skill.script (向 backend)
  → 起「沙箱執行器」子行程/容器
      stdin  ← JSON(input)
      限制:非 root、read-only FS、無網路*、seccomp、
            記憶體上限、CPU 上限、時間上限(=timeout_seconds)
      stdout → JSON(output)
  → 逾時→504 / 非零退出→500 / stdout 非 JSON→500 exec_failed
  → 回 {workflow, output}
* 若允許存取 backend 檢索,僅開單一白名單出口,經受控代理注入 identity headers。
```

### 8.2 隔離強度光譜(擇一)

| 方案                             | 隔離度             | 成本  | 備註                       |
| -------------------------------- | ------------------ | ----- | -------------------------- |
| 鎖定容器(非 root/RO FS/資源限制) | 最低可接受         | 低—中 | MVP 底線                   |
| nsjail                           | 中                 | 中    | namespace + seccomp 收斂   |
| gVisor                           | 高                 | 中—高 | 使用者態核心攔 syscall     |
| Firecracker microVM              | 最高               | 高    | 每次 invoke 一台 microVM   |
| **agent 委派(不自建沙箱)**       | 交給 agent runtime | 最低  | 免第 8 節整段;執行語意改變 |

### 8.3 密鑰與資源

- `INTERNAL_API_TOKEN`、`JWT_SECRET` **絕不**進沙箱環境變數或檔案。
- 每租戶執行頻率 / 併發 / 資源配額,防 DoS。
- 每次 invoke 落稽核 log(誰、哪個 skill、input/output 摘要、耗時、結果)。

## 9. 歷史匯出 / 轉檔草稿(已由第 0 節取代)

- 後端 `SkillExporter`:字串組裝 `SKILL.md`(frontmatter `name`+`description`;body = `## 流程`+flow+`## 商業邏輯`+logic+`## Script` 說明)+ `scripts/main.py`(= script 原文)。
- 用 .NET `System.IO.Compression.ZipArchive` 打包,回 `application/zip`,檔名 `<name>.zip`。
- 純資料轉換,不執行任何使用者碼,無安全風險。

## 10. 歷史端到端資料流草稿(已由第 0 節取代)

```
建立/編輯:
  瀏覽器 → POST /api/skills → platform(JWT) → backend(ADMIN, X-Internal-Token)
         → 寫 skill 表 → 回 Skill → 前端 Toast

匯出:
  瀏覽器 → GET /api/skills/{name}/export → platform → backend
         → 組 SKILL.md + scripts/main.py → zip → 下載

(作廢,執行流見 node-first 設計文稿第 6 節)
執行(P3):
  瀏覽器/呼叫端 → POST /api/workflows/{name} → platform → workflow :8001
         → 命中 user skill → 取 script(backend) → 沙箱執行(stdin/stdout)
         → {workflow, output} → 回傳
```
