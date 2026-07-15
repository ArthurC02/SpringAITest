using System.Security.Cryptography;
using System.Text;

namespace Backend.Api.Skills;

/// <summary>
/// skill_revision.definition_sha256 的計算。稽核用 hash 鏈:audit trail 記
/// skill_name + revision + definition_sha256,可回查當次執行用的確切定義原文。
/// 小寫十六進位、UTF-8 編碼 — 與引擎端對同一份原文算出的 hash 必須逐字元相同。
/// </summary>
public static class SkillHash
{
    public static string Sha256(string definition)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
}
