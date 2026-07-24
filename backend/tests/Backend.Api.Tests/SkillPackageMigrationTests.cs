using System.IO.Compression;
using System.Text;
using Backend.Api.Data;

namespace Backend.Api.Tests;

/// <summary>
/// 啟動期遷移挑選 SKILL.md 的規則(無 DB 單元測試)。zip 內可能存在多個 `*/SKILL.md`:
/// workflow 的匯入只要求「所有 entry 共用單一頂層資料夾」,前綴底下仍可有 `examples/SKILL.md`。
/// 遷移端必須對齊該語義(只認深度 1)且優先取 `{name}/SKILL.md`,不能靠 central directory 順序 ——
/// 取錯檔會讓 Rewrite 拋例外,連帶讓 backend 永久起不來(或靜默改寫資源檔)。
/// </summary>
public sealed class SkillPackageMigrationTests
{
    private const string Name = "sales-helper";

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, string> ReadZip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            });
    }

    private static string SkillMd(string name)
        => $"---\nname: {name}\ndescription: d\n---\n\n```yaml\nname: {name}\nflow:\n  - node: query_intake\n```\n";

    // 誘餌(巢狀 / 同層另一資料夾)刻意排在真檔前面,模擬 central directory 順序不利的 zip。
    [Theory]
    [InlineData($"{Name}/examples/SKILL.md")]
    [InlineData("zzz-other/SKILL.md")]
    public void Rewrite_DecoySkillMdFirst_StillPicksTopLevelTargetSkillMd(string decoy)
    {
        const string decoyText = "誘餌:沒有 frontmatter 的說明檔\n";
        var package = Zip((decoy, decoyText), ($"{Name}/SKILL.md", SkillMd("sales_helper")));

        Assert.True(SkillPackageMigration.PackageNeedsRewrite(package, Name, "flow"));
        var migrated = SkillPackageMigration.Rewrite(package, Name, "flow");

        var entries = ReadZip(migrated.Bytes);
        Assert.Contains($"name: {Name}\n", entries[$"{Name}/SKILL.md"]);
        Assert.DoesNotContain("sales_helper", entries[$"{Name}/SKILL.md"]);
        Assert.Equal(decoyText, entries[decoy]);
    }

    // 已標準的 package + 巢狀誘餌 → 不得誤判為需要改寫(否則每次啟動都重壓縮、破壞 byte 契約)。
    [Fact]
    public void PackageNeedsRewrite_StandardPackageWithNestedDecoy_IsFalse()
    {
        var package = Zip(
            ($"{Name}/examples/SKILL.md", "誘餌\n"),
            ($"{Name}/SKILL.md", SkillMd(Name)));

        Assert.False(SkillPackageMigration.PackageNeedsRewrite(package, Name, "flow"));
    }
}
