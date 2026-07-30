namespace Backend.Api.Skills;

/// <summary>Agent Skills 標準名稱規則的 Backend 單一來源。</summary>
internal static class SkillNameRules
{
    /// <summary>
    /// 保留字:(a) code 註冊工作流的名稱 — skill 不得同名(否則執行時路由鍵撞名);
    /// (b) platform 的字面路由段 catalog/validate/nodes — 字面段永遠勝過 {name},
    /// 這種名字的 skill 建得起來卻永遠點不進去(GET /api/skills/catalog 回的是引擎目錄)。
    /// 新增 workflow 內建 skill 時必須同步此清單。
    /// ponytail: 硬寫保留字，等 workflow 名單真的會變再改成打 GET /workflows。
    /// </summary>
    internal static readonly HashSet<string> ReservedBusinessWorkflowNames = new(StringComparer.Ordinal)
    {
        "summarize", "triage", "rag-qa", "analyze-report", "kb-query",
        "catalog", "validate", "nodes",
        "template-retrieval", "template-compare", "template-stats", "template-infer", "template-inspire",
    };

    public static bool IsStandard(string name)
    {
        if (name.Length is < 1 or > 64
            || name.StartsWith('-')
            || name.EndsWith('-')
            || name.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        return name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}
