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

    /// <summary>agentic package 的 SHA-256(對「實際儲存的原始 zip bytes」計算 — 自洽,不依賴引擎 manifest 語意)。</summary>
    public static string Sha256(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Compare an expected lowercase/uppercase hexadecimal SHA-256 value without
    /// data-dependent short-circuiting. Malformed or absent hashes fail closed.
    /// </summary>
    public static bool MatchesSha256(byte[] bytes, string? expected)
    {
        if (expected is null || expected.Length != 64)
        {
            return false;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualBytes = SHA256.HashData(bytes);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
