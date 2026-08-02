"""script 步驟經 Node Shell 執行的行為（AT3-12 ~ AT3-14、AT-GOV-03、AT-GOV-04）。

治理硬規則對 script 與對 node 是**同一套**：script 步驟不另開一條繞過 Node Shell 的
執行路徑 —— 所以 fatal 短路、保留鍵不可覆寫、稽核強制附加，對 script 一體適用。
"""

import hashlib

import pytest

from app.engine import compiler, node_registry
from app.engine.node_shell import (
    CONFIG_SEED_KEYS,
    IDENTITY_KEYS,
    RUNTIME_AUTHORITY_KEYS,
)
from app.engine.script_runner import MAX_WRITE_BYTES, ScriptTraceEntry
from app.engine.skill import Skill

# import 觸發 kb_query 節點註冊（compiler 強制附加的 audit_feedback 來自這裡）
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app.nodes.kbquery.models import QueryRewriteOutput
from tests.conftest import default_engine_deps, run_flow
from tests.kbquery_fakes import FakeStructuredLLM

TEST_NODES = ("s_next", "s_authority_observer")

# script 原始碼裡的哨兵：trace／audit 任何角落出現它 = 原始碼全文外洩（AT3-14／AT-GOV-04）
SOURCE_SENTINEL = "SCRIPT_SOURCE_SENTINEL"


@pytest.fixture(autouse=True, scope="module")
def _register_test_nodes():
    @node_registry.node(name="s_next", writes=["next_ran"], description="script 後續節點")
    def make_s_next():
        async def fn(state: dict) -> dict:
            return {"next_ran": True}

        return fn

    @node_registry.node(
        name="s_authority_observer",
        reads=sorted(RUNTIME_AUTHORITY_KEYS),
        writes=["authority_seen"],
        description="驗證下游節點仍讀到 snapshot authority",
    )
    def make_authority_observer():
        async def fn(state: dict) -> dict:
            return {
                "authority_seen": {
                    key: state[key] for key in RUNTIME_AUTHORITY_KEYS
                }
            }

        return fn

    yield
    for name in TEST_NODES:
        node_registry._REGISTRY.pop((name, "1.0"), None)


def _script_entry(result: dict) -> ScriptTraceEntry:
    entries = [e for e in result["trace"] if isinstance(e, ScriptTraceEntry)]
    assert len(entries) == 1
    return entries[0]


def _traced(result: dict) -> list[str]:
    return [e.node_name for e in result["trace"]]


# ---------------------------------------------------------------------------
# 決策表另一半：合法 script 正常寫入 state
# ---------------------------------------------------------------------------


def test_script_step_writes_into_state():
    """規格 §3.1 的範例形狀：script 寫一個自訂鍵，後續節點照跑。"""
    result = run_flow(
        [
            {"script": "state['assumption_note'] = '信心值偏低，建議人工複核'"},
            {"node": "s_next"},
        ]
    )

    assert result["assumption_note"] == "信心值偏低，建議人工複核"
    assert result["next_ran"] is True
    assert _script_entry(result).status == "ok"
    assert _traced(result)[-1] == "audit_feedback"  # 稽核照樣強制附加


# ---------------------------------------------------------------------------
# AT3-12 timeout_ms 逾時 → 走 Node Shell 的 fatal 短路
# ---------------------------------------------------------------------------


def test_script_timeout_triggers_fatal_short_circuit():
    """【AT3-12】跑滿 CPU 的合法 script + timeout_ms=1 → 步驟視為節點錯誤、後續節點不執行。"""
    result = run_flow(
        [
            {
                "script": "x = 0\nfor i in range(1000000):\n    x = i * i",
                "timeout_ms": 1,
            },
            {"node": "s_next"},
        ]
    )

    entry = _script_entry(result)
    assert entry.status == "error"
    assert "Timeout" in entry.error_code  # 錯誤類別含 timeout 標記
    assert "next_ran" not in result  # 後續節點沒跑（fatal 短路）
    assert result["trace"][-2].status == "skipped"  # s_next 被短路
    assert "fatal_error" in result
    assert _traced(result)[-1] == "audit_feedback"  # 稽核仍落地（run_on_fatal）


# ---------------------------------------------------------------------------
# AT3-13 state 寫入超過 256KB → 拒絕（不寫入 state）
# ---------------------------------------------------------------------------


def test_script_oversized_state_write_is_rejected():
    """【AT3-13】script 寫入 300KB → 該步驟視為錯誤，且該鍵不進 state。"""
    result = run_flow([{"script": "state['big'] = 'x' * (300 * 1024)"}, {"node": "s_next"}])

    assert "big" not in result
    entry = _script_entry(result)
    assert entry.status == "error"
    assert entry.error_code == "ScriptLimitExceeded"
    assert "next_ran" not in result


# json.dumps('x' * n) 的位元組數是 n + 2（前後兩個引號）——上限量的是序列化後的大小。
_JSON_QUOTES = 2


@pytest.mark.parametrize(
    ("total_bytes", "kept", "status"),
    [
        (MAX_WRITE_BYTES, True, "ok"),
        (MAX_WRITE_BYTES + 1, False, "error"),
    ],
)
def test_script_state_write_size_boundary(total_bytes, kept, status):
    """【AT3-13 的邊界】上限剛好卡在 256KB：等於上限放行，多 1 位元組即出局。

    300KB 那個測試只證明「夠大會被擋」，上限訂在 200KB 或 290KB 也照樣過關；
    這裡直接吃生產常數 MAX_WRITE_BYTES 的 on-point / off-point。
    """
    result = run_flow([{"script": f"state['big'] = 'x' * {total_bytes - _JSON_QUOTES}"}])

    assert _script_entry(result).status == status
    assert ("big" in result) == kept


# ---------------------------------------------------------------------------
# AT3-14 sha256 入 audit trail，原始碼全文不落 trace
# ---------------------------------------------------------------------------


def test_script_sha256_in_audit_trail_and_source_absent_from_trace():
    """【AT3-14】trace 只有 SHA-256 + 讀寫鍵名 + 耗時 + 狀態，沒有原始碼全文。"""
    source = f"state['note'] = '{SOURCE_SENTINEL}' + str(state['seed'])"
    digest = hashlib.sha256(source.encode("utf-8")).hexdigest()
    deps = default_engine_deps()

    result = run_flow(
        [{"script": source}],
        {"seed": 1},
        deps=deps,
        input_schema={"seed": {"type": "int"}},
    )

    entry = _script_entry(result)
    assert entry.script_sha256 == digest
    assert entry.input_summary == "seed"  # 讀的鍵名
    assert entry.output_summary == "note"  # 寫的鍵名
    assert entry.latency_ms >= 0
    assert entry.status == "ok"

    # trace 不含原始碼全文（連片段都不該有）
    trace_json = "".join(e.model_dump_json() for e in result["trace"])
    assert "state[" not in trace_json
    assert SOURCE_SENTINEL not in trace_json

    # audit trail 落地後仍查得到這段 script 的 hash（序列化不得把它丟掉）
    trail = deps.audit_repo.saved[0]
    trail_json = trail.model_dump_json()
    assert digest in trail_json
    assert SOURCE_SENTINEL not in trail_json
    assert any(
        isinstance(e, ScriptTraceEntry) and e.script_sha256 == digest
        for e in trail.node_trace
    )


# ---------------------------------------------------------------------------
# AT-GOV-03 保留鍵不可被 Script 覆寫
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("key", ["query_id", "original_query", "query_timestamp"])
def test_script_cannot_overwrite_immutable_keys(key):
    """【AT-GOV-03】script 寫保留鍵 → 忽略；引擎注入的原值不變。"""
    result = run_flow(
        [
            {"node": "query_intake"},
            {"script": f"state[{key!r}] = 'forged'\nstate['ran'] = True"},
        ],
        {"query": "2025Q3 稅後淨利是多少"},
    )

    assert result["ran"] is True  # script 有跑（不是整段被拒）
    assert result[key] != "forged"
    assert result["original_query"] == "2025Q3 稅後淨利是多少"


def test_script_cannot_forge_tenant_id():
    """身分鍵同理：script 寫 tenant_id 被剝除 —— 否則流程中途就能換租戶。"""
    result = run_flow(
        [{"script": "state['tenant_id'] = 'other-tenant'\nstate['ran'] = True"}]
    )

    assert result["tenant_id"] == "t-test"
    assert result["ran"] is True


# 每個身分鍵的（伺服器依 RequestContext 注入的真值, script 想竄改的值）。
IDENTITY_VALUES: dict[str, tuple] = {
    "tenant_id": ("t-test", "other-tenant"),
    "user_id": ("u-authentic", "u-forged"),
    "role": ("USER", "ADMIN"),
}


@pytest.mark.parametrize("key", sorted(IDENTITY_KEYS))
def test_script_cannot_forge_identity_keys(key):
    """身分鍵三個都要擋：換租戶、冒用他人、自我提權（role=ADMIN）是同一類逃逸。

    直接吃生產常數：日後新增身分鍵會自動被涵蓋（缺對照值即在第一行 assert 出局）。
    """
    assert set(IDENTITY_VALUES) == set(IDENTITY_KEYS)
    authentic, forged = IDENTITY_VALUES[key]

    result = run_flow(
        [{"script": f"state[{key!r}] = {forged!r}\nstate['ran'] = True"}],
        {key: authentic},
    )

    assert result["ran"] is True  # script 有跑（不是整段被拒）
    assert result[key] == authentic


def test_script_cannot_replace_runtime_authority_seen_by_downstream_node():
    """Malicious script writes are dropped; later nodes see the snapshot values."""
    authentic = {
        "run_id": "run-authentic",
        "agent_id": "agent-authentic",
        "agent_revision": 7,
        "knowledge_sources": ["source-authentic"],
        "enforce_data_scope": True,
    }
    assignments = "\n".join(
        [
            "state['run_id'] = 'run-forged'",
            "state['agent_id'] = 'agent-forged'",
            "state['agent_revision'] = 999",
            "state['knowledge_sources'] = ['source-forged']",
            "state['enforce_data_scope'] = False",
            "state['ordinary'] = 'kept'",
        ]
    )

    result = run_flow(
        [{"script": assignments}, {"node": "s_authority_observer"}],
        authentic,
    )

    assert result["ordinary"] == "kept"
    assert result["authority_seen"] == authentic
    assert {key: result[key] for key in RUNTIME_AUTHORITY_KEYS} == authentic


@pytest.mark.parametrize("key", ["fatal_error", "errors", "trace"])
def test_script_cannot_forge_engine_keys(key):
    """引擎鍵（trace/errors/fatal_error）由 Node Shell 寫入：script 寫得進去就能偽造稽核與短路。"""
    result = run_flow([{"script": f"state[{key!r}] = 'forged'"}, {"node": "s_next"}])

    assert result.get("fatal_error") in (None, "")  # 沒有被偽造的 fatal
    assert result["next_ran"] is True  # 後續節點照跑（沒被假 fatal 短路）
    assert result["trace"][0].node_name == "script"  # trace 沒被字串蓋掉
    assert all(e["error"] != "forged" for e in result.get("errors", []))


# 每個 Configuration Set 執行參數鍵的（伺服器注入的真值, script 想竄改的值）。
CONFIG_SEED_VALUES: dict[str, tuple] = {"retrieval_top_k": (3, 99)}


@pytest.mark.parametrize("key", sorted(CONFIG_SEED_KEYS))
def test_script_cannot_forge_config_seed_keys(key):
    """FORBIDDEN_WRITE_KEYS 的第四個來源集合：invoke 期由 active Configuration Set
    注入的只讀執行參數。寫得進去 = script 可以自己放大 retrieval_top_k，繞過租戶設定。

    直接吃生產常數：日後新增 seed 鍵會自動被涵蓋（缺 seed 值即在第一行 assert 出局）。
    """
    assert set(CONFIG_SEED_VALUES) == set(CONFIG_SEED_KEYS)
    authentic, forged = CONFIG_SEED_VALUES[key]

    result = run_flow(
        [{"script": f"state[{key!r}] = {forged!r}\nstate['ran'] = True"}],
        {key: authentic},
    )

    assert result["ran"] is True  # script 有跑（不是整段被拒）
    assert result[key] == authentic


def test_script_runtime_exception_becomes_fatal_not_500():
    """script 內的執行期例外（除零）→ 受控的 fatal 短路 + 稽核照樣落地，不是未捕捉 500。"""
    deps = default_engine_deps()
    result = run_flow([{"script": "state['x'] = 1 / 0"}, {"node": "s_next"}], deps=deps)

    entry = _script_entry(result)
    assert entry.status == "error"
    assert entry.error_code == "ZeroDivisionError"
    assert "x" not in result  # 失敗的 script 一個鍵都不落
    assert "next_ran" not in result  # 後續節點被短路
    assert _traced(result)[-1] == "audit_feedback"
    assert len(deps.audit_repo.saved) == 1


def test_script_cannot_see_engine_internal_keys_at_runtime():
    """【AT2-20 的執行期那一半】script 讀不到 __loop_<id>_count：迴圈計數不在它的 state 視野裡。

    存檔期擋的是 `state['__loop_0_count']` 這種字面寫法；這裡擋的是「用 in 探測」——
    script 看到的是 public state（__ 前綴鍵已剝除），探測結果只能是 False。
    """
    result = run_flow(
        [
            {
                "loop": {
                    "max_iterations": 2,
                    "body": [
                        {
                            "script": (
                                "state['leaked'] = '__loop_0_count' in state\n"
                                "state['internal_keys'] = 0\n"
                            )
                        }
                    ],
                }
            }
        ]
    )

    assert result["leaked"] is False
    assert result["internal_keys"] == 0


# ---------------------------------------------------------------------------
# AT-GOV-04 trace 不落 LLM 私有推理，也不落 script 全文
# ---------------------------------------------------------------------------


def test_trace_leaks_neither_llm_reasoning_nor_script_source():
    """【AT-GOV-04】同一條 flow 同時有 LLM 節點與 script 步驟：兩種私有內容都不得外洩。"""
    cot = "PRIVATE_CHAIN_OF_THOUGHT"
    llm = FakeStructuredLLM(
        outputs={
            QueryRewriteOutput: QueryRewriteOutput(
                normalized_query="2025Q3 稅後淨利",
                query_variants=["2025Q3 稅後淨利"],
                rewrite_reason="正規化期間與指標",
            )
        },
        private_thinking=cot,
    )
    deps = default_engine_deps(llm=llm)
    source = f"state['note'] = '{SOURCE_SENTINEL}'"

    result = run_flow(
        [
            {"node": "query_intake"},
            {"node": "query_rewrite"},
            {"script": source},
        ],
        {"query": "2025Q3 稅後淨利是多少"},
        deps=deps,
    )

    trace_json = "".join(e.model_dump_json() for e in result["trace"])
    trail_json = deps.audit_repo.saved[0].model_dump_json()

    assert cot not in trace_json and cot not in trail_json
    assert SOURCE_SENTINEL not in trace_json  # script 全文不落 trace
    assert result["note"] == SOURCE_SENTINEL  # 但 script 確實執行過


# ---------------------------------------------------------------------------
# script 步驟同樣受編譯期治理護欄（繞過 validate API 也擋）
# ---------------------------------------------------------------------------


def test_compiler_rejects_forbidden_script_even_without_api_validation():
    skill = Skill.model_validate(
        {"name": "probe-skill", "flow": [{"script": "import os"}]}
    )

    with pytest.raises(compiler.SkillCompileError) as exc:
        compiler.compile(skill, default_engine_deps())

    assert "forbidden_script" in str(exc.value)


@pytest.mark.parametrize("timeout_ms", [0, 10001])
def test_compiler_rejects_out_of_range_script_timeout(timeout_ms):
    """timeout_ms 上限 10000（規格 §3.2）：邊界外在編譯期就擋。"""
    skill = Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [{"script": "state['x'] = 1", "timeout_ms": timeout_ms}],
        }
    )

    with pytest.raises(compiler.SkillCompileError) as exc:
        compiler.compile(skill, default_engine_deps())

    assert "timeout_ms" in str(exc.value)


@pytest.mark.parametrize("timeout_ms", [100, 10000])
def test_compiler_accepts_in_range_script_timeout(timeout_ms):
    """決策表另一半：邊界內（含上界 10000）照常編譯並執行。

    下界雖然是 1ms，但那個預算連 worker thread 的排程都不夠（見 AT3-12：
    timeout_ms=1 的實際語意就是「必逾時」）—— 所以這裡取務實的 100ms。
    """
    result = run_flow([{"script": "state['x'] = 1", "timeout_ms": timeout_ms}])

    assert result["x"] == 1
