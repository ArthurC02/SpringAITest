namespace Backend.Api.OperationsGovernance;

/// <summary>
/// CSR-EVAL-001 (plans/chat-skill-routing/04-acceptance-test.md §7) becomes the first durable
/// eval suite revision. This is a bootstrap, not the full curated corpus: the acceptance test
/// itself calls for at least 20 de-identified prompts per category, calibrated and owned by an
/// ongoing quality-eval/product process -- that content does not exist anywhere in this repo, and
/// authoring it is out of scope for a backend storage-layer change. The point of seeding here is
/// only to let suite authority start from CSR-EVAL-001's own shape/categories rather than a second,
/// competing corpus; a real curated corpus lands as later revisions of this same suite_id.
/// `mode:"deterministic"` is a placeholder tag -- these natural-language routing prompts are not
/// runnable by Workflow's fixture/replay runner yet (plan §5.2's "live shadow" mode is not part of
/// the frozen case.mode enum), matching the "cases need not be runner-executable yet" instruction.
/// `required_case_ids`/`freshness_seconds` are a reasonable operational default for this bootstrap,
/// not a product-confirmed release policy; both can move in a future revision without touching rev1.
/// </summary>
public static class CsrEval001Suite
{
    public const string SuiteId = "csr-eval-001";

    public const string CasesJson = """
        {
          "policy": {
            "required_case_ids": ["chit-chat-01", "retrieval-01"],
            "freshness_seconds": 2592000
          },
          "cases": [
            {"case_id":"retrieval-01","mode":"deterministic","input":{"message":"請幫我從知識庫查詢上一季的退款政策"},"expected":{"category":"retrieval","acceptable_skills":["kb_query"]}},
            {"case_id":"retrieval-02","mode":"deterministic","input":{"message":"公司的年假規定是什麼？"},"expected":{"category":"retrieval","acceptable_skills":["kb_query"]}},
            {"case_id":"compare-01","mode":"deterministic","input":{"message":"比較 Q1 與 Q2 的營收成長率"},"expected":{"category":"compare","acceptable_skills":["compare_metrics"]}},
            {"case_id":"compare-02","mode":"deterministic","input":{"message":"這兩份合約的付款條件有什麼不同？"},"expected":{"category":"compare","acceptable_skills":["compare_metrics"]}},
            {"case_id":"infer-01","mode":"deterministic","input":{"message":"根據目前的銷售趨勢，下個月大概會成長多少？"},"expected":{"category":"infer","acceptable_skills":["infer_trend"]}},
            {"case_id":"infer-02","mode":"deterministic","input":{"message":"如果原物料成本上漲一成，對毛利率的影響是什麼？"},"expected":{"category":"infer","acceptable_skills":["infer_trend"]}},
            {"case_id":"inspire-01","mode":"deterministic","input":{"message":"給我三個提升客戶回購率的點子"},"expected":{"category":"inspire","acceptable_skills":["inspire_ideas"]}},
            {"case_id":"inspire-02","mode":"deterministic","input":{"message":"這次活動主題還能怎麼發想？"},"expected":{"category":"inspire","acceptable_skills":["inspire_ideas"]}},
            {"case_id":"stats-01","mode":"deterministic","input":{"message":"幫我算一下這批數據的平均值和標準差"},"expected":{"category":"stats","acceptable_skills":["compute_stats"]}},
            {"case_id":"stats-02","mode":"deterministic","input":{"message":"這組樣本的中位數是多少？"},"expected":{"category":"stats","acceptable_skills":["compute_stats"]}},
            {"case_id":"tenant-custom-01","mode":"deterministic","input":{"message":"幫我用內部工具查一下這個客戶的專屬資料"},"expected":{"category":"tenant_custom_skill","acceptable_skills":["tenant_custom_search"]}},
            {"case_id":"tenant-custom-02","mode":"deterministic","input":{"message":"用我們租戶自己的檢索工具找一下相關文件"},"expected":{"category":"tenant_custom_skill","acceptable_skills":["tenant_custom_search"]}},
            {"case_id":"chit-chat-01","mode":"deterministic","input":{"message":"早安，今天過得如何？"},"expected":{"category":"chit_chat","acceptable_skills":[]}},
            {"case_id":"chit-chat-02","mode":"deterministic","input":{"message":"謝謝你的幫忙！"},"expected":{"category":"chit_chat","acceptable_skills":[]}},
            {"case_id":"ambiguous-01","mode":"deterministic","input":{"message":"幫我看一下這個"},"expected":{"category":"ambiguous","acceptable_skills":[]}},
            {"case_id":"ambiguous-02","mode":"deterministic","input":{"message":"這個怎麼樣？"},"expected":{"category":"ambiguous","acceptable_skills":[]}}
          ]
        }
        """;
}
