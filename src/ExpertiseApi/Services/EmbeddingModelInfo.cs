namespace ExpertiseApi.Services;

/// <summary>
/// Single source of truth for the identity of the embedding model the API is
/// built against. Every consumer that stamps, compares, or gates on the model
/// name or vector dimensionality (reembed metadata upsert, restore
/// compatibility gate) reads these constants — a model swap changes them in
/// exactly one place (#455, #437).
/// </summary>
internal static class EmbeddingModelInfo
{
    public const string Name = "bge-micro-v2";

    /// <summary>Must match the pgvector column type on ExpertiseEntries.Embedding (vector(384)).</summary>
    public const int Dimensions = 384;

    /// <summary>
    /// Token ceiling passed to the ONNX embedding generator. 512 is
    /// bge-micro-v2's architectural window (and the connector's implicit
    /// default — made explicit here so the ceiling is a visible, single-point
    /// decision). Input beyond the ceiling is silently truncated by the
    /// connector (#429); the MaxBodyLength/MaxTitleLength write guards are
    /// derived from this value. The #437 model swap raises it.
    /// </summary>
    public const int MaximumTokens = 512;
}
