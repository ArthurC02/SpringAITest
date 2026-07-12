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

    [Fact]
    public void SplitText_OverlapGreaterThanMax_StillTerminates()
    {
        // step 會被夾到至少 1,不會無窮迴圈。
        var chunks = Chunking.SplitText(new string('x', 15), maxChars: 5, overlap: 10);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.Length <= 5));
    }
}
