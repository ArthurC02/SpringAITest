# 顯式 import 各工作流模組：模組頂層的 @register(...) 裝飾器會在 import 當下執行，
# 因此只要這裡有 import 到，該工作流就會被登記進 registry。
# 目前手動列舉；工作流數量變多後，可考慮改用 pkgutil.iter_modules 自動掃描本套件底下的模組。
from app.workflows import analyze_report, rag_qa, summarize, triage  # noqa: F401
