using Microsoft.Extensions.AI;
using Pgvector;

namespace ExpertiseApi.Services;

internal class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
    // bge-family models are trained asymmetrically for retrieval: the QUERY side carries
    // this instruction, the document side never does. bge-micro-v2's own card omits it,
    // but the model is distilled from BAAI/bge-small-en-v1.5, whose card specifies it
    // (optional in v1.5 — "slight degradation" without — and most beneficial for short
    // queries, which is exactly this API's agent query profile). Documents embedded via
    // BuildInputText and the dedup path stay unprefixed: dedup compares document-vs-
    // document, which is symmetric (#424).
    internal const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    public static string BuildInputText(string title, string body) => $"{title} {body}";

    public static string BuildQueryInputText(string query) => $"{QueryInstruction}{query}";

    public Task<Vector> GenerateQueryEmbeddingAsync(string query, CancellationToken ct = default)
        => GenerateEmbeddingAsync(BuildQueryInputText(query), ct);

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
