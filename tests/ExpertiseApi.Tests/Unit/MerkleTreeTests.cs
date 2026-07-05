using System.Security.Cryptography;
using System.Text;
using ExpertiseApi.Services;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Verifies the RFC 6962 Merkle Tree Hash construction (ADR-012) against
/// expected values computed here with raw SHA-256 calls — an independent
/// derivation of the leaf/node prefixing and the largest-power-of-two split
/// rule, not a mirror of the implementation's recursion.
/// </summary>
public class MerkleTreeTests
{
    private static byte[] Leaf(string data) =>
        SHA256.HashData([0x00, .. Encoding.UTF8.GetBytes(data)]);

    private static byte[] Node(byte[] left, byte[] right) =>
        SHA256.HashData([0x01, .. left, .. right]);

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

    [Fact]
    public void EmptyList_IsSha256OfEmptyString()
    {
        // RFC 6962 §2.1: MTH({}) = SHA-256().
        MerkleTree.ComputeRoot([]).Should().Be(Hex(SHA256.HashData(Array.Empty<byte>())));
    }

    [Fact]
    public void SingleLeaf_IsPrefixedLeafHash()
    {
        MerkleTree.ComputeRoot(["aa"]).Should().Be(Hex(Leaf("aa")));
    }

    [Fact]
    public void TwoLeaves_IsPrefixedNodeOverLeafHashes()
    {
        var expected = Node(Leaf("aa"), Leaf("bb"));
        MerkleTree.ComputeRoot(["aa", "bb"]).Should().Be(Hex(expected));
    }

    [Fact]
    public void ThreeLeaves_SplitsAtLargestPowerOfTwoBelowCount()
    {
        // n=3 → k=2: MTH = node(MTH([0,2)), MTH([2,3))) = node(node(L0,L1), L2).
        var expected = Node(Node(Leaf("aa"), Leaf("bb")), Leaf("cc"));
        MerkleTree.ComputeRoot(["aa", "bb", "cc"]).Should().Be(Hex(expected));
    }

    [Fact]
    public void FiveLeaves_SplitsFourOne()
    {
        // n=5 → k=4 (largest power of two strictly below 5, NOT ceil(n/2)=3).
        var left = Node(Node(Leaf("a"), Leaf("b")), Node(Leaf("c"), Leaf("d")));
        var expected = Node(left, Leaf("e"));
        MerkleTree.ComputeRoot(["a", "b", "c", "d", "e"]).Should().Be(Hex(expected));
    }

    [Fact]
    public void LeafOrder_ChangesRoot()
    {
        MerkleTree.ComputeRoot(["aa", "bb"]).Should().NotBe(MerkleTree.ComputeRoot(["bb", "aa"]));
    }

    [Fact]
    public void LeafAndNodePrefixes_DomainSeparate()
    {
        // A single leaf whose data equals a two-leaf tree's concatenated child
        // hashes must not collide with that tree's root — the 0x00/0x01
        // prefixes exist precisely to prevent second-preimage splicing.
        var twoLeafRoot = MerkleTree.ComputeRoot(["aa", "bb"]);
        var spliced = MerkleTree.ComputeRoot([Hex(Leaf("aa")) + Hex(Leaf("bb"))]);
        spliced.Should().NotBe(twoLeafRoot);
    }
}
