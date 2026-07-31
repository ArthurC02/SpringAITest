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

    // 舊版 agentic frontmatter:kind / required_role / timeout_seconds / input_schema / uses_tools
    // 都還散在頂層,遷移要把它們投影進 metadata + allowed-tools。
    private static string LegacyAgenticSkillMd(string name)
        => $"---\nname: {name}\ndescription: legacy agentic helper\nkind: agentic\n"
            + "required_role: ADMIN\ntimeout_seconds: 30\n"
            + "uses_tools:\n  - search\n  - fetch\ninput_schema:\n  type: object\n---\n\n# 使用說明\n";

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

    // 兩個合法位置同時存在時的第一優先序:root `SKILL.md` 勝過 `{name}/SKILL.md`
    // (SelectSkillMdName 的順序)。順序反過來的話,遷移會改到資料夾下那份、留下沒改到的 root
    // frontmatter —— 匯入端讀 root,等於遷移靜默失效。
    [Fact]
    public void Rewrite_RootAndFolderedSkillMd_PrefersRootSkillMd()
    {
        const string folderedText = "---\nname: foldered_name\ndescription: d\n---\n資料夾下那份\n";
        var package = Zip(($"{Name}/SKILL.md", folderedText), ("SKILL.md", SkillMd("sales_helper")));

        Assert.True(SkillPackageMigration.PackageNeedsRewrite(package, Name, "flow"));
        var migrated = SkillPackageMigration.Rewrite(package, Name, "flow");

        var entries = ReadZip(migrated.Bytes);
        Assert.Contains($"name: {Name}\n", entries["SKILL.md"]);
        Assert.DoesNotContain("sales_helper", entries["SKILL.md"]);
        Assert.Equal(folderedText, entries[$"{Name}/SKILL.md"]); // 另一份原封不動
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

    // kind="agentic" 分支(另一半決策表):root + `{name}/` 兩份 SKILL.md 同時存在時,
    // root 那份被投影成標準 frontmatter、資料夾下那份原封不動,且投影結果必須收斂 ——
    // 再判一次不得又說要改寫,否則每次啟動都會重壓縮並換掉 package_sha256。
    [Fact]
    public void Rewrite_AgenticRootAndFolderedSkillMd_ProjectsRootToStandardFrontmatter()
    {
        const string folderedText = "---\nname: foldered_name\ndescription: d\n---\n資料夾下那份\n";
        var package = Zip(
            ($"{Name}/SKILL.md", folderedText),
            ("SKILL.md", LegacyAgenticSkillMd("sales_helper")));

        Assert.True(SkillPackageMigration.PackageNeedsRewrite(package, Name, "agentic"));
        var migrated = SkillPackageMigration.Rewrite(package, Name, "agentic");

        var entries = ReadZip(migrated.Bytes);
        var skillMd = entries["SKILL.md"];
        Assert.Contains($"name: {Name}\n", skillMd);
        Assert.DoesNotContain("sales_helper", skillMd);
        Assert.DoesNotContain("uses_tools", skillMd);            // 頂層 legacy 欄位收乾淨
        Assert.Contains("allowed-tools: search fetch", skillMd); // uses_tools 序列 → 空白分隔字串
        Assert.Contains("kind: agentic", skillMd);
        Assert.Contains("required_role: ADMIN", skillMd);
        Assert.Contains("timeout_seconds:", skillMd);
        Assert.Contains("{\"type\":\"object\"}", skillMd);       // input_schema 轉成 JSON 字串
        Assert.NotNull(migrated.CanonicalDefinition);
        // 回傳的 canonical definition 就是新 frontmatter,body 一字不動接在後面。
        Assert.Equal($"---\n{migrated.CanonicalDefinition}---\n\n# 使用說明\n", skillMd);
        Assert.Equal(folderedText, entries[$"{Name}/SKILL.md"]);
        Assert.False(SkillPackageMigration.PackageNeedsRewrite(migrated.Bytes, Name, "agentic"));
    }

    // 讀不出 / 找不到 / 認不出 SKILL.md 一律回 true —— fail-safe:寧可重寫,不可靜默跳過遷移。
    [Fact]
    public void PackageNeedsRewrite_UnreadableOrFrontmatterlessPackage_IsTrue()
    {
        Assert.True(SkillPackageMigration.PackageNeedsRewrite(
            new byte[] { 1, 2, 3 }, Name, "flow"));                        // 根本不是 zip
        Assert.True(SkillPackageMigration.PackageNeedsRewrite(
            Zip(("other/notes.txt", "x")), Name, "flow"));                 // 零個 SKILL.md
        Assert.True(SkillPackageMigration.PackageNeedsRewrite(
            Zip(("SKILL.md", "沒有 frontmatter 的說明檔\n")), Name, "flow")); // frontmatter regex 不過
    }

    // SelectSkillMdName 第三順位:既無 root SKILL.md 也無 `{name}/SKILL.md` 時,唯一的深度 1
    // `*/SKILL.md` 仍要被選中並就地改寫 —— entry 名稱不隨 Skill 改名而搬移。
    [Fact]
    public void Rewrite_OnlyLegacyFolderedSkillMd_RewritesThatEntryInPlace()
    {
        var package = Zip(("legacy-name/SKILL.md", SkillMd("sales_helper")));

        Assert.True(SkillPackageMigration.PackageNeedsRewrite(package, Name, "flow"));
        var migrated = SkillPackageMigration.Rewrite(package, Name, "flow");

        var entries = ReadZip(migrated.Bytes);
        Assert.Equal(1, entries.Count);
        var skillMd = entries["legacy-name/SKILL.md"]; // 沒有這個 key 就是被搬走了
        Assert.Contains($"name: {Name}\n", skillMd);
        Assert.DoesNotContain("sales_helper", skillMd);
    }

    // 完全沒有合法候選 → 直接拋,不得靜默產出殘缺 package。
    [Fact]
    public void Rewrite_NoSkillMdCandidate_Throws()
    {
        var package = Zip(("random.txt", "x"));

        var error = Assert.Throws<InvalidDataException>(
            () => SkillPackageMigration.Rewrite(package, Name, "flow"));
        Assert.Equal("既有 Skill package 缺少 root SKILL.md", error.Message);
    }

    // 已標準的 package 走 Rewrite 必須原封退回「同一個」byte[](不重壓縮),
    // 否則每次啟動 package_sha256 都會變。
    [Fact]
    public void Rewrite_AlreadyStandardPackage_ReturnsSameByteArray()
    {
        var package = Zip(("SKILL.md", SkillMd(Name)), ("assets/logo.txt", "logo"));

        var migrated = SkillPackageMigration.Rewrite(package, Name, "flow");

        Assert.Same(package, migrated.Bytes);
        Assert.Null(migrated.CanonicalDefinition);
    }

    // 真的需要重壓縮時,其他 entry 不只 bytes 要保留,權限位元與時間戳也要跟著過去。
    [Fact]
    public void Rewrite_RezippedPackage_PreservesOtherEntryAttributesAndTimestamp()
    {
        byte[] package;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var asset = archive.CreateEntry("assets/logo.txt");
                asset.LastWriteTime = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
                asset.ExternalAttributes = 0x20;
                using (var assetStream = asset.Open())
                {
                    assetStream.Write(Encoding.UTF8.GetBytes("logo"));
                }

                var skillMd = archive.CreateEntry("SKILL.md");
                using (var skillMdStream = skillMd.Open())
                {
                    skillMdStream.Write(Encoding.UTF8.GetBytes(SkillMd("sales_helper")));
                }
            }

            package = buffer.ToArray();
        }

        var migrated = SkillPackageMigration.Rewrite(package, Name, "flow");

        Assert.NotSame(package, migrated.Bytes);
        using var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var result = new ZipArchive(new MemoryStream(migrated.Bytes), ZipArchiveMode.Read);
        var before = source.GetEntry("assets/logo.txt")!;
        var after = result.GetEntry("assets/logo.txt")!;
        Assert.Equal(before.LastWriteTime, after.LastWriteTime);
        Assert.Equal(before.ExternalAttributes, after.ExternalAttributes);
    }
}
