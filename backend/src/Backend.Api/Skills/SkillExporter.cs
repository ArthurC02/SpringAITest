using System.IO.Compression;
using System.Text;

namespace Backend.Api.Skills;

/// <summary>
/// 把一個 Skill 打包成 Claude Skill 格式的 zip(純字串組裝,不解析/不執行 definition 內容)。
/// zip 內恰兩個檔:
///   SKILL.md   — 組出來的 manifest(frontmatter 只有 name + description,body 為固定結構);
///   skill.yaml — = skill.Definition 原文,逐 byte 相同(UTF-8 無 BOM,一個字元都不改)。
/// name 已受 `^[a-z][a-z0-9_]{2,63}$` 約束(純 ASCII),description 照原樣放入 frontmatter(POC,不做 YAML 逃脫)。
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
            // definition 已是權威原文 — 原封寫入,不做任何換行正規化。
            WriteEntry(archive, "skill.yaml", Utf8NoBom.GetBytes(skill.Definition));
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static string BuildManifest(Skill skill) =>
        "---\n"
        + $"name: {skill.Name}\n"
        + $"description: {skill.Description}\n"
        + "---\n"
        + "\n"
        + "## 定義\n"
        + "\n"
        + "本 Skill 為 node-first 引擎的宣告式流程定義，權威內容位於 `skill.yaml`。\n"
        + "\n"
        + "## 執行\n"
        + "\n"
        + $"以引擎 `POST /skills/{skill.Name}/invoke` 執行，輸入依定義中的 `input_schema`，輸出見 `output_schema`。\n";
}
