# Cleanup 與 Consolidation Roadmap

> **Superseded retirement policy (2026-08-02):** C8 的 usage-zero、rollback-window 與相容雙軌退場策略已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 取代；原文保留為歷史理由。現行程式仍遵守本文件，直到 hard-reset 對應 phase 實作並通過 gate。

> Cleanup 不是獨立功能里程碑。只有 replacement 已 authoritative、fallback usage 為零、rollback window 結束且 evidence 可驗證時才能刪除舊路徑。

| Phase | Cleanup                                                                             | 前置 gate                                                                          | 不可刪除                                            |
| ----- | ----------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- | --------------------------------------------------- |
| C1    | 以 extended operations evidence 取代 compile-time legacy inventory                  | E1 dual-write reconciliation 無差異、retention/rollback window 完成                | Historical telemetry、run events、trace IDs         |
| C2    | Regression API 移除 caller-supplied `passed`                                        | E2 runner results authoritative、migration 完成                                    | Historical regression/override audit                |
| C3    | 移除 hard-coded guard/router/summary/persona duplicates 與 frontend prompt defaults | Prompt manifest canary 全綠、fallback usage = 0                                    | Published prompt/model revisions、snapshot pins     |
| C4    | 共用 Runs/trace projection，移除 consoles 重複 polling/formatting                   | Unified Runs center parity/E2E 全綠                                                | Authoring-specific test controls                    |
| C5    | 移除 raw JSON operations production view 與無 owner 的 log-only alerts              | Structured cockpit/recovery metrics authoritative                                  | Developer diagnostics、OTel/Langfuse exporters      |
| C6    | 刪除 in-process script execution                                                    | Isolated adapter parity/adversarial/recovery evidence 全綠，production 無 fallback | AST analyzer、script source revision/hash audit     |
| C7    | 移除 connector duplicate discovery/permission caches                                | Connector catalog/tool-boundary parity 全綠                                        | Native tools、connector revisions、run/effect audit |
| C8    | Skill 概念重整 P5：收斂 public `/api/skills*` 的 flow 寫入／flow export fallback 與 flow 讀取可見性 | `/api/business-workflows*` 已承接流量、舊 Skill 面 flow 寫入 usage `= 0`、rollback window 結束，且 owner 登記 evidence 與人工簽核 | workflow-internal `/skills/validate` alias（另立退場 gate）、`skill`/`skill_revision` 實體表、所有 immutable revisions/run snapshot pins、統一 invoke、R6 legacy-flow runtime |

## 執行規則

1. 每項 cleanup 在原計畫 completion PR 中登記 owner、usage query、rollback deadline 與 deletion evidence。
2. C1/C2 可連續執行；C3、C4、C6 可在各自 replacement 穩定後平行；C7 只在 MCP 真的交付時存在。
3. R6 legacy-flow cleanup 仍由 [../agent-platform-redesign/05-migration-and-rollout.md](../agent-platform-redesign/05-migration-and-rollout.md) 擁有，本文件只引用，不另定 threshold。
4. Immutable revisions、snapshots、approval/effect ledgers、context evidence、eval results 與 security audit records 永不因程式路徑 cleanup 一併刪除。
5. **C8 是 P5 的阻擋 gate，不是事後清理提醒。** 新 Business Workflow 路由存在、測試通過或前端入口完成，都不足以宣告 P5；usage query、rollback deadline、deletion evidence 與人工簽核必須在同一完成 PR 中可審計。
