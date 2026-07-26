using System.Text;
using Backend.Api.Files;

namespace Backend.Api.Tests;

/// <summary>切塊行為必須與 workflow/app/chunking.py 的 split_text 一致。</summary>
public sealed class ChunkingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \t ")]
    public void SplitText_BlankInput_ReturnsEmpty(string? text)
    {
        Assert.Empty(Chunking.SplitText(text));
    }

    [Fact]
    public void SplitText_ShortParagraph_SingleTrimmedChunk()
    {
        var chunks = Chunking.SplitText("  你好世界  ");

        Assert.Single(chunks);
        Assert.Equal("你好世界", chunks[0]);
    }

    [Fact]
    public void SplitText_SplitsOnBlankLines_IntoParagraphs()
    {
        var chunks = Chunking.SplitText("第一段。\n\n第二段。\n \n第三段。");

        Assert.Equal(new[] { "第一段。", "第二段。", "第三段。" }, chunks);
    }

    [Fact]
    public void SplitText_LongParagraph_SlidesWithOverlap()
    {
        // maxChars=10, overlap=4 → step=6。長度 20 的段落:start=0,6,12;第三塊 end=22>=20 即收尾。
        var text = new string('a', 20);

        var chunks = Chunking.SplitText(text, maxChars: 10, overlap: 4);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(10, chunks[0].Length); // 0..10
        Assert.Equal(10, chunks[1].Length); // 6..16
        Assert.Equal(8, chunks[2].Length);  // 12..20
    }

    // 段落長度剛好等於 maxChars → 不切(1 塊);多 1 字 → 進滑動視窗(2 塊)。off-by-one 邊界。
    [Theory]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    public void SplitText_LengthAtMaxCharsBoundary(int len, int expectedChunks)
    {
        var chunks = Chunking.SplitText(new string('a', len), maxChars: 10, overlap: 4);

        Assert.Equal(expectedChunks, chunks.Count);
    }

    // 非 BMP 字元(emoji、CJK 擴展區罕用字)在 UTF-16 是 surrogate pair。切點若落在 pair 中間,
    // 會切出孤兒 surrogate:轉 UTF-8 時靜默換成替代字元(不拋例外的資料損毀)。
    // 5 個 code point = 10 個 UTF-16 char,maxChars=3 讓每個視窗邊界都落在 pair 中間。
    [Theory]
    [InlineData("😀")]  // U+1F600
    [InlineData("𠀀")]  // U+20000(CJK 擴展 B)
    public void SplitText_NonBmpAtMaxCharsBoundary_KeepsCodePointsIntact(string codePoint)
    {
        var text = string.Concat(Enumerable.Repeat(codePoint, 5));

        var chunks = Chunking.SplitText(text, maxChars: 3, overlap: 0);

        // 孤兒 surrogate 無法編成合法 UTF-8,會在往返時變成 U+FFFD —— 這就是損毀本身。
        Assert.All(chunks, c => Assert.Equal(c, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(c))));
        // 且切法不得靜默丟字:overlap=0 時串回來必須逐字等於原文。
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void SplitText_OverlapGreaterThanMax_StillTerminates()
    {
        // step 會被夾到至少 1,不會無窮迴圈。
        var chunks = Chunking.SplitText(new string('x', 15), maxChars: 5, overlap: 10);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.Length <= 5));
    }
}
