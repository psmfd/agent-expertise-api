using Microsoft.Extensions.AI;
using Pgvector;

namespace ExpertiseApi.Services;

internal class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
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
        var result = await generator.GenerateAsync([text], cancellationToken: ct);
        return new Vector(result[0].Vector.ToArray());
    }

    public async Task<IReadOnlyList<Vector>> GenerateBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var list = texts.ToList();
        var results = await generator.GenerateAsync(list, cancellationToken: ct);
        return results.Select(r => new Vector(r.Vector.ToArray())).ToList();
    }
}
