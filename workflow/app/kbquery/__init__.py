"""kb_query：可驗證、可追蹤、可稽核的知識查詢工作流（固定節點 + 條件邊 + 驗證閘門）。

不是泛用 ReAct Agent：十個單一職責節點、型別明確的 State、Evidence Verification
強制閘門（未通過不得輸出實質答案）、重試有上限、全程 Audit Trail。
外部服務（檢索、rerank、詞彙字典、稽核儲存）一律經 ports.py 的 Protocol 隔離。
"""
