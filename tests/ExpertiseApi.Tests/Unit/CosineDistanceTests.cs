using ExpertiseApi.Data;

namespace ExpertiseApi.Tests.Unit;

public class CosineDistanceTests
{
    [Fact]
    public void IdenticalVectors_ShouldHaveZeroDistance()
    {
        var v = new float[] { 1.0f, 0.0f, 0.0f };
        ExpertiseRepository.CosineDistance(v, v).Should().BeApproximately(0.0, 1e-10);
    }

    [Fact]
    public void OrthogonalVectors_ShouldHaveDistanceOne()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };
        ExpertiseRepository.CosineDistance(a, b).Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void OppositeVectors_ShouldHaveDistanceTwo()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { -1.0f, 0.0f };
        ExpertiseRepository.CosineDistance(a, b).Should().BeApproximately(2.0, 1e-10);
    }

    [Fact]
    public void SimilarVectors_ShouldHaveSmallDistance()
    {
        var a = new float[] { 1.0f, 0.1f, 0.0f };
        var b = new float[] { 1.0f, 0.2f, 0.0f };
        var distance = ExpertiseRepository.CosineDistance(a, b);
        distance.Should().NotBeNull();
        distance!.Value.Should().BeGreaterThan(0.0);
        distance.Value.Should().BeLessThan(0.05);
    }

    [Fact]
    public void ZeroVector_ShouldProduceNaN()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 0.0f };
        var distance = ExpertiseRepository.CosineDistance(a, b);
        distance.Should().NotBeNull();
        double.IsNaN(distance!.Value).Should().BeTrue();
    }

    [Fact]
    public void MismatchedDimensions_StoredShorter_ShouldReturnNull()
    {
        var stored = new float[] { 1.0f, 0.5f };
        var query = new float[] { 1.0f, 0.5f, 0.3f };
        ExpertiseRepository.CosineDistance(stored, query).Should().BeNull();
    }

    [Fact]
    public void MismatchedDimensions_StoredLonger_ShouldReturnNull()
    {
        var stored = new float[] { 1.0f, 0.5f, 0.3f };
        var query = new float[] { 1.0f, 0.5f };
        ExpertiseRepository.CosineDistance(stored, query).Should().BeNull();
    }

    [Fact]
    public void MatchingDimensions_384dim_ShouldReturnValidDistance()
    {
        var a = new float[384];
        var b = new float[384];
        var rng = new Random(42);
        for (var i = 0; i < 384; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2 - 1);
            b[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var distance = ExpertiseRepository.CosineDistance(a, b);
        distance.Should().NotBeNull();
        distance!.Value.Should().BeInRange(0.0, 2.0);
    }
}
