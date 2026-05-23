using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Endpoints;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pgvector;

namespace ExpertiseApi.Tests.Unit;

public class DeduplicationServiceTests
{
    private readonly IExpertiseRepository _repo = Substitute.For<IExpertiseRepository>();
    private readonly Vector _testVector = TestHelpers.CreateTestVector();
    private readonly TenantContext _ctx = TestHelpers.CreateTenantContext();

    private DeduplicationService CreateService(bool enabled = true, double threshold = 0.10)
    {
        var options = Options.Create(new DeduplicationOptions
        {
            Enabled = enabled,
            SemanticThreshold = threshold
        });
        return new DeduplicationService(_repo, options);
    }

    private static CreateExpertiseRequest CreateRequest(
        string domain = "shared",
        string title = "Test",
        string body = "Test body") =>
        new(domain, title, body, EntryType.Pattern, Severity.Info, "test");

    [Fact]
    public async Task CheckAsync_WhenDisabled_ReturnsNotDuplicate()
    {
        var service = CreateService(enabled: false);
        var request = CreateRequest();

        var (isDuplicate, existing) = await service.CheckAsync(request, _testVector, _ctx);

        isDuplicate.Should().BeFalse();
        existing.Should().BeNull();
        await _repo.DidNotReceive().FindExactMatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WhenExactMatchWithSameBody_ReturnsDuplicate()
    {
        var service = CreateService();
        var request = CreateRequest(body: "Exact body");
        var existingEntry = TestHelpers.SeedEntry(body: "Exact body");

        _repo.FindExactMatchAsync("shared", "Test", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(existingEntry);

        var (isDuplicate, existing) = await service.CheckAsync(request, _testVector, _ctx);

        isDuplicate.Should().BeTrue();
        existing.Should().Be(existingEntry);
    }

    [Fact]
    public async Task CheckAsync_WhenExactMatchWithDifferentBody_FallsToSemanticCheck()
    {
        var service = CreateService();
        var request = CreateRequest(body: "New body");
        var existingEntry = TestHelpers.SeedEntry(body: "Different body");

        _repo.FindExactMatchAsync("shared", "Test", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(existingEntry);
        _repo.FindNearestInDomainAsync("shared", _testVector, 0.10, Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(default(ExpertiseEntry));

        var (isDuplicate, _) = await service.CheckAsync(request, _testVector, _ctx);

        isDuplicate.Should().BeFalse();
        await _repo.Received(1).FindNearestInDomainAsync("shared", _testVector, 0.10, Arg.Any<TenantContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WhenSemanticMatchBelowThreshold_ReturnsDuplicate()
    {
        var service = CreateService();
        var request = CreateRequest();
        var nearEntry = TestHelpers.SeedEntry(title: "Similar entry");

        _repo.FindExactMatchAsync("shared", "Test", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(default(ExpertiseEntry));
        _repo.FindNearestInDomainAsync("shared", _testVector, 0.10, Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(nearEntry);

        var (isDuplicate, existing) = await service.CheckAsync(request, _testVector, _ctx);

        isDuplicate.Should().BeTrue();
        existing.Should().Be(nearEntry);
    }

    [Fact]
    public async Task CheckAsync_WhenNoMatch_ReturnsNotDuplicate()
    {
        var service = CreateService();
        var request = CreateRequest();

        _repo.FindExactMatchAsync("shared", "Test", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(default(ExpertiseEntry));
        _repo.FindNearestInDomainAsync("shared", _testVector, 0.10, Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(default(ExpertiseEntry));

        var (isDuplicate, existing) = await service.CheckAsync(request, _testVector, _ctx);

        isDuplicate.Should().BeFalse();
        existing.Should().BeNull();
    }

    [Fact]
    public async Task CheckBatchAsync_WhenEmbeddingsCountMismatch_ThrowsArgumentException()
    {
        var service = CreateService();
        var requests = new List<CreateExpertiseRequest> { CreateRequest(), CreateRequest(title: "Other") };
        var vectors = new List<Vector> { _testVector }; // one fewer embedding than requests

        await service.Invoking(s => s.CheckBatchAsync(requests, vectors, _ctx))
            .Should().ThrowAsync<ArgumentException>()
            .WithParameterName("embeddings");
    }

    [Fact]
    public async Task CheckBatchAsync_WhenDisabled_ReturnsAllNotDuplicate()
    {
        var service = CreateService(enabled: false);
        var requests = new List<CreateExpertiseRequest> { CreateRequest(), CreateRequest(title: "Other") };
        var vectors = new List<Vector> { _testVector, _testVector };

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.IsDuplicate.Should().BeFalse());
    }

    [Fact]
    public async Task CheckBatchAsync_WithExactMatch_ReturnsDuplicate()
    {
        var service = CreateService();
        var requests = new List<CreateExpertiseRequest> { CreateRequest(body: "Exact body") };
        var vectors = new List<Vector> { _testVector };
        var existingEntry = TestHelpers.SeedEntry(title: "Test", body: "Exact body");

        _repo.FindExactMatchesAsync("shared", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns([existingEntry]);

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(1);
        results[0].IsDuplicate.Should().BeTrue();
        results[0].Existing.Should().Be(existingEntry);
    }

    [Fact]
    public async Task CheckBatchAsync_WithNoMatches_ReturnsAllNotDuplicate()
    {
        var service = CreateService();
        var requests = new List<CreateExpertiseRequest> { CreateRequest(), CreateRequest(title: "Other") };
        var vectors = new List<Vector> { _testVector, _testVector };

        _repo.FindExactMatchesAsync("shared", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpertiseEntry>());
        _repo.FindAllEmbeddingsInDomainAsync("shared", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpertiseEntry>());

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.IsDuplicate.Should().BeFalse());
    }

    [Fact]
    public async Task CheckBatchAsync_WithSemanticMatch_ReturnsDuplicate()
    {
        var service = CreateService();
        var requests = new List<CreateExpertiseRequest> { CreateRequest(title: "Unique Title") };
        var vectors = new List<Vector> { _testVector };

        // No exact title match — entry title differs
        var domainEntry = TestHelpers.SeedEntry(title: "Similar But Different Title");
        // Use the same vector so cosine distance == 0, well below the 0.10 threshold
        domainEntry.Embedding = _testVector;

        _repo.FindExactMatchesAsync("shared", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpertiseEntry>());
        _repo.FindAllEmbeddingsInDomainAsync("shared", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns([domainEntry]);

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(1);
        results[0].IsDuplicate.Should().BeTrue();
        results[0].Existing.Should().Be(domainEntry);
    }

    [Fact]
    public async Task CheckBatchAsync_WithSemanticMatchAboveThreshold_ReturnsNotDuplicate()
    {
        var service = CreateService(threshold: 0.01); // very tight threshold
        var requests = new List<CreateExpertiseRequest> { CreateRequest(title: "Unique Title") };

        // Build a vector that is orthogonal to _testVector (cosine distance == 1)
        var orthogonalValues = new float[384];
        orthogonalValues[0] = 1.0f; // all other dims zero — orthogonal to random _testVector
        var farVector = new Vector(orthogonalValues);

        var vectors = new List<Vector> { farVector };

        var domainEntry = TestHelpers.SeedEntry(title: "Similar But Different Title");
        domainEntry.Embedding = _testVector; // very different direction

        _repo.FindExactMatchesAsync("shared", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpertiseEntry>());
        _repo.FindAllEmbeddingsInDomainAsync("shared", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns([domainEntry]);

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(1);
        results[0].IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckBatchAsync_WithMixedResults_ReturnsCorrectPerItem()
    {
        var service = CreateService();
        var requests = new List<CreateExpertiseRequest>
        {
            CreateRequest(title: "Duplicate", body: "Same body"),
            CreateRequest(title: "Unique", body: "Different body")
        };
        var vectors = new List<Vector> { _testVector, _testVector };

        var existingEntry = TestHelpers.SeedEntry(title: "Duplicate", body: "Same body");
        _repo.FindExactMatchesAsync("shared", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns([existingEntry]);
        _repo.FindAllEmbeddingsInDomainAsync("shared", Arg.Any<TenantContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpertiseEntry>());

        var results = await service.CheckBatchAsync(requests, vectors, _ctx);

        results.Should().HaveCount(2);
        results[0].IsDuplicate.Should().BeTrue();
        results[1].IsDuplicate.Should().BeFalse();
    }
}
