# Workflow Designer UI 技術評估

> **Superseded retirement policy (2026-08-03):** R6 的共存、流量門檻與 rollback-window 退場策略已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 取代；原文保留為歷史理由。現行程式仍遵守本文件，直到 hard-reset 對應 phase 實作並通過 gate。

> 決策：v1 選用 `@xyflow/react`（React Flow）作視覺畫布，搭配 ELK.js 自動排版；Backend/appdb 持久化 Graph IR drafts/revisions，Workflow server 是語意驗證、canonicalization、LangGraph 編譯與執行的唯一權威。
>
> 產品定位：這是 SYSTEM_ADMIN 使用的 Agent Execution Harness Designer。n8n-like 指操作方式，不代表建立通用商業自動化平台。

## 1. 需求

SYSTEM_ADMIN 需要類似 n8n 的 Web UI：

- node palette、搜尋、拖放、連線、縮放、minimap。
- React custom nodes 與 properties form。
- typed input/output/control ports 與 connection validation。
- branch、bounded loop、fan-out/fan-in、Agent dispatch、Verifier、aggregation。
- draft autosave、undo/redo、validate、simulate、publish、revision diff/rollback。
- P3 以 stable node ID 顯示 simulated trace；P4 接入真實 root/child 唯讀 trace overlay。
- 可嵌入現有 React 19 + Vite + TypeScript SPA。
- 前端不得成為第二套 workflow runtime。
- Node Catalog 聚焦 preflight、Context、Agent step、policy/tool gate、checkpoint、retry、verification、cleanup 等 Harness primitives，不放 tenant-specific business steps。

## 2. 候選比較

| 候選 | 優點 | 主要問題 | 結論 |
| --- | --- | --- | --- |
| React Flow (`@xyflow/react`) | 原生 React/TypeScript、custom nodes、多 handles、subflow grouping、connection validation、save/restore、MIT；與現有前端最貼合 | 只有 UI；typed semantics、Graph validation、execution、undo/redo 等產品能力仍需自行建置；layout 需 Dagre/ELK | **首選** |
| Rete.js v2 | React renderer、typed sockets、data/control-flow engines、plugin 生態完整 | 多 package 整合較重；若使用其 engine 會與 LangGraph形成雙 runtime；部分 advanced plugins 採非商用授權 | 第二候選，不採 engine/scopes |
| JointJS / `@joint/react` | 成熟的 ports、graph model、React integration、大圖能力 | Core 為 MPL-2.0；許多 n8n-like 完整 editor 功能集中在商業 JointJS+ | 願意採購企業工具時再評估 |
| BaklavaJS | MIT、TypeScript、typed interfaces、subgraphs、engine | 官方 renderer 是 VueJS；React 專案需混載 Vue 或自寫 renderer | 不選 |
| LiteGraph.js | MIT、Canvas2D、slots/widgets、JSON 與 subgraphs | 無原生 React；表單、CSS、可及性與 React state 整合較弱；自帶 engine 與 LangGraph 重疊 | 不選 |
| n8n editor | UX 是很好的參考，功能完整 | n8n 是 Vue 應用而非通用 React canvas；Sustainable Use License 對商業嵌入有限制 | 只借鏡 UX，不 fork/embed |
| LangSmith Studio | 適合查看 graph、state、trace、thread 與除錯 | 定位是開發／觀測工具，不是可嵌入產品的拖拉拓樸 authoring SDK | 可作 trace UX 參考 |

## 3. 選擇 React Flow 的理由

React Flow 官方將它定位為建立 node-based UI 的可客製 React component，核心包含拖曳、縮放、平移、選取與增刪；custom nodes 可放任意 React 內容，多個 handles 可表達不同 ports。官方也提供 connection validation、save/restore、subflow/grouping 與 ELK/Dagre layout 範例。

它沒有宣稱執行 workflow，反而符合本案邊界：畫布只產生產品自有 Graph IR，Python Workflow service 才做權威驗證與 LangGraph 編譯。

## 4. 建議前端組成

```text
WorkflowDesigner
  ├─ NodePalette / NodeSearch
  ├─ WorkflowCanvas (@xyflow/react)
  │   ├─ custom WorkflowNode
  │   ├─ typed handles
  │   └─ custom conditional edges
  ├─ NodePropertyInspector
  ├─ ValidationPanel
  ├─ GraphOutline / MiniMap
  ├─ ELK AutoLayout
  ├─ RevisionDiff / PublishBar
  ├─ SimulationConsole
  └─ TraceOverlay
```

Node Catalog 從 server 取得並產生 typed TypeScript union。Frontend `isValidConnection` 只做即時提示；server validate/publish 仍重新檢查。

## 5. 儲存邊界

不要把 React Flow state 直接當 runtime definition：

```text
WorkflowDefinition
  semantic Graph IR:
    nodes(type/version/config)
    edges(source port/target port)
    governance

WorkflowUiMetadata
  positions
  viewport
  visual groups
  collapsed state
```

- Semantic definition 與 UI metadata 分開 hash/diff，但共用 draft ETag/version；只移動節點不會改變 semantic definition hash。
- React Flow `parentId` 只代表視覺 grouping；未來真正 subflow 使用 `Call Workflow` + pinned revision，P3 MVP 不開放。
- 瀏覽器不產生 Python、LangGraph code 或可執行 script。

## 6. MVP 與後續

### MVP

- 使用固定 Orchestrator 與 Agent-Runtime templates；兩者共用 Graph IR/Designer，但 Node Catalog 與必要治理階段依 `kind` 分開。
- SYSTEM_ADMIN 可編輯節點參數、少數安全 edges、fan-out/timeout/repair budgets。
- Node Catalog 先包含 Context、Analyze、Sufficiency、Decompose、Dispatch、Join、Verifier、Repair、Aggregate、Respond。
- Agent-Runtime Node Catalog 先包含 Intake、Load Skill、Model Step、Read Tool、Approval/Policy Gate、Validate Output、Return。
- identity/authority/global budget/terminal cleanup/audit 採 compiler-owned wrapper，不呈現成可刪除 nodes；stage 歸屬以 02-spec §7.3 對照表為權威；Worker 與 Verifier variants 由 server 強制不同 tool/output invariants。
- Node 的 authoring capability 與 runtime policy 分離；`workflow.manage` 不參與一般使用者正式 run 的授權交集。
- governance shell、identity、audit、approval、terminal error path 鎖定。
- server validation、simulation、publish、rollback、simulated trace overlay。

### 後續

- Call Workflow、版本化 subflows。
- copy/paste、keyboard shortcuts、協作與註解。
- 大型 graph virtualisation/performance tuning。
- workflow templates/marketplace。
- 僅在採購決策後考慮 React Flow Pro 或 JointJS+ 功能。

## 7. 官方資料

- [React Flow 首頁、核心能力與 MIT license](https://reactflow.dev/)
- [React Flow custom handles](https://reactflow.dev/learn/customization/handles)
- [React Flow TypeScript types](https://reactflow.dev/learn/advanced-use/typescript)
- [React Flow examples：validation、save/restore、subflows、layout](https://reactflow.dev/examples)
- [React Flow layout 建議：Dagre 到 ELK](https://reactflow.dev/learn/layouting/layouting)
- [Rete.js v2：React renderer 與 data/control-flow engines](https://retejs.org/docs/)
- [Rete.js server/Python engine 說明](https://retejs.org/docs/faq/)
- [Rete.js licensing：部分 advanced plugins 非商用](https://retejs.org/docs/licensing/)
- [JointJS/JointsJS+ license](https://www.jointjs.com/license)
- [JointJS+ pricing](https://www.jointjs.com/pricing)
- [BaklavaJS：VueJS renderer、TypeScript/plugin 架構](https://baklava.tech/)
- [LiteGraph.js official repository](https://github.com/jagenjo/litegraph.js/)
- [n8n Sustainable Use License](https://github.com/n8n-io/n8n/blob/master/LICENSE.md)
- [LangSmith Studio](https://docs.langchain.com/langsmith/studio)
