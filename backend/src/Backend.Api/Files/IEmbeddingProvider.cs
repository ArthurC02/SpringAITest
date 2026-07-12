namespace Backend.Api.Files;

/// <summary>嵌入模型抽象:文件批次嵌入與單一查詢嵌入。實作由 EMBEDDINGS_PROVIDER 決定。</summary>
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct);

    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct);
}
