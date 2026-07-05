using System.Security.Cryptography;
using System.Text;

namespace ExpertiseApi.Services;

/// <summary>
/// RFC 6962 (Certificate Transparency) Merkle Tree Hash over an ordered list of
/// record hashes (ADR-012). Leaf input is the UTF-8 bytes of the record's lowercase
/// hex <c>RecordHash</c>; domain separation follows the RFC exactly:
/// <c>MTH({}) = SHA-256()</c>, <c>leaf = SHA-256(0x00 || data)</c>,
/// <c>node = SHA-256(0x01 || left || right)</c>, splitting at the largest power of
/// two strictly less than n. The RFC construction (rather than a flat hash-of-hashes)
/// is deliberate: inclusion proofs come for free later, when down-sync reconciliation
/// (ADR-013 deferral list, #342) needs to prove a single entry is covered by a
/// published root without shipping the full leaf set.
/// </summary>
internal static class MerkleTree
{
    private const byte LeafPrefix = 0x00;
    private const byte NodePrefix = 0x01;

    /// <param name="leafData">Ordered record hashes (lowercase hex strings), export order.</param>
    /// <returns>Lowercase hex Merkle Tree Hash.</returns>
    public static string ComputeRoot(IReadOnlyList<string> leafData)
    {
        ArgumentNullException.ThrowIfNull(leafData);
        return Convert.ToHexStringLower(Mth(leafData, 0, leafData.Count));
    }

    private static byte[] Mth(IReadOnlyList<string> leaves, int offset, int count)
    {
        if (count == 0)
            return SHA256.HashData([]);

        if (count == 1)
        {
            var data = Encoding.UTF8.GetBytes(leaves[offset]);
            var buffer = new byte[1 + data.Length];
            buffer[0] = LeafPrefix;
            data.CopyTo(buffer, 1);
            return SHA256.HashData(buffer);
        }

        var k = LargestPowerOfTwoBelow(count);
        var left = Mth(leaves, offset, k);
        var right = Mth(leaves, offset + k, count - k);

        var node = new byte[1 + left.Length + right.Length];
        node[0] = NodePrefix;
        left.CopyTo(node, 1);
        right.CopyTo(node, 1 + left.Length);
        return SHA256.HashData(node);
    }

    private static int LargestPowerOfTwoBelow(int n)
    {
        // Largest power of two strictly less than n (n >= 2 here).
        var k = 1;
        while (k * 2 < n)
            k *= 2;
        return k;
    }
}
