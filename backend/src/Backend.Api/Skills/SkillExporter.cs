using System.IO.Compression;
using System.Text;

namespace Backend.Api.Skills;

/// <summary>
/// 把一個 flow Skill 打包成 Claude Skill 格式的 zip(純字串組裝,不解析/不執行 definition 內容)。
/// zip 內恰一個檔 SKILL.md,自包含(05 §3.1):
///   frontmatter 只有標準欄位 name + description;body 以 fenced ```yaml 區塊嵌入 definition 原文。
/// definition 逐 byte 嵌入開場 ```yaml 行與收場 ``` 行之間 — 不再序列化、不做任何換行正規化,
/// workflow 匯入端萃取「第一個 ```yaml 區塊」即取回 skill.Definition 本身(byte-for-byte 契約)。
/// name 已受標準規則約束(`^[a-z0-9]([a-z0-9-]*[a-z0-9])?$`、1–64、無連續 `--`;純 ASCII),
/// description 以 YAML-safe scalar 寫入 frontmatter(skills-ref validate friendly)。
/// agentic export 不走這裡(controller 直接回存好的 package bytes)。
/// </summary>
public static class SkillExporter
{
    // Encoding.UTF8.GetBytes 不會輸出 BOM(BOM 只由 preamble 產生)。
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static byte[] ToZip(Skill skill)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "SKILL.md", Utf8NoBom.GetBytes(BuildManifest(skill)));
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    // definition 夾在 "```yaml\n" 與 "\n```" 之間 → 萃取「第一個 ```yaml 區塊」即得原文。
    // ponytail: 不逃脫 definition 內部的 ``` — flow YAML 定義不含三反引號;若日後允許,改用長圍欄(````)。
    private static string BuildManifest(Skill skill) =>
        "---\n"
        + $"name: {skill.Name}\n"
        + $"description: {YamlScalar(skill.Description)}\n"
        + "---\n"
        + "\n"
        + "本 Skill 為 node-first 引擎的宣告式流程定義，權威內容即下方 ```yaml 區塊。\n"
        + "\n"
        + "```yaml\n"
        + skill.Definition
        + "\n```\n";

    /// <summary>
    /// 以 YAML-safe scalar 寫入 description(修正舊 POC 未逃脫的假設,03-design §2.4)。
    /// 可安全當 plain scalar 者原樣輸出(維持既有 `description: 中文` 形狀);否則以雙引號包裹並逃脫
    /// 反斜線/雙引號/換行/tab。plain-safe 的判定刻意保守:任何 `:`/`#`、前後空白、開頭指示字元、
    /// 控制字元皆改用引號 — 產出對 YAML parser 一律無歧義。
    /// </summary>
    private static string YamlScalar(string s)
    {
        if (IsPlainSafe(s))
        {
            return s;
        }

        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsPlainSafe(string s)
    {
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
        {
            return false;
        }

        // 開頭指示字元(在 value 位置可能引發歧義)一律改引號。
        if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0]) >= 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            // 保守:任何冒號/井字號或控制字元 → 引號(避免 `k: v`、` #` 這類歧義)。
            if (c is ':' or '#' or '\n' or '\r' or '\t' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
