"""引擎：Node 為一等公民的執行基礎設施（Node Registry + Harness）。

工作流不再擁有節點；節點以 @node 註冊成具名、版本化、宣告 I/O 契約的一等公民，
圖只是把註冊表裡的節點組起來的結果（由 compiler.py 依 Skill YAML 編譯）。
"""
