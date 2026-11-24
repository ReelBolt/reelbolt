using System;
using System.IO;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Csam;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// Pins the local PDQ implementation against Meta's official regression vector, so a library drift
/// that silently stops matching a real hash is a failing test rather than a quiet production gap.
/// </summary>
public class PdqHasherTests
{
    /// <summary>The official PDQ of bridge-1-original.jpg, documented by Meta's ThreatExchange PDQ suite.</summary>
    private const string OfficialHex = "d8f8f0cee0f4a84f0637022a078f67f0b36e2ed596621e1d33e6339c4e9c9b22";

    /// <summary>The exact bytes of the committed fixture; a different image would make the vector meaningless.</summary>
    private const string OfficialSha256 = "b5b0799616df52d475a3968dc7e54f1d0724c912244ffa6175bc786375dd7298";

    [Fact]
    public void OfficialMetaVector_IsWithinHammingDistance11_AndBase64Is32Bytes()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "PdqParity", "bridge-1-original.jpg");
        File.Exists(path).Should().BeTrue($"the committed fixture must be copied to the output directory: {path}");
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant().Should().Be(OfficialSha256, "the regression vector must be Meta's exact bytes");

        using FileStream stream = File.OpenRead(path);
        PdqHasher hasher = new();

        bool ok = hasher.TryHash(stream, out string base64Hash, out int quality);

        ok.Should().BeTrue();
        quality.Should().BeGreaterThan(0);
        byte[] computed = Convert.FromBase64String(base64Hash);
        computed.Should().HaveCount(32, "a PDQ hash is 32 bytes");

        // Observed on 2026-10-21: Hamming distance 4 from the official hex (the library promises <= 11).
        int distance = HammingDistance(computed, Convert.FromHexString(OfficialHex));
        distance.Should().BeLessThanOrEqualTo(11);
        distance.Should().Be(4, "the measured distance for this fixture; a move means the implementation changed");
    }

    [Fact]
    public void BytesThatAreNotAnImage_ReturnFalse_InsteadOfThrowing()
    {
        using MemoryStream stream = new("this is not an image"u8.ToArray());

        bool ok = new PdqHasher().TryHash(stream, out string hash, out int quality);

        ok.Should().BeFalse();
        hash.Should().BeEmpty();
        quality.Should().Be(0);
    }

    private static int HammingDistance(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int distance = 0;
        for (int i = 0; i < left.Length; i++)
            distance += System.Numerics.BitOperations.PopCount((uint)(left[i] ^ right[i]));
        return distance;
    }
}
