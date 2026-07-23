namespace ExpertiseApi.Services;

/// <summary>
/// Single source of truth for the identity of the embedding model the API is
/// built against (ADR-017). Every consumer that stamps, compares, or gates on
/// the model name, vector dimensionality, or token ceiling (reembed metadata
/// upsert, restore compatibility gate, ONNX generator wiring, eval harness
/// window math) reads these constants — a model swap changes them in exactly
/// one place (#455, #437).
/// </summary>
internal static class EmbeddingModelInfo
{
    public const string Name = "jina-embeddings-v2-small-en";

    /// <summary>Must match the pgvector column type on ExpertiseEntries.Embedding (vector(512)).</summary>
    public const int Dimensions = 512;

    /// <summary>
    /// Token ceiling passed to the ONNX embedding generator. 6144 is the
    /// ground-truth plateau for this corpus (ADR-017): coverage at 6144 equals
    /// 8192 (99.71%, corpus p99 = 4,347 tokens), at 65% less peak RSS, and
    /// 8192 measurably degraded one in-window needle via mean-pooling
    /// dilution. Input beyond the ceiling is silently truncated by the
    /// connector (#429); the MaxBodyLength/MaxTitleLength write guards are
    /// derived from this value.
    /// </summary>
    public const int MaximumTokens = 6144;
}
