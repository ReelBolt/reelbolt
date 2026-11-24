using System.Globalization;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One min/max pair for one channel over one bucket of a peak LOD.</summary>
public sealed record PcmPeakBucket(short Min, short Max);

/// <summary>
/// One level of detail: the bucket width in milliseconds, the bucket count per channel, and the
/// channel-major buckets (<c>Channels[c][b]</c>).
/// </summary>
public sealed record PcmPeakLod(int LodMs, int BucketCount, IReadOnlyList<IReadOnlyList<PcmPeakBucket>> Channels);

/// <summary>The whole sampled result: the decode's stream shape plus one entry per requested LOD.</summary>
public sealed record PcmPeakResult(int SampleRate, int Channels, IReadOnlyList<PcmPeakLod> Lods);

/// <summary>
/// Pure, unit-testable static class that windows a raw signed-16-bit little-endian PCM stream
/// (the exact format the peaks pass decodes to) into per-channel min/max pairs at several bucket
/// widths. No ffmpeg dependency and no process I/O — it operates on bytes already on disk/in
/// memory.
/// </summary>
/// <remarks>
/// <para>
/// It sits next to <see cref="WavRmsSampler"/> and deliberately reuses that class's windowing
/// discipline: frames are indexed by <c>bytesPerFrame = 2 * channels</c>, the last bucket is
/// short rather than padded, and a malformed input yields an EMPTY result rather than throwing —
/// this feature's standing contract for anything that reaches a step executor.
/// </para>
/// <para>
/// <b>Binary layout</b> (documented here because it is the wire contract the editor decodes):
/// for each LOD a separate object, channel-major —
/// <c>[lod][channel][bucket]{int16 min, int16 max}</c>, little-endian. So one LOD object is
/// <c>channels * bucketCount * 4</c> bytes, channel 0's buckets first. The companion JSON sidecar
/// (see <see cref="BuildSidecarJson"/>) carries <c>{ sampleRate, channels, lodsMs, bucketCounts,
/// layout, byteOrder }</c> so a client can index into it without guessing.
/// </para>
/// </remarks>
public static class PcmPeakSampler
{
    /// <summary>The three LOD bucket widths, in milliseconds — the editor's zoom levels.</summary>
    public static readonly int[] DefaultLodsMs = [20, 100, 1000];

    /// <summary>
    /// Samples <paramref name="rawPcm"/> into one <see cref="PcmPeakLod"/> per entry of
    /// <paramref name="lodsMs"/>. Anything malformed — empty input, a non-positive sample rate or
    /// channel count, no requested LODs, fewer than one whole frame — returns a result with an
    /// empty <see cref="PcmPeakResult.Lods"/> instead of throwing.
    /// </summary>
    public static PcmPeakResult Sample(
        byte[]? rawPcm, int sampleRate, int channels, IReadOnlyList<int>? lodsMs = null)
    {
        IReadOnlyList<int> lods = lodsMs ?? DefaultLodsMs;
        var empty = new PcmPeakResult(Math.Max(0, sampleRate), Math.Max(0, channels), []);

        if (rawPcm is null || rawPcm.Length < 2 || sampleRate <= 0 || channels < 1 || lods.Count == 0)
            return empty;

        int bytesPerFrame = 2 * channels;
        int totalFrames = rawPcm.Length / bytesPerFrame;
        if (totalFrames <= 0)
            return empty;

        var results = new List<PcmPeakLod>(lods.Count);

        foreach (int lodMs in lods)
        {
            if (lodMs <= 0)
                return empty;

            int bucketFrames = Math.Max(1, (int)Math.Round(sampleRate * (lodMs / 1000.0)));
            int bucketCount = (totalFrames + bucketFrames - 1) / bucketFrames;

            var perChannel = new List<IReadOnlyList<PcmPeakBucket>>(channels);
            for (int c = 0; c < channels; c++)
            {
                var buckets = new List<PcmPeakBucket>(bucketCount);
                for (int b = 0; b < bucketCount; b++)
                {
                    int frameStart = b * bucketFrames;
                    int frameEnd = Math.Min(totalFrames, frameStart + bucketFrames);

                    short min = short.MaxValue;
                    short max = short.MinValue;
                    for (int f = frameStart; f < frameEnd; f++)
                    {
                        int offset = f * bytesPerFrame + c * 2;
                        short sample = (short)(rawPcm[offset] | (rawPcm[offset + 1] << 8));
                        if (sample < min)
                            min = sample;
                        if (sample > max)
                            max = sample;
                    }

                    buckets.Add(new PcmPeakBucket(min, max));
                }

                perChannel.Add(buckets);
            }

            results.Add(new PcmPeakLod(lodMs, bucketCount, perChannel));
        }

        return new PcmPeakResult(sampleRate, channels, results);
    }

    /// <summary>
    /// Serializes one LOD to its object's bytes: channel-major
    /// <c>[channel][bucket]{int16 min, int16 max}</c>, little-endian.
    /// </summary>
    public static byte[] SerializeLod(PcmPeakLod lod)
    {
        ArgumentNullException.ThrowIfNull(lod);

        int channels = lod.Channels.Count;
        byte[] bytes = new byte[channels * lod.BucketCount * 4];
        int offset = 0;

        for (int c = 0; c < channels; c++)
        {
            IReadOnlyList<PcmPeakBucket> buckets = lod.Channels[c];
            for (int b = 0; b < lod.BucketCount; b++)
            {
                PcmPeakBucket bucket = buckets[b];
                WriteInt16LittleEndian(bytes, ref offset, bucket.Min);
                WriteInt16LittleEndian(bytes, ref offset, bucket.Max);
            }
        }

        return bytes;
    }

    /// <summary>
    /// The companion sidecar: <c>{ sampleRate, channels, lodsMs, bucketCounts, layout, byteOrder }</c>.
    /// <c>bucketCounts</c> is parallel to <c>lodsMs</c> — one object per LOD, in order.
    /// </summary>
    public static string BuildSidecarJson(PcmPeakResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"sampleRate\":").Append(Number(result.SampleRate)).Append(',');
        builder.Append("\"channels\":").Append(Number(result.Channels)).Append(',');
        builder.Append("\"lodsMs\":[")
               .Append(string.Join(',', result.Lods.Select(l => Number(l.LodMs))))
               .Append("],");
        builder.Append("\"bucketCounts\":[")
               .Append(string.Join(',', result.Lods.Select(l => Number(l.BucketCount))))
               .Append("],");
        builder.Append("\"layout\":\"ch,min,max\",");
        builder.Append("\"byteOrder\":\"le\"");
        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>The object leaf for one LOD's raw bytes.</summary>
    public static string LodLeaf(string paramsHash, int lodMs) =>
        $"peaks-{paramsHash}-lod{Number(lodMs).Replace(".", "_", StringComparison.Ordinal)}ms.raw";

    /// <summary>The object leaf for the peaks sidecar.</summary>
    public static string IndexLeaf(string paramsHash) => $"peaks-{paramsHash}-index.json";

    private static void WriteInt16LittleEndian(byte[] target, ref int offset, short value)
    {
        target[offset++] = (byte)(value & 0xFF);
        target[offset++] = (byte)((value >> 8) & 0xFF);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
