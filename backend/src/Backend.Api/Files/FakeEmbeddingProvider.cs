using System.Security.Cryptography;
using System.Text;

namespace Backend.Api.Files;

/// <summary>
/// 確定性假嵌入:同一段文字永遠得到相同向量,不需連網、不需金鑰,適合本機開發與測試。
/// 對應 workflow embeddings.py 的 "fake" 分支(該處用 langchain DeterministicFakeEmbedding)。
///
/// ponytail: 這裡用 SHA-256 → 種子 → PRNG,刻意不與 langchain 的 numpy 常態分布做位元對齊
/// (規格只要求「確定性、維度 1536」)。假向量本無語意,舊 fake 資料的 schema 不變仍可讀取,
/// 唯一影響是舊 fake chunk 與新 fake query 不會恰好互相命中——而假向量本就不代表真實相似度。
/// 若日後要與 workflow 舊查詢結果完全一致,再改成移植 numpy MT19937 常態分布。
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dim;

    public FakeEmbeddingProvider(int dim = 1536) => _dim = dim;

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToArray());

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
        => Task.FromResult(Embed(text));

    private float[] Embed(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);
        var v = new float[_dim];
        for (var i = 0; i < _dim; i++)
        {
            v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        return v;
    }
}
