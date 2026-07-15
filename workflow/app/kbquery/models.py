"""kb_query 的 Enum 與 Pydantic 模型：所有節點輸入輸出的共同契約。

規範重點：
- LLM 結構化輸出一律先經這裡的模型驗證，才能寫入 State。
- Audit 相關模型只有「可稽核」欄位（輸入、輸出、決策、證據），
  刻意不設任何可存放模型私有推理過程（Chain of Thought）的欄位。
"""

from enum import StrEnum
from typing import Any

from pydantic import BaseModel, Field, SerializeAsAny


class IntentType(StrEnum):
    SINGLE_VALUE_LOOKUP = "SINGLE_VALUE_LOOKUP"
    TABLE_LOOKUP = "TABLE_LOOKUP"
    COMPARISON = "COMPARISON"
    CALCULATION = "CALCULATION"
    CAUSE_ANALYSIS = "CAUSE_ANALYSIS"
    DOCUMENT_LOCATION = "DOCUMENT_LOCATION"
    PERFORMANCE_ANALYSIS = "PERFORMANCE_ANALYSIS"
    UNKNOWN = "UNKNOWN"


class VerificationResult(StrEnum):
    PASS = "PASS"
    RETRY = "RETRY"
    FAIL = "FAIL"


class AnswerMode(StrEnum):
    ANSWER = "ANSWER"
    ABSTAIN = "ABSTAIN"


class FailureCode(StrEnum):
    PERIOD_MISMATCH = "PERIOD_MISMATCH"
    METRIC_MISMATCH = "METRIC_MISMATCH"
    VERSION_MISMATCH = "VERSION_MISMATCH"
    TABLE_CELL_MISMATCH = "TABLE_CELL_MISMATCH"
    VALUE_MISMATCH = "VALUE_MISMATCH"
    INSUFFICIENT_EVIDENCE = "INSUFFICIENT_EVIDENCE"
    CONFLICTING_EVIDENCE = "CONFLICTING_EVIDENCE"
    CALCULATION_ERROR = "CALCULATION_ERROR"
    SOURCE_NOT_TRACEABLE = "SOURCE_NOT_TRACEABLE"


class IssueLabel(StrEnum):
    QUERY_REWRITE_ERROR = "QUERY_REWRITE_ERROR"
    INTENT_CLASSIFICATION_ERROR = "INTENT_CLASSIFICATION_ERROR"
    CONTEXT_RESOLUTION_ERROR = "CONTEXT_RESOLUTION_ERROR"
    RETRIEVAL_MISS = "RETRIEVAL_MISS"
    RERANK_ERROR = "RERANK_ERROR"
    DATA_LOCATION_ERROR = "DATA_LOCATION_ERROR"
    EVIDENCE_VERIFICATION_ERROR = "EVIDENCE_VERIFICATION_ERROR"
    CALCULATION_ERROR = "CALCULATION_ERROR"
    ANSWER_FORMAT_ERROR = "ANSWER_FORMAT_ERROR"
    SOURCE_VERSION_ERROR = "SOURCE_VERSION_ERROR"


class QueryRewriteOutput(BaseModel):
    """Query Rewrite 節點要求 LLM 回傳的結構化輸出。

    rewrite_reason 限制長度：只允許簡短、可稽核的改寫摘要，不是思考過程。
    """

    normalized_query: str = Field(min_length=1)
    query_variants: list[str] = Field(min_length=1, max_length=5)
    rewrite_reason: str = Field(default="", max_length=200)


class IntentOutput(BaseModel):
    """Intent Classification 節點要求 LLM 回傳的結構化輸出。"""

    intent_type: IntentType
    question_type: str = ""
    confidence: float = Field(ge=0.0, le=1.0)


class SourceResult(BaseModel):
    """單筆檢索結果：保留原始分數、rerank 分數與排序理由，供稽核追蹤。"""

    source_id: str
    document_id: str
    document_title: str
    version: str = ""
    page: int | None = None
    source_type: str  # "text" | "table" | "structured"
    retrieval_method: str  # "vector" | "keyword" | "metadata" | "table" | "structured"
    original_score: float
    rerank_score: float | None = None
    rerank_reason: str = ""
    metadata: dict[str, Any] = Field(default_factory=dict)


class Evidence(BaseModel):
    """可追溯的證據：必須能定位到文件頁面、表格儲存格或結構化資料列。"""

    source_id: str
    document_id: str
    document_title: str
    document_version: str = ""
    source_type: str
    page_number: int | None = None
    section: str = ""
    exact_excerpt: str = ""
    exact_value: str = ""
    table_name: str = ""
    sheet_name: str = ""
    row_identifier: str = ""
    column_identifier: str = ""
    metric: str = ""
    period: str = ""
    unit: str = ""
    locator_score: float = 0.0


class CalculationTrace(BaseModel):
    """確定性計算的完整軌跡：公式、輸入值、輸入值出處與結果。"""

    formula: str
    inputs: dict[str, float]
    input_sources: list[str] = Field(default_factory=list)  # 對應 Evidence.source_id
    result: float


class RetrievalPlan(BaseModel):
    """Retrieval Planner 的產出：查哪些來源、怎麼查、怎麼排。"""

    methods: list[str]  # 啟用的檢索方法，同時是 searcher adapter 的 key
    source_priority: list[str]
    use_keyword: bool = False
    use_vector: bool = False
    use_metadata: bool = False
    use_table: bool = False
    use_structured: bool = False
    top_k: int = 8
    rerank_policy: str = "score_with_context_boost"
    adjustment_reason: str = ""  # 重試時記錄「依哪個 failure code 做了什麼調整」


class Citation(BaseModel):
    document_title: str
    document_version: str = ""
    page: int | None = None
    table_name: str = ""
    sheet_name: str = ""
    row: str = ""
    column: str = ""


class TraceEntry(BaseModel):
    """單一節點的執行紀錄；input/output summary 只放欄位鍵名摘要，不放原文內容。"""

    node_name: str
    start_time: str
    end_time: str
    latency_ms: float
    status: str  # "ok" | "error" | "skipped"
    input_summary: str = ""
    output_summary: str = ""
    error_code: str = ""
    component_version: str = ""  # model / tool / index 版本（可取得時）


class RegressionTestItem(BaseModel):
    """錯題轉成的回歸測試項目。"""

    question: str
    expected_intent: IntentType | None = None
    expected_period: str = ""
    expected_metric: str = ""
    expected_sources: list[str] = Field(default_factory=list)
    expected_evidence_location: str = ""
    expected_answer_rule: str = ""
    original_issue: IssueLabel | None = None


class AuditTrail(BaseModel):
    """一次查詢的完整稽核紀錄。欄位固定，無自由欄位可夾帶模型推理文字。"""

    query_id: str
    query_timestamp: str = ""
    original_query: str
    normalized_query: str = ""
    query_variants: list[str] = Field(default_factory=list)
    intent_type: IntentType | None = None
    resolved_context: dict[str, Any] = Field(default_factory=dict)
    retrieval_plans: list[RetrievalPlan] = Field(default_factory=list)
    ranked_sources: list[SourceResult] = Field(default_factory=list)
    selected_evidence: list[Evidence] = Field(default_factory=list)
    verification_result: VerificationResult | None = None
    failure_codes: list[FailureCode] = Field(default_factory=list)
    confidence: float | None = None
    answer_mode: AnswerMode | None = None
    final_answer: str = ""
    source_citations: list[Citation] = Field(default_factory=list)
    calculation_trace: CalculationTrace | None = None
    retry_count: int = 0
    # SerializeAsAny：script／tool 步驟的 entry 是 TraceEntry 的子類（多帶 script_sha256、
    # tool、args_keys）。不加這個標記，pydantic 會照宣告型別序列化，稽核紀錄落地成 JSON 時
    # 就會把 script 的 SHA-256 丟掉（AT3-14 要求 hash 進 audit trail）。
    node_trace: list[SerializeAsAny[TraceEntry]] = Field(default_factory=list)
    errors: list[dict[str, Any]] = Field(default_factory=list)
    user_feedback: str = ""
