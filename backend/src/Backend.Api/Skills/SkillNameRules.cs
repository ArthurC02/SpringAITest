namespace Backend.Api.Skills;

/// <summary>Agent Skills 標準名稱規則的 Backend 單一來源。</summary>
internal static class SkillNameRules
{
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
