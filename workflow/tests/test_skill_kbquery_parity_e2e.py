"""kb_query Skill 的 golden e2e（AT2-21 ~ AT2-27，原 parity e2e 改造）。

手寫圖（原 app.kbquery.graph）已隨 Node-First 遷移退役，不再有第二張圖可互比；七個案例
改為對編譯後的 skills/kb_query.yaml 輸出做**固定期望值**比對，取代原本的「兩圖逐鍵互比」。
業務層斷言（answer_mode／final_answer／citations…）逐字沿用已刪除的 test_kbquery_e2e.py，
行為覆蓋不因刪圖而消失。

trace 投影另外對照 _GOLDEN_TRACE 這份釘死的期望值（改造前對現行輸出跑一次、原樣入檔）：
這是 Harness `writes` 剝除行為（output_summary）與 component_version 判定的唯一觀測點，
原 parity 測試逐欄位比對兩張圖 trace 的價值在此保留，只是比對對象從「另一張圖」換成
「釘死的期望值」。audit_trail.node_trace 與 trace 的結構關係（audit_feedback 落地時
state.trace 尚未含自己這筆）另以結構性斷言驗證，不需要為 audit_trail 整份再存一份 golden。
"""

import asyncio

from app import skills
from app.engine import compiler
from app.nodes.kbquery.models import AnswerMode, IssueLabel, VerificationResult
from tests.kbquery_fakes import (
    TABLE_2025,
    TEXT_2025Q2,
    TEXT_2025Q3,
    TEXT_WRONG_PERIOD,
    FakeSearch,
    make_deps,
)

QUERY_2025Q3 = "2025Q3 稅後淨利是多少？"

# TraceEntry 內的牆鐘欄位；其餘（node_name/status/input_summary/output_summary/
# error_code/component_version）皆為確定性，一律比對
TRACE_NONDETERMINISTIC = {"start_time", "end_time", "latency_ms"}

# 七案例的 trace 投影釘死期望值（改造前對現行輸出跑一次、原樣入檔，見模組 docstring）。
_GOLDEN_TRACE = {'single_value_lookup': [{'node_name': 'query_intake',
                          'status': 'ok',
                          'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                          'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'query_rewrite',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                          'output_summary': 'normalized_query,query_variants,rewrite_reason',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'intent_classification',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                          'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'context_resolver',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                          'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'retrieval_planner',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                          'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'source_retrieval_rerank',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                          'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'data_locator',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                          'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'evidence_verification',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                          'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'answer_composer',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                          'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'audit_feedback',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                          'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                          'error_code': '',
                          'component_version': ''}],
 'table_cell_lookup': [{'node_name': 'query_intake',
                        'status': 'ok',
                        'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                        'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'query_rewrite',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                        'output_summary': 'normalized_query,query_variants,rewrite_reason',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'intent_classification',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                        'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'context_resolver',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                        'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'retrieval_planner',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                        'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'source_retrieval_rerank',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                        'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'data_locator',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                        'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'evidence_verification',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                        'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'answer_composer',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                        'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                        'error_code': '',
                        'component_version': ''},
                       {'node_name': 'audit_feedback',
                        'status': 'ok',
                        'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                        'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                        'error_code': '',
                        'component_version': ''}],
 'cross_document_comparison': [{'node_name': 'query_intake',
                                'status': 'ok',
                                'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                                'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'query_rewrite',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                                'output_summary': 'normalized_query,query_variants,rewrite_reason',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'intent_classification',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                                'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'context_resolver',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                                'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'retrieval_planner',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                                'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'source_retrieval_rerank',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                                'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'data_locator',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                                'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'evidence_verification',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                                'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'answer_composer',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                                'error_code': '',
                                'component_version': ''},
                               {'node_name': 'audit_feedback',
                                'status': 'ok',
                                'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                                'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                                'error_code': '',
                                'component_version': ''}],
 'retry_then_success': [{'node_name': 'query_intake',
                         'status': 'ok',
                         'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                         'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'query_rewrite',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                         'output_summary': 'normalized_query,query_variants,rewrite_reason',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'intent_classification',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                         'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'context_resolver',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                         'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'retrieval_planner',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                         'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'source_retrieval_rerank',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                         'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'data_locator',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                         'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'evidence_verification',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                         'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'retrieval_planner',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                         'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'source_retrieval_rerank',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                         'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'data_locator',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                         'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'evidence_verification',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                         'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'answer_composer',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                         'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                         'error_code': '',
                         'component_version': ''},
                        {'node_name': 'audit_feedback',
                         'status': 'ok',
                         'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                         'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                         'error_code': '',
                         'component_version': ''}],
 'retry_exhausted_safe_abstain': [{'node_name': 'query_intake',
                                   'status': 'ok',
                                   'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                                   'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'query_rewrite',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                                   'output_summary': 'normalized_query,query_variants,rewrite_reason',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'intent_classification',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                                   'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'context_resolver',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                                   'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'retrieval_planner',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                                   'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'source_retrieval_rerank',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                                   'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'data_locator',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                                   'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'evidence_verification',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                                   'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'retrieval_planner',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                   'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'source_retrieval_rerank',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                   'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'data_locator',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                   'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'evidence_verification',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                   'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'answer_composer',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                   'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                                   'error_code': '',
                                   'component_version': ''},
                                  {'node_name': 'audit_feedback',
                                   'status': 'ok',
                                   'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                                   'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                                   'error_code': '',
                                   'component_version': ''}],
 'retry_cap_prevents_infinite_loop': [{'node_name': 'query_intake',
                                       'status': 'ok',
                                       'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                                       'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'query_rewrite',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                                       'output_summary': 'normalized_query,query_variants,rewrite_reason',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'intent_classification',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                                       'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'context_resolver',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                                       'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'retrieval_planner',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                                       'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'source_retrieval_rerank',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                                       'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'data_locator',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                                       'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'evidence_verification',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                                       'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'retrieval_planner',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'source_retrieval_rerank',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'data_locator',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'evidence_verification',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'retrieval_planner',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'source_retrieval_rerank',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'data_locator',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'evidence_verification',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'answer_composer',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                                       'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                                       'error_code': '',
                                       'component_version': ''},
                                      {'node_name': 'audit_feedback',
                                       'status': 'ok',
                                       'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                                       'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                                       'error_code': '',
                                       'component_version': ''}],
 'blank_query_fatal_short_circuit': [{'node_name': 'query_intake',
                                      'status': 'error',
                                      'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                                      'output_summary': '',
                                      'error_code': 'ValueError',
                                      'component_version': ''},
                                     {'node_name': 'query_rewrite',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'intent_classification',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'context_resolver',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'retrieval_planner',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'source_retrieval_rerank',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'data_locator',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'evidence_verification',
                                      'status': 'skipped',
                                      'input_summary': '',
                                      'output_summary': '',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'answer_composer',
                                      'status': 'ok',
                                      'input_summary': 'errors,fatal_error,query,retrieval_plans,tenant_id,trace',
                                      'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                                      'error_code': '',
                                      'component_version': ''},
                                     {'node_name': 'audit_feedback',
                                      'status': 'ok',
                                      'input_summary': 'answer_format_policy,answer_mode,assumption_note,errors,fatal_error,final_answer,query,retrieval_plans,source_citations,tenant_id,trace',
                                      'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                                      'error_code': '',
                                      'component_version': ''}]}


def _trace_projection(entries) -> list[dict]:
    """TraceEntry 去掉牆鐘欄位後的投影（其餘欄位全比）。"""
    return [e.model_dump(exclude=TRACE_NONDETERMINISTIC) for e in entries]


def run_skill_graph(deps, query: str, **extra_state) -> dict:
    """用 skills/kb_query.yaml 的編譯圖跑一次。"""
    graph = compiler.compile(skills.get("kb-query").skill, deps)
    output = asyncio.run(
        graph.ainvoke({"query": query, "tenant_id": "t-test", **extra_state})
    )
    return compiler.public_output(output)


def _assert_audit_landed(deps, result):
    """通用斷言：不論成敗，稽核必須恰好落地一筆，trace 必含首尾節點。"""
    assert len(deps.audit_repo.saved) == 1
    node_names = [t.node_name for t in result["trace"]]
    assert "query_intake" in node_names
    assert "audit_feedback" in node_names


def _assert_golden_trace(case: str, result: dict) -> None:
    """trace 投影對照釘死的期望值 + audit_trail.node_trace 與 trace 的結構性關係。

    node_trace 落地時（audit_feedback 節點內）state["trace"] 尚未含 audit_feedback
    自己這筆（trace 用 operator.add，節點回傳後才追加），因此 node_trace 少最後一筆。
    """
    trace_projection = _trace_projection(result["trace"])
    assert trace_projection == _GOLDEN_TRACE[case]
    assert result["trace"][-1].node_name == "audit_feedback"
    assert (
        _trace_projection(result["audit_trail"].node_trace) == trace_projection[:-1]
    )


def test_skill_e2e_single_value_lookup_success():
    """【AT2-21】單一數值查詢成功：ANSWER + 正確數值 + citation，無議題與回歸測項。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert "1,234" in result["final_answer"]
    assert result["source_citations"]
    assert result["verification_result"] == VerificationResult.PASS
    assert result["issue_label"] is None
    assert result["regression_test_item"] is None
    _assert_audit_landed(deps, result)
    _assert_golden_trace("single_value_lookup", result)


def test_skill_e2e_table_cell_lookup_success():
    """【AT2-22】表格儲存格查詢成功：citation 精確到 row × column。"""
    query = "損益表中 2025Q3 稅後淨利是多少？"
    deps = make_deps({"table": FakeSearch(lambda q, f: [TABLE_2025])})
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    citation = result["source_citations"][0]
    assert citation.row == "稅後淨利"
    assert citation.column == "2025Q3"
    assert "1234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_golden_trace("table_cell_lookup", result)


def test_skill_e2e_cross_document_comparison_success():
    """【AT2-23】跨文件比較成功：兩期間各選一筆證據，兩個數值都進最終答案。"""
    query = "比較 2025Q2 與 2025Q3 的稅後淨利"
    deps = make_deps(
        {
            "vector": FakeSearch(lambda q, f: [TEXT_2025Q3, TEXT_2025Q2]),
            "metadata": FakeSearch(lambda q, f: []),
        }
    )
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert len(result["selected_evidence"]) == 2
    assert {e.period for e in result["selected_evidence"]} == {"2025Q2", "2025Q3"}
    assert len(result["source_citations"]) == 2
    assert "1,100" in result["final_answer"]
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_golden_trace("cross_document_comparison", result)


def test_skill_e2e_retry_then_success():
    """【AT2-24】第一次驗證失敗（期間錯誤）、第二次依 PERIOD_MISMATCH 加 metadata 檢索成功。"""
    deps = make_deps(
        {
            "vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD]),
            "metadata": FakeSearch(lambda q, f: [TEXT_2025Q3]),
        }
    )
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert result["retrieval_attempt"] == 2
    assert len(result["retrieval_plans"]) == 2
    assert "PERIOD_MISMATCH" in result["retrieval_plans"][1].adjustment_reason
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_golden_trace("retry_then_success", result)


def test_skill_e2e_retry_exhausted_safe_abstain():
    """【AT2-25】重試耗盡：安全 ABSTAIN + 議題標籤 + 回歸測項 + 改進清單，稽核照樣落地。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])})
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["retrieval_attempt"] == 2  # == max_retrieval_attempts
    assert result["final_answer"].startswith("【無法提供答案】")
    assert "PERIOD_MISMATCH" in result["final_answer"]
    assert result["issue_label"] == IssueLabel.RETRIEVAL_MISS
    assert result["regression_test_item"] is not None
    assert result["regression_test_item"].question == QUERY_2025Q3
    assert result["improvement_backlog"]
    _assert_audit_landed(deps, result)
    _assert_golden_trace("retry_exhausted_safe_abstain", result)


def test_skill_e2e_retry_cap_prevents_infinite_loop():
    """【AT2-26】RETRY 次數上限防護：max_attempts=3 時恰好在第 3 次停止。

    業務上限（max_retrieval_attempts，由 deps 注入、寫進 state）先於引擎上限
    （YAML 的 max_iterations: 10）收斂。
    """
    deps = make_deps(
        {"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])}, max_attempts=3
    )
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["retrieval_attempt"] == 3  # 到上限即停，不無限循環
    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert len(result["retrieval_plans"]) == 3
    _assert_audit_landed(deps, result)
    _assert_golden_trace("retry_cap_prevents_infinite_loop", result)


def test_skill_e2e_blank_query_fatal_short_circuit():
    """【AT2-27】空白 query：intake fatal 短路，仍走安全 ABSTAIN + 稽核落地，errors 非空。

    fatal 後 loop body 的節點只會產生 skipped entry，不得真的執行。
    """
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_skill_graph(deps, "   ")

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["errors"]
    assert result["fatal_error"].startswith("query_intake")
    _assert_audit_landed(deps, result)
    _assert_golden_trace("blank_query_fatal_short_circuit", result)

    skipped = {t.node_name for t in result["trace"] if t.status == "skipped"}
    assert {
        "retrieval_planner",
        "source_retrieval_rerank",
        "data_locator",
        "evidence_verification",
    } <= skipped
