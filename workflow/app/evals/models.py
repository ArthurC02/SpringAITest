"""POST /evals/run 的請求／回應 schema（跨服務 contract，snake_case 同 skill invoke 慣例）。

candidate.ref 的內層形狀是本服務自訂的解析慣例（非跨服務定案的一部分）：kind="skill" 時
取 `ref["name"]`，與既有 `/skills/{name}/invoke` 的名稱查詢方式一致。
"""

from typing import Any, Literal

from pydantic import BaseModel, Field


class EvalCase(BaseModel):
    """單一 eval case。deterministic fixture 與 recorded replay 共用同一形狀——兩者
    機制相同，差別只在 fixtures 資料的來源是手作或去識別後的錄製回應（見 §5.2）。
    未知的 mode 值（例如尚未實作的 live shadow）在此以 Literal 收斂成請求層級 422。
    """

    case_id: str
    mode: Literal["deterministic", "replay"]
    input: dict[str, Any] = Field(default_factory=dict)
    expected: dict[str, Any] = Field(default_factory=dict)
    fixtures: dict[str, Any] = Field(default_factory=dict)


class EvalSuite(BaseModel):
    suite_id: str
    revision: int
    # 上限 200：單一請求不得無界——case 數量直接等於 runner 逐一同步執行的圖編譯／
    # 執行次數，無上限等於請求層級的資源放大攻擊面。
    cases: list[EvalCase] = Field(default_factory=list, max_length=200)


class EvalCandidate(BaseModel):
    """本階段只執行 kind="skill"；"agent" 是契約預留形狀，尚未實作（422 fail closed）。"""

    kind: Literal["skill", "agent"]
    ref: dict[str, Any] = Field(default_factory=dict)
    # snapshot/prompt/model 識別等，原樣 echo 進每個 case 的 canonical_identity 計算，
    # workflow 不解讀其內容。型別容許 None：.NET System.Text.Json 的 Web defaults
    # 不省略 null 欄位，backend 送出省略 pins 鍵的合法請求時仍可能序列化成
    # "pins": null，兩者（以及明確傳入 {}）在呼叫端（app.evals.api）一律正規化為
    # 同一個 canonical identity。
    pins: dict[str, Any] | None = Field(default=None)


class EvalRunnerOptions(BaseModel):
    # gt=0：0 或負值的預算沒有語意（budget_ms 未提供＝不設預算，用 None 表達，不是 0）。
    budget_ms: int | None = Field(default=None, gt=0)


class EvalRunRequest(BaseModel):
    suite: EvalSuite
    candidate: EvalCandidate
    runner: EvalRunnerOptions = Field(default_factory=EvalRunnerOptions)


class EvalCaseResult(BaseModel):
    case_id: str
    canonical_identity: str
    verdict: Literal["PASS", "FAIL", "ERROR"]
    metrics: dict[str, Any] = Field(default_factory=dict)
    failure_reason: str | None = None


class EvalRunResponse(BaseModel):
    runner_version: str
    suite_id: str
    revision: int
    started_at: str
    completed_at: str
    cases: list[EvalCaseResult] = Field(default_factory=list)
