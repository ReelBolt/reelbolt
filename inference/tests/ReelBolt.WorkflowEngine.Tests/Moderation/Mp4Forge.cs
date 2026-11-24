using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Builds small H.264 MP4 files whose picture, frame durations and sync-sample table are chosen frame by frame, with real
/// ffmpeg and no stored media. The picture is encoded without B-frames, then the <c>stts</c> (durations) and <c>stss</c>
/// (sync samples) boxes are rewritten, so a file can claim a keyframe where there is none (a false sync entry) or hold one
/// frame on screen for ten hours. Used to build the files a crafted upload would use against the frame check.
/// </summary>
internal static class Mp4Forge
{
    /// <summary>One frame: its colour ('r' red, 'b' blue), its duration in ms, whether it is a real keyframe, and whether the index falsely lists it as one.</summary>
    internal readonly record struct Fr(char Colour, int Ms, bool RealKey = false, bool FalseSync = false);

    internal static Fr B(int ms, bool key = false) => new('b', ms, key);

    /// <summary>A red frame the index falsely lists as a keyframe.</summary>
    internal static Fr Rf(int ms) => new('r', ms, false, true);

    internal static Fr R(int ms) => new('r', ms);

    private static readonly Dictionary<char, (byte Y, byte U, byte V)> Colours = new()
    {
        ['r'] = (81, 90, 240),
        ['b'] = (41, 240, 110),
    };

    private const int Side = 64;

    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl"];

    internal static void Encode(IReadOnlyList<Fr> frames, string output)
    {
        List<int> keys = [.. frames.Select((f, i) => (f, i)).Where(x => x.f.RealKey).Select(x => x.i)];
        string tmp = output + ".tmp.mp4";
        string keyArgs;
        string rate;
        if (keys.Count > 200)
        {
            keyArgs = "-force_key_frames " + string.Join(",", keys.Select(k => (k / 1000.0).ToString("0.000000", CultureInfo.InvariantCulture)));
            rate = "1000";
        }
        else
        {
            keyArgs = "-force_key_frames expr:" + string.Join("+", keys.Select(k => $"eq(n,{k})"));
            rate = "25";
        }

        using (Process p = Process.Start(new ProcessStartInfo("ffmpeg",
                   $"-y -loglevel error -f rawvideo -pix_fmt yuv420p -s {Side}x{Side} -r {rate} -i - -c:v libx264 -preset veryfast -bf 0 -g 100000 "
                   + $"-keyint_min 1 -sc_threshold 0 -x264-params intra-refresh=0:ref=1 {keyArgs} -pix_fmt yuv420p -video_track_timescale 1000 -an \"{tmp}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true
        })!)
        {
            Dictionary<char, byte[]> cache = [];
            using (Stream stdin = p.StandardInput.BaseStream)
            {
                foreach (Fr f in frames)
                {
                    if (!cache.TryGetValue(f.Colour, out byte[]? raw))
                    {
                        (byte y, byte u, byte v) = Colours[f.Colour];
                        raw = [.. Enumerable.Repeat(y, Side * Side), .. Enumerable.Repeat(u, Side * Side / 4), .. Enumerable.Repeat(v, Side * Side / 4)];
                        cache[f.Colour] = raw;
                    }

                    stdin.Write(raw);
                }
            }

            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(error);
        }

        try
        {
            Patch(tmp, output, frames);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private sealed class Box(string type, byte[]? payload, List<Box>? children, byte[]? raw)
    {
        public string Type { get; } = type;

        public byte[]? Payload { get; set; } = payload;

        public List<Box>? Children { get; } = children;

        public byte[]? Raw { get; } = raw;

        public byte[] Serialize()
        {
            if (Raw is not null) return Raw;
            byte[] body = Children is null ? Payload! : [.. Children.SelectMany(c => c.Serialize())];
            byte[] head = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(8 + body.Length));
            Encoding.ASCII.GetBytes(Type).CopyTo(head, 4);
            return [.. head, .. body];
        }
    }

    private static List<Box> Parse(byte[] b, int start, int end, bool top)
    {
        List<Box> boxes = [];
        int at = start;
        while (at < end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
            string type = Encoding.ASCII.GetString(b, at + 4, 4);
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            int next = at + (int)size;
            if (Containers.Contains(type))
                boxes.Add(new Box(type, null, Parse(b, at + header, next, false), null));
            else if (top)
                boxes.Add(new Box(type, null, null, b[at..next]));
            else
                boxes.Add(new Box(type, b[(at + header)..next], null, null));
            at = next;
        }

        return boxes;
    }

    private static Box Find(IEnumerable<Box> boxes, string type) => boxes.First(x => x.Type == type);

    private static void Patch(string source, string destination, IReadOnlyList<Fr> frames)
    {
        byte[] file = File.ReadAllBytes(source);
        List<Box> top = Parse(file, 0, file.Length, true);
        Box moov = Find(top, "moov");
        Box trak = Find(moov.Children!, "trak");
        Box mdia = Find(trak.Children!, "mdia");
        Box stbl = Find(Find(mdia.Children!, "minf").Children!, "stbl");

        byte[] stsz = Find(stbl.Children!, "stsz").Payload!;
        if (BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(8)) != frames.Count)
            throw new InvalidOperationException("the encoder dropped frames");

        // Durations: runs of equal ones.
        List<(int Count, int Delta)> runs = [];
        foreach (Fr f in frames)
        {
            if (runs.Count > 0 && runs[^1].Delta == f.Ms) runs[^1] = (runs[^1].Count + 1, f.Ms);
            else runs.Add((1, f.Ms));
        }

        Box stts = Find(stbl.Children!, "stts");
        byte[] sttsBody = new byte[8 + (runs.Count * 8)];
        BinaryPrimitives.WriteUInt32BigEndian(sttsBody.AsSpan(4), (uint)runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sttsBody.AsSpan(8 + (i * 8)), (uint)runs[i].Count);
            BinaryPrimitives.WriteUInt32BigEndian(sttsBody.AsSpan(12 + (i * 8)), (uint)runs[i].Delta);
        }

        stts.Payload = sttsBody;

        // Sync samples: the encoder's real keyframes plus the frames that falsely claim to be one.
        Box? stss = stbl.Children!.FirstOrDefault(x => x.Type == "stss");
        if (stss is not null)
        {
            SortedSet<int> sync = [];
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(stss.Payload!.AsSpan(4));
            for (int i = 0; i < n; i++) sync.Add((int)BinaryPrimitives.ReadUInt32BigEndian(stss.Payload.AsSpan(8 + (i * 4))) - 1);
            for (int i = 0; i < frames.Count; i++) if (frames[i].FalseSync) sync.Add(i);
            byte[] body = new byte[8 + (sync.Count * 4)];
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)sync.Count);
            int at = 0;
            foreach (int s in sync) BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8 + (at++ * 4)), (uint)(s + 1));
            stss.Payload = body;
        }

        long total = frames.Sum(f => (long)f.Ms);
        byte[] mdhd = Find(mdia.Children!, "mdhd").Payload!;
        BinaryPrimitives.WriteUInt32BigEndian(mdhd.AsSpan(16), (uint)total);
        byte[] tkhd = Find(trak.Children!, "tkhd").Payload!;
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(20), (uint)total);
        byte[] mvhd = Find(moov.Children!, "mvhd").Payload!;
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)(total * timescale / 1000));
        trak.Children!.RemoveAll(x => x.Type == "edts");

        File.WriteAllBytes(destination, [.. top.SelectMany(x => x.Serialize())]);
    }
}
