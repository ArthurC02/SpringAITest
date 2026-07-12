using System.Text.RegularExpressions;

namespace Backend.Api.Files;

/// <summary>
/// 純函式:把長文字切成適合嵌入/檢索的片段。逐行移植自 workflow/app/chunking.py 的 split_text,
/// 行為必須一致(舊資料切法相同)。
///
/// 策略分兩層:先以空白行為界切成段落(保留語意邊界);段落超過 maxChars 再以滑動視窗
/// (重疊 overlap 字)切塊,避免關鍵語句剛好被切在區塊邊界而遺失上下文。
/// </summary>
public static class Chunking
{
    private static readonly Regex ParagraphSplit = new(@"\n\s*\n", RegexOptions.Compiled);

    public static List<string> SplitText(string? text, int maxChars = 800, int overlap = 100)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var paragraphs = ParagraphSplit.Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paragraphs.Count == 0)
        {
            return new List<string>();
        }

        // overlap 若 >= maxChars,滑動視窗會原地打轉;至少前進 1 字元避免無窮迴圈。
        var step = Math.Max(maxChars - overlap, 1);

        var chunks = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length <= maxChars)
            {
                chunks.Add(paragraph);
                continue;
            }

            var start = 0;
            while (start < paragraph.Length)
            {
                var end = start + maxChars;
                var take = Math.Min(maxChars, paragraph.Length - start);
                var piece = paragraph.Substring(start, take).Trim();
                if (piece.Length > 0)
                {
                    chunks.Add(piece);
                }

                if (end >= paragraph.Length)
                {
                    break;
                }

                start += step;
            }
        }

        return chunks;
    }
}
