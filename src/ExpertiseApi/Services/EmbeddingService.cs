using Microsoft.Extensions.AI;
using Pgvector;

namespace ExpertiseApi.Services;

internal class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
    public static string BuildInputText(string title, string body) => $"{title} {body}";

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
