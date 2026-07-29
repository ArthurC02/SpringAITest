"""Phase E2 versioned eval runner（規格 plans/agent-architecture-improvements/02-evaluation-observability-plan.md §5）。

Authority 邊界：Workflow 只執行 eval candidate，不保存 release state、不建 suite
catalog——suite 定義由 Backend 以 request payload 傳入，結果由 response 回傳，
本服務對 eval 全程 stateless。
"""
