using Microsoft.Extensions.AI;
using Pgvector;

namespace ExpertiseApi.Services;

internal class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
    // Bounds in-flight ONNX inference process-wide (static: the service is
    // scoped, the memory is not). A ceiling-filling embed transiently peaks
    // ~12 GB RSS at MaximumTokens = 6144 (ADR-017 ground truth); without this
    // gate, concurrent max-length writes stack those transients — an
    // authenticated memory-exhaustion path on the single-host A2 target
    // (review finding, 2026-07-23). The per-minute rate limiter bounds request
    // RATE, not overlap; this bounds overlap. Serialized embeds cost ~1s of
    // queueing only under exactly the burst that would otherwise exhaust the
    // host.
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);

    // jina-embeddings-v2-small-en is trained symmetrically: queries and documents
    // embed identically, with NO instruction prefix (ADR-017). The bge-family
    // query instruction PR #431 added was model-specific and was removed with the
    // swap — a model change must re-verify prefix requirements against the new
    // model's card (bge needs one, jina forbids none-vs-some asymmetry).
    public static string BuildInputText(string title, string body) => $"{title} {body}";

    public Task<Vector> GenerateQueryEmbeddingAsync(string query, CancellationToken ct = default)
        => GenerateEmbeddingAsync(query, ct);

    public async Task<Vector> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        await InferenceGate.WaitAsync(ct);
        try
        {
            var result = await generator.GenerateAsync([text], cancellationToken: ct);
            return new Vector(result[0].Vector.ToArray());
        }
        finally
        {
            InferenceGate.Release();
        }
    }

    public async Task<IReadOnlyList<Vector>> GenerateBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var list = texts.ToList();
        await InferenceGate.WaitAsync(ct);
        try
        {
            var results = await generator.GenerateAsync(list, cancellationToken: ct);
            return results.Select(r => new Vector(r.Vector.ToArray())).ToList();
        }
        finally
        {
            InferenceGate.Release();
        }
    }
}
