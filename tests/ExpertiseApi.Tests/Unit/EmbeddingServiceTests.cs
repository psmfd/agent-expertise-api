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
    public void BuildQueryInputText_PrefixesBgeQueryInstruction()
    {
        // bge-family asymmetric retrieval: the query side carries the instruction, the
        // document side (BuildInputText) never does (#424). If the instruction wording
        // ever changes (model swap), stored document embeddings stay valid — only
        // query-side embedding changes — but this pin forces the change to be deliberate.
        EmbeddingService.BuildQueryInputText("pgvector index tuning")
            .Should().Be("Represent this sentence for searching relevant passages: pgvector index tuning");

        EmbeddingService.BuildInputText("title", "body")
            .Should().Be("title body", "document-side input must remain unprefixed");
    }

    [Fact]
    public async Task GenerateQueryEmbeddingAsync_EmbedsPrefixedQuery_NotRawQuery()
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
            TestHelpers.CreateContentEmbedding(EmbeddingService.BuildQueryInputText("kafka rebalance")),
            "the query embedding must be generated from the instruction-prefixed text");
        queryVector.ToArray().Should().NotEqual(
            TestHelpers.CreateContentEmbedding("kafka rebalance"),
            "embedding the raw query would silently drop the bge query instruction");
    }
}
