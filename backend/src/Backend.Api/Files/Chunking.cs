using System.Text.RegularExpressions;

namespace Backend.Api.Files;

/// <summary>
/// 純函式:把長文字切成適合嵌入/檢索的片段。原為 workflow/app/chunking.py 的 split_text 移植
/// (該檔已隨 workflow 改為 state-only 而移除,backend 是現在的唯一權威)。
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

            // 滑動視窗以 **code point** 為單位,不是 UTF-16 char:非 BMP 字元(emoji、CJK 擴展區)
            // 是 surrogate pair,切在中間會產生孤兒 surrogate,轉 UTF-8 時靜默換成替代字元
            // (不拋例外的資料損毀)。ASCII/BMP 的切法與先前逐字相同(一 char 即一 code point)。
            // ponytail: 邊界單位是 code point 而非完整 grapheme cluster —— 組合字/ZWJ 序列仍可能
            // 被拆成兩塊,但兩半都是合法 UTF-8(非損毀);要更嚴格再換 StringInfo 的 text element。
            var offsets = CodePointOffsets(paragraph);
            var count = offsets.Length - 1;

            var start = 0;
            while (start < count)
            {
                var take = Math.Min(maxChars, count - start);
                var piece = paragraph[offsets[start]..offsets[start + take]].Trim();
                if (piece.Length > 0)
                {
                    chunks.Add(piece);
                }

                if (start + maxChars >= count)
                {
                    break;
                }

                start += step;
            }
        }

        return chunks;
    }

    /// <summary>每個 code point 的起始索引,最後補一個 s.Length 哨兵(切點只取自此陣列)。</summary>
    private static int[] CodePointOffsets(string s)
    {
        var offsets = new List<int>(s.Length + 1);
        for (var i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
        {
            offsets.Add(i);
        }

        offsets.Add(s.Length);
        return offsets.ToArray();
    }
}
