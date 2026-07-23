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
}
