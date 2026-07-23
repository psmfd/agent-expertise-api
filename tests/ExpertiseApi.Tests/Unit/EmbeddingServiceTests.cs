using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace ExpertiseApi.Tests.Unit;

public class EmbeddingServiceTests
{
    [Fact]
    public async Task GenerateBatchAsync_MapsEachOutputToItsInputPosition()
    {
        // A content-derived generator (same shape as the test factory mocks, #353) so each
        // input maps to a DISTINGUISHABLE vector — the precondition that makes an ordering
        // bug observable at all. With the old content-independent mock (identical vector for
        // every input) a reorder/off-by-one in GenerateBatchAsync would have been invisible.
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.ArgAt<IEnumerable<string>>(0).ToList();
                var res = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var input in inputs)
                    res.Add(new Embedding<float>(TestHelpers.CreateContentEmbedding(input)));
                return Task.FromResult(res);
            });

        var service = new EmbeddingService(generator);
        var texts = new[] { "alpha input", "bravo input", "charlie input" };

        var vectors = await service.GenerateBatchAsync(texts);

        vectors.Should().HaveCount(3);
        for (var i = 0; i < texts.Length; i++)
        {
            vectors[i].ToArray().Should().Equal(
                TestHelpers.CreateContentEmbedding(texts[i]),
                $"output {i} must correspond to input {i} — a reorder would attach the wrong embedding to an entry");
        }
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_BoundsInferenceConcurrency_ToOne()
    {
        // Review finding 2026-07-23: a ceiling-filling embed transiently peaks
        // ~12 GB RSS (ADR-017), so overlapping inference is the memory-exhaustion
        // vector on the A2 single host. This pins the InferenceGate: N parallel
        // callers must never observe more than one in-flight generator call.
        var inFlight = 0;
        var maxObserved = 0;
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedExtensionsMax(ref maxObserved, now);
                await Task.Delay(30);
                Interlocked.Decrement(ref inFlight);
                var res = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var input in ci.ArgAt<IEnumerable<string>>(0))
                    res.Add(new Embedding<float>(TestHelpers.CreateContentEmbedding(input)));
                return res;
            });

        var service = new EmbeddingService(generator);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            i % 2 == 0
                ? service.GenerateEmbeddingAsync($"text {i}")
                : (Task)service.GenerateBatchAsync([$"batch {i}"])));

        maxObserved.Should().Be(1,
            "the static inference gate must serialize ONNX calls across single and batch paths");
    }

    private static void InterlockedExtensionsMax(ref int target, int candidate)
    {
        int snapshot;
        while (candidate > (snapshot = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, candidate, snapshot) == snapshot)
                break;
        }
    }

    [Fact]
    public void BuildInputText_RemainsUnprefixed()
    {
        // jina-v2-small embeds queries and documents symmetrically with NO
        // instruction prefix (ADR-017). This pin forces any future prefix
        // reintroduction (another model swap) to be deliberate — bge needed
        // one (PR #431), jina must not have one.
        EmbeddingService.BuildInputText("title", "body")
            .Should().Be("title body", "document-side input must remain unprefixed");
    }

    [Fact]
    public async Task GenerateQueryEmbeddingAsync_EmbedsRawQuery_NoInstructionPrefix()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.ArgAt<IEnumerable<string>>(0).ToList();
                var res = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var input in inputs)
                    res.Add(new Embedding<float>(TestHelpers.CreateContentEmbedding(input)));
                return Task.FromResult(res);
            });

        var service = new EmbeddingService(generator);

        var queryVector = await service.GenerateQueryEmbeddingAsync("kafka rebalance");

        queryVector.ToArray().Should().Equal(
            TestHelpers.CreateContentEmbedding("kafka rebalance"),
            "ADR-017: jina embeds the raw query — a leftover bge-style instruction prefix " +
            "would silently degrade every semantic search against the new model");
    }
}
