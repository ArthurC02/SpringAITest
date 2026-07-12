"""測試 registry 的核心行為：重名防呆、以及列表內容。"""

import pytest
from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel

from app.workflows import registry

# 匯入以確保 summarize / triage / rag_qa / analyze_report 已透過 @register 完成註冊。
from app.workflows import analyze_report, rag_qa, summarize, triage  # noqa: F401


def test_duplicate_name_raises_value_error():
    """重複註冊同一個名稱應該立刻拋出 ValueError，且不覆蓋原本的登記。"""
    name = "__throwaway_for_test__"

    def build():
        g = StateGraph(dict)
        g.add_node("noop", lambda state: state)
        g.add_edge(START, "noop")
        g.add_edge("noop", END)
        return g.compile()

    try:
        registry.register(name, "第一次註冊")(build)

        with pytest.raises(ValueError):
            registry.register(name, "第二次註冊，應該失敗")(build)

        # 確認第一次註冊的內容沒有被第二次呼叫影響。
        spec = registry.get(name)
        assert spec is not None
        assert spec.description == "第一次註冊"
    finally:
        # 清理，避免污染其他測試。
        registry._REGISTRY.pop(name, None)


def test_all_specs_contains_summarize_and_triage_sorted():
    names = [spec.name for spec in registry.all_specs()]

    assert "summarize" in names
    assert "triage" in names
    assert names == sorted(names)


def test_unspecified_fields_keep_defaults():
    """既有工作流（summarize / triage）未指定的欄位應維持預設：
    required_role=USER、input_model/timeout_seconds=None（寬鬆）。"""
    assert registry.get("triage").required_role == "USER"

    spec = registry.get("summarize")
    assert spec.required_role == "USER"
    assert spec.input_model is None
    assert spec.timeout_seconds is None


def test_rag_qa_and_analyze_report_declare_role_and_input_model():
    """新增的兩個工作流：rag_qa 為 USER 權限，analyze_report 為 ADMIN 限定，且皆有 input_model。"""
    rag_qa_spec = registry.get("rag_qa")
    assert rag_qa_spec is not None
    assert rag_qa_spec.required_role == "USER"
    assert rag_qa_spec.input_model is not None

    analyze_report_spec = registry.get("analyze_report")
    assert analyze_report_spec is not None
    assert analyze_report_spec.required_role == "ADMIN"
    assert analyze_report_spec.input_model is not None


def test_register_accepts_required_role_input_model_and_timeout():
    """register() 應接受新增的三個參數並正確存入 WorkflowSpec。"""
    name = "__throwaway_for_registry_fields_test__"

    class DummyInput(BaseModel):
        text: str

    def build():
        g = StateGraph(dict)
        g.add_node("noop", lambda state: state)
        g.add_edge(START, "noop")
        g.add_edge("noop", END)
        return g.compile()

    try:
        registry.register(
            name,
            "測試用工作流",
            required_role="ADMIN",
            input_model=DummyInput,
            timeout_seconds=5,
        )(build)

        spec = registry.get(name)
        assert spec is not None
        assert spec.required_role == "ADMIN"
        assert spec.input_model is DummyInput
        assert spec.timeout_seconds == 5
    finally:
        registry._REGISTRY.pop(name, None)
