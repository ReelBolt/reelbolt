using System.Buffers.Binary;

namespace ReelBolt.Shared.Metering;

/// <summary>Reads a WAV's duration from its header, which is what ASR is billed on (audio seconds).</summary>
public static class WavDuration
{
    /// <summary>
    /// Most RIFF chunks the walk steps over before giving up. A real WAV has a handful (<c>fmt </c>,
    /// <c>data</c>, maybe <c>LIST</c>/<c>fact</c>/<c>bext</c>); the cap is what keeps a crafted file with
    /// thousands of empty chunks from being walked forever.
    /// </summary>
    private const int MaxChunks = 512;

    private const int RiffHeaderBytes = 12;
    private const int ChunkHeaderBytes = 8;

    /// <summary>
    /// Duration in seconds from the RIFF chunks: data-chunk bytes divided by the format chunk's byte rate. Every
    /// chunk header is followed wherever it says, so a large metadata chunk before <c>data</c> (a <c>LIST</c>
    /// with tags, an <c>ID3 </c> block, a <c>bext</c> broadcast description) does not hide the audio — a seek per
    /// chunk, never a read of the chunk's body, and at most <see cref="MaxChunks"/> chunks. A streamed WAV whose
    /// data size is 0 or 0xFFFFFFFF (ffmpeg writing to a pipe) uses the bytes that actually follow. Needs a
    /// seekable stream and leaves its position untouched; false when it cannot tell.
    /// </summary>
    public static bool TryGetSeconds(Stream wav, out double seconds)
    {
        seconds = 0;
        if (!wav.CanSeek) return false;

        long origin = wav.Position;
        try
        {
            long length = wav.Length;
            byte[] header = new byte[RiffHeaderBytes];
            if (!ReadAt(wav, 0, header, length) ||
                !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                return false;
            }

            uint byteRate = 0;
            byte[] chunkHeader = new byte[ChunkHeaderBytes];
            byte[] format = new byte[12];
            long pos = RiffHeaderBytes;
            for (int chunk = 0; chunk < MaxChunks; chunk++)
            {
                if (!ReadAt(wav, pos, chunkHeader, length))
                    return false;

                ReadOnlySpan<byte> id = chunkHeader.AsSpan(0, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                long body = pos + ChunkHeaderBytes;

                if (id.SequenceEqual("fmt "u8))
                {
                    // 16-byte PCM, 18-byte and 40-byte (WAVE_FORMAT_EXTENSIBLE) chunks all carry the byte
                    // rate at body+8, so only those twelve bytes are ever read.
                    byteRate = ReadAt(wav, body, format, length)
                        ? BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8, 4))
                        : 0;
                }
                else if (id.SequenceEqual("data"u8))
                {
                    if (byteRate == 0) return false;
                    long available = Math.Max(0, length - body);
                    long dataBytes = size == 0 || size == uint.MaxValue || size > available ? available : size;
                    seconds = (double)dataBytes / byteRate;
                    return true;
                }

                // RIFF pads every chunk to an even length; a chunk that neither advances nor fits is
                // malformed, and walking it would either loop or leave the file.
                long next = body + size + (size & 1);
                if (next <= pos || next > length)
                    return false;
                pos = next;
            }

            return false;
        }
        finally
        {
            wav.Position = origin;
        }
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> from <paramref name="offset"/>, false when the stream ends first. The
    /// only reads this class makes: 12 bytes for the RIFF header, then 8 (and 12 for <c>fmt </c>) per chunk.
    /// </summary>
    private static bool ReadAt(Stream wav, long offset, byte[] buffer, long length)
    {
        if (offset < 0 || offset + buffer.Length > length)
            return false;

        wav.Position = offset;
        int read = 0;
        while (read < buffer.Length)
        {
            int count = wav.Read(buffer, read, buffer.Length - read);
            if (count <= 0)
                return false;
            read += count;
        }

        return true;
    }
}
