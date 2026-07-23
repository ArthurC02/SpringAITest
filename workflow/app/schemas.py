from typing import Any, Literal

from pydantic import BaseModel, Field

from app.engine.skill import InputField, SkillError, SkillMeta


class InvokeRequest(BaseModel):
    """呼叫 skill 的請求本體：input 會（移除保留鍵後）當成圖的初始 state 傳入。"""

    input: dict[str, Any]


class SkillInfo(BaseModel):
    """Skill 清單項目：供 GET /skills 回傳。source 為 "builtin"（repo 檔案）或 "custom"（來自 backend）。"""

    name: str
    description: str
    required_role: str
    source: str
    revision: int
    # additive contract：舊的內部 producer/fake 未帶 kind 時仍視為 flow；FastAPI response
    # 會把預設值序列化，因此 catalog 對外一律明確輸出 kind。
    kind: Literal["flow", "agentic"] = "flow"
    input_schema: dict[str, InputField] | None = None
    # 內建 template_* 骨架的 YAML 原文,供前端 compose 定點 patch;custom 項不帶（None）。
    definition: str | None = None


class SkillValidateRequest(BaseModel):
    """POST /skills/validate 的請求本體：definition 是 skill 定義的 YAML 原文。

    刻意收 YAML 原文而不是已解析的物件：解析失敗本身就是要回報的錯誤之一
    （invalid_flow），前端編輯器送出的也正是編輯中的原文。
    """

    definition: str


class SkillInvokeResponse(BaseModel):
    """呼叫 skill 的回應本體：output 為圖執行結束後的最終 state（已剝除引擎內部鍵）。"""

    skill: str
    output: dict[str, Any]


class PackageSkillMeta(SkillMeta):
    """validate-package 回應沿用公開 SkillMeta；internal 額外資料位於外層 result。"""

    pass


class PackageManifest(BaseModel):
    """package 清單：正規化後的 entry 路徑與原始 zip bytes 的 SHA-256。"""

    entries: list[str]
    sha256: str


class ValidatePackageResult(BaseModel):
    """POST /skills/validate-package 的回應（設計 §2.2）：既有 validation 回應的 internal superset。

    canonical_definition 與 package_manifest 僅在此出現，不擴張公開 validator 契約。
    invalid（valid=false）時以 exclude_none 保證不出現任何可被寫入的 metadata/definition。
    """

    valid: bool
    errors: list[SkillError] = Field(default_factory=list)
    skill: PackageSkillMeta | None = None
    canonical_definition: str | None = None
    package_manifest: PackageManifest | None = None


class NodeInfo(BaseModel):
    """節點目錄項目：供 GET /nodes 回傳節點的 I/O 契約（Skill 編輯器據此組流程）。"""

    name: str
    version: str
    description: str
    reads: list[str]
    writes: list[str]
    requires_tools: list[str]
