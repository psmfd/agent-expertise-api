using System.Security.Cryptography;
using System.Text;

namespace ExpertiseApi.Tests.Unit;

public class ApiKeyAuthHandlerTests
{
    [Fact]
    public void FixedTimeEquals_WithMatchingKeys_ReturnsTrue()
    {
        var key = "test-api-key-12345";
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        CryptographicOperations.FixedTimeEquals(expectedHash, providedHash).Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_WithDifferentKeys_ReturnsFalse()
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("correct-key"));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes("wrong-key"));

        CryptographicOperations.FixedTimeEquals(expectedHash, providedHash).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_WithDifferentLengthKeys_ReturnsFalse()
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("short"));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes("a-much-longer-key-value"));

        CryptographicOperations.FixedTimeEquals(expectedHash, providedHash).Should().BeFalse();
    }

    [Fact]
    public void SHA256HashData_AlwaysProduces32Bytes()
    {
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes("short"));
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes("a-much-longer-key"));

        hash1.Length.Should().Be(32);
        hash2.Length.Should().Be(32);
    }
}
