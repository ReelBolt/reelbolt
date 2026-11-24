#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Where T-012's live-model evidence lands (<c>reports/T-012</c> when
/// <c>REELBOLT_T012_ARTIFACTS</c> points there), plus where the captured trial files are read
/// from (<c>REELBOLT_T012_TRIALS</c>, the trials directory).
///
/// <para>
/// <b>The vendor responses are untrusted data.</b> Nothing from <c>trials/</c> is executed: the JSON
/// is parsed, every RLE run is bounds-checked by the shipping <see cref="Sam31VideoRleDecoder"/>, and
/// the only binaries started in this file are ffmpeg/ffprobe over files this process wrote.
/// </para>
/// </summary>
public static class T012Artifacts
{
    public const string RootVariable = "REELBOLT_T012_ARTIFACTS";
    public const string TrialsVariable = "REELBOLT_T012_TRIALS";

    public static string Root { get; } = Resolve();
    public static string TrialsRoot { get; } = Environment.GetEnvironmentVariable(TrialsVariable) ?? string.Empty;

    public static bool TrialsAvailable =>
        TrialsRoot.Length > 0
        && Directory.Exists(Path.Combine(TrialsRoot, "clips"))
        && Directory.Exists(Path.Combine(TrialsRoot, "fal"));

    public static string Folder(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string MaskPath(T012Run run) => Path.Combine(Folder("masks"), run.Id + "-" + run.ClipId.ToLowerInvariant() + "-mask.mkv");

    public static string Scratch(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "t012-live", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Resolve()
    {
        string configured = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        string path = configured.Length == 0
            ? Path.Combine(Path.GetTempPath(), "t012-live-artifacts")
            : configured;
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>One captured (clip, prompt) pair: the vendor response file and the clip it segmented.</summary>
internal sealed record T012Run(string Id, string ClipId, string Prompt, int MaxObjects = 4)
{
    public string ResponsePath => Path.Combine(T012Artifacts.TrialsRoot, "fal", Id, ClipId, "response.json");

    public string ClipPath => Path.Combine(T012Artifacts.TrialsRoot, "clips", ClipId.ToLowerInvariant() + ".mp4");

    public string Label => Id + "/" + ClipId;
}

internal static class T012Runs
{
    public static readonly T012Run D1 = new("D1", "DASH", "speedometer");
    public static readonly T012Run D2 = new("D2", "DASH", "steering wheel");
    public static readonly T012Run D1b = new("D1b", "DASH", "speedometer");
    public static readonly T012Run F1 = new("F1", "FACE", "face");
    public static readonly T012Run F2 = new("F2", "FACE", "person");

    public static readonly IReadOnlyList<T012Run> All = [D1, D2, D1b, F1, F2];
}

/// <summary>How a vendor box is read: the vendor sends <c>[cx, cy, w, h]</c> (measured), not a corner.</summary>
internal sealed record T012BoxSample(
    bool HasBox,
    double CentreOffset,
    double CornerOffset,
    double InsideFraction,
    double BoxWidthPx,
    double BoxHeightPx,
    double MaskWidthPx,
    double MaskHeightPx,
    bool CentreInsideMask);

/// <summary>One frame of the chosen object's mask, measured by the shipping decoder's own numbers.</summary>
internal sealed record T012FrameSample(
    int Index, int Area, int X0, int Y0, int Width, int Height, double Cx, double Cy, double Bx, double By)
{
    public bool IsEmpty => Area == 0;
}

/// <summary>One object-frame where the decoded mask keeps less than the reader's floor inside its own vendor box.</summary>
internal sealed record T012BoxViolation(int Frame, int ObjectId, double InsidePercent, int AreaPx, double BoxWidthPx);

/// <summary>One vendor object slot's worst frame-to-frame movement, normalised by the frame diagonal.</summary>
internal sealed record T012ObjectJumps(
    int ObjectId, int FramesWithMask, double CentroidJump, int CentroidFrame, double BboxJump, int BboxFrame);

internal sealed class T012DecodeResult
{
    public required T012Run Run { get; init; }
    public required string ResponseSha256 { get; init; }
    public required int InputFrames { get; init; }
    public required int[] ObjectCounts { get; init; }
    public required IReadOnlyList<T012FrameSample> Frames { get; init; }
    public required IReadOnlyList<T012BoxSample> Boxes { get; init; }
    public required int ChosenObjectId { get; init; }
    public required Dictionary<int, long> ObjectPixels { get; init; }
    public required Dictionary<int, int> ObjectFrames { get; init; }
    public required string? ReaderRefusal { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<T012ObjectJumps> ObjectJumps { get; init; }
    public required IReadOnlyList<T012BoxViolation> BoxGateViolations { get; init; }
    public required double BoxGateMinimumInside { get; init; }
    public required int BoxGateMinimumFrame { get; init; }
    public required int BoxGateMinimumObject { get; init; }

    public int EmptyFrames => Frames.Count(f => f.IsEmpty);

    public double MeanCoverage => Frames.Average(f => f.Area / (double)(Width * Height));
}

/// <summary>
/// Reads one captured <c>fal-ai/sam-3-1/video-rle</c> document the way the product would: the shipping
/// <see cref="Sam31VideoRleResponseReader"/> validates the document (frame count, object slots, boxes,
/// union vs per-object masks) and the shipping <see cref="Sam31VideoRleDecoder"/> decodes every run.
/// Also writes the chosen object's mask as the FFV1 <c>gray</c> video the artifact contract names.
/// </summary>
internal static class T012Decode
{
    public const string Model = "fal-ai/sam-3-1/video-rle";

    public static string EnsureMask(T012Run run, int width, int height, int frames)
    {
        string path = T012Artifacts.MaskPath(run);
        if (!File.Exists(path))
            Read(run, width, height, frames, path);
        return path;
    }

    public static T012DecodeResult Read(T012Run run, int width, int height, int expectedFrames, string? maskVideoPath = null)
    {
        string json = File.ReadAllText(run.ResponsePath);
        string sha = Sha256OfText(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement frameArray = document.RootElement.GetProperty("objects");
        frameArray.GetArrayLength().Should().Be(expectedFrames,
            because: $"{run.Label} must answer the {expectedFrames} input frames one per frame");

        // The shipping reader is the contract: it refuses a document the request did not buy. A
        // refusal is recorded, not swallowed — the masks below still decode individually because the
        // statistics half of this ticket has to describe what the vendor actually returned.
        string? refusal = null;
        try
        {
            VideoSegmentationResult read = Sam31VideoRleResponseReader.Read(
                document.RootElement, Model, width, height, expectedFrames, run.MaxObjects,
                providerFingerprint: "fal/" + Model + "/captured-2026-10-18");
            read.InputFrames.Should().Be(expectedFrames);
        }
        catch (VideoSegmentationBadResponseException exception)
        {
            refusal = exception.Message;
        }

        var objectCounts = new int[expectedFrames];
        var violations = new List<T012BoxViolation>();
        double minimumInside = 1.0;
        int minimumInsideFrame = -1, minimumInsideObject = -1;
        var objectPixels = new Dictionary<int, long>();
        var objectFrames = new Dictionary<int, int>();
        var firstBox = new Dictionary<int, Sam31VideoRleDecoder.SegmentationBox>();
        double diagonal = Math.Sqrt((double)width * width + (double)height * height);
        var previousSample = new Dictionary<int, (double Cx, double Cy, double Bx, double By)>();
        var objectJumps = new Dictionary<int, (int Frames, double Centroid, int CentroidFrame, double Bbox, int BboxFrame)>();

        int index = 0;
        foreach (JsonElement frame in frameArray.EnumerateArray())
        {
            objectCounts[index] = frame.GetArrayLength();
            foreach (JsonElement entry in frame.EnumerateArray())
            {
                int objectId = entry.GetProperty("object_id").GetInt32();
                VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(
                    entry.GetProperty("rle").GetString(), objectId, width, height);
                objectPixels[objectId] = objectPixels.GetValueOrDefault(objectId) + mask.Area;
                if (mask.Area > 0)
                {
                    objectFrames[objectId] = objectFrames.GetValueOrDefault(objectId) + 1;

                    // Per-slot movement, so "the face tracks cleanly" and "a small edge slot's box
                    // jumps" can be two measured sentences rather than one impression.
                    var (slotArea, slotSumX, slotSumY, slotX0, slotY0, slotX1, slotY1) = MeasureBox(mask.Pixels, width, height);
                    double slotCx = slotSumX / (double)slotArea, slotCy = slotSumY / (double)slotArea;
                    double slotBx = (slotX0 + slotX1) / 2.0, slotBy = (slotY0 + slotY1) / 2.0;
                    if (previousSample.TryGetValue(objectId, out var previousSlot))
                    {
                        double centroidJump = Distance(previousSlot.Cx, previousSlot.Cy, slotCx, slotCy) / diagonal;
                        double bboxJump = Math.Sqrt(
                            Math.Pow((previousSlot.Bx - slotBx) / width, 2) + Math.Pow((previousSlot.By - slotBy) / height, 2));
                        var best = objectJumps.GetValueOrDefault(objectId, (0, 0, -1, 0, -1));
                        objectJumps[objectId] = (
                            best.Frames + 1,
                            Math.Max(best.Centroid, centroidJump),
                            centroidJump > best.Centroid ? index : best.CentroidFrame,
                            Math.Max(best.Bbox, bboxJump),
                            bboxJump > best.Bbox ? index : best.BboxFrame);
                    }
                    else
                    {
                        objectJumps[objectId] = (1, 0, -1, 0, -1);
                    }

                    previousSample[objectId] = (slotCx, slotCy, slotBx, slotBy);
                    if (!firstBox.ContainsKey(objectId) && entry.TryGetProperty("box", out JsonElement box))
                    {
                        double[] values = ReadBoxValues(box);
                        firstBox[objectId] = ConvertBox(values);
                    }

                    if (entry.TryGetProperty("box", out JsonElement vendorBox)
                        && vendorBox.ValueKind == JsonValueKind.Array
                        && vendorBox.GetArrayLength() == 4)
                    {
                        double[] values = ReadBoxValues(vendorBox);
                        double inside = InsideFraction(mask.Pixels, width, height, ConvertBox(values));
                        if (inside * 100.0 < Sam31VideoRleResponseReader.MinBoxAgreementPercent)
                        {
                            violations.Add(new T012BoxViolation(
                                index, objectId, Math.Round(inside * 100.0, 2), mask.Area,
                                Math.Round(values[2] * width, 1)));
                        }

                        if (inside < minimumInside)
                        {
                            minimumInside = inside;
                            minimumInsideFrame = index;
                            minimumInsideObject = objectId;
                        }
                    }
                }
            }

            index++;
        }

        var candidates = objectPixels
            .OrderBy(pair => pair.Key)
            .Select(pair => new Sam31VideoRleDecoder.SegmentationObjectCandidate(
                pair.Key, pair.Value, firstBox.TryGetValue(pair.Key, out var box) ? box : null))
            .ToList();
        int chosen = Sam31VideoRleDecoder.ChooseObjectId(candidates) ?? objectPixels.OrderByDescending(p => p.Value).First().Key;

        var frames = new List<T012FrameSample>(expectedFrames);
        var boxes = new List<T012BoxSample>(expectedFrames);
        var empty = new byte[width * height];

        Process? pipe = null;
        Task<string>? pipeError = null;
        if (maskVideoPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(maskVideoPath))!);
            pipe = StartGrayVideoPipe(maskVideoPath, width, height, 24);
            pipeError = pipe.StandardError.ReadToEndAsync();
        }

        try
        {
            index = 0;
            foreach (JsonElement frame in frameArray.EnumerateArray())
            {
                byte[] pixels = empty;
                bool found = false;
                JsonElement boxElement = default;
                bool hasBox = false;

                foreach (JsonElement entry in frame.EnumerateArray())
                {
                    if (entry.GetProperty("object_id").GetInt32() != chosen)
                        continue;

                    VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(
                        entry.GetProperty("rle").GetString(), chosen, width, height);
                    pixels = mask.Pixels;
                    found = true;
                    if (entry.TryGetProperty("box", out JsonElement box) && box.ValueKind == JsonValueKind.Array)
                    {
                        boxElement = box;
                        hasBox = true;
                    }
                }

                if (pipe is not null)
                    pipe.StandardInput.BaseStream.Write(pixels, 0, pixels.Length);

                var (area, sumX, sumY, x0, y0, x1, y1) = MeasureBox(pixels, width, height);
                double cx = area == 0 ? 0 : sumX / (double)area;
                double cy = area == 0 ? 0 : sumY / (double)area;
                frames.Add(new T012FrameSample(
                    index, area, x0, y0, Math.Max(0, x1 - x0 + 1), Math.Max(0, y1 - y0 + 1), cx, cy,
                    area == 0 ? 0 : (x0 + x1) / 2.0,
                    area == 0 ? 0 : (y0 + y1) / 2.0));

                if (hasBox && area > 0)
                {
                    double[] values = ReadBoxValues(boxElement);
                    double centreX = values[0], centreY = values[1], boxW = values[2], boxH = values[3];
                    double centreOffset = Math.Sqrt(
                        Math.Pow(centreX * width - cx, 2) + Math.Pow(centreY * height - cy, 2)) / diagonal;
                    double cornerX = (values[0] + values[2] / 2.0) * width;
                    double cornerY = (values[1] + values[3] / 2.0) * height;
                    double cornerOffset = Math.Sqrt(
                        Math.Pow(cornerX - cx, 2) + Math.Pow(cornerY - cy, 2)) / diagonal;

                    Sam31VideoRleDecoder.SegmentationBox converted = ConvertBox(values);
                    boxes.Add(new T012BoxSample(
                        HasBox: true,
                        CentreOffset: centreOffset,
                        CornerOffset: cornerOffset,
                        InsideFraction: InsideFraction(pixels, width, height, converted),
                        BoxWidthPx: boxW * width,
                        BoxHeightPx: boxH * height,
                        MaskWidthPx: x1 - x0 + 1,
                        MaskHeightPx: y1 - y0 + 1,
                        CentreInsideMask: centreX * width >= x0 && centreX * width <= x1
                                          && centreY * height >= y0 && centreY * height <= y1));
                }
                else
                {
                    boxes.Add(new T012BoxSample(false, 0, 0, 0, 0, 0, 0, 0, false));
                }

                index++;
            }
        }
        finally
        {
            if (pipe is not null)
            {
                pipe.StandardInput.BaseStream.Dispose();
                string error = pipeError!.GetAwaiter().GetResult();
                pipe.WaitForExit();
                int exitCode = pipe.ExitCode;
                pipe.Dispose();
                if (exitCode != 0)
                    throw new InvalidOperationException($"ffmpeg could not write {maskVideoPath}: {error}");
            }
        }

        return new T012DecodeResult
        {
            Run = run,
            ResponseSha256 = sha,
            InputFrames = expectedFrames,
            ObjectCounts = objectCounts,
            ObjectJumps = objectJumps.OrderBy(pair => pair.Key).Select(pair => new T012ObjectJumps(
                pair.Key, pair.Value.Frames, Math.Round(pair.Value.Centroid, 5), pair.Value.CentroidFrame,
                Math.Round(pair.Value.Bbox, 5), pair.Value.BboxFrame)).ToList(),
            BoxGateViolations = violations,
            BoxGateMinimumInside = minimumInsideFrame < 0 ? 1.0 : minimumInside,
            BoxGateMinimumFrame = minimumInsideFrame,
            BoxGateMinimumObject = minimumInsideObject,
            Frames = frames,
            Boxes = boxes,
            ChosenObjectId = chosen,
            ObjectPixels = objectPixels,
            ObjectFrames = objectFrames,
            ReaderRefusal = refusal,
            Width = width,
            Height = height
        };
    }

    /// <summary>
    /// The report block for one run: frame counts, empty frames, per-frame coverage, centroid path,
    /// drift, the largest centroid jump, and what the vendor's own boxes say about the mask.
    /// </summary>
    public static JsonObject Describe(T012DecodeResult result)
    {
        int width = result.Width, height = result.Height;
        double diagonal = Math.Sqrt((double)width * width + (double)height * height);
        List<T012FrameSample> masked = result.Frames.Where(f => !f.IsEmpty).ToList();

        var coverage = new JsonArray();
        var centroid = new JsonArray();
        foreach (T012FrameSample frame in result.Frames)
        {
            coverage.Add(Math.Round(frame.Area / (double)(width * height), 6));
            centroid.Add(new JsonArray(
                Math.Round(frame.Cx / width, 5),
                Math.Round(frame.Cy / height, 5)));
        }

        // Every distance below is NORMALISED by the frame diagonal, so "0.52" means what a reader
        // expects: 52% of the diagonal. Pixels are the same number times the diagonal.
        double driftFromFirst = 0, driftFromMean = 0, largestJump = 0, largestBoxJump = 0;
        int largestJumpFrame = -1, largestBoxJumpFrame = -1;
        if (masked.Count > 0)
        {
            T012FrameSample first = masked[0];
            double meanX = masked.Average(f => f.Cx), meanY = masked.Average(f => f.Cy);
            driftFromFirst = masked.Max(f => Distance(f.Cx, f.Cy, first.Cx, first.Cy)) / diagonal;
            driftFromMean = masked.Max(f => Distance(f.Cx, f.Cy, meanX, meanY)) / diagonal;

            for (int i = 1; i < result.Frames.Count; i++)
            {
                T012FrameSample previous = result.Frames[i - 1], current = result.Frames[i];
                if (previous.IsEmpty || current.IsEmpty)
                    continue;

                double jump = Distance(previous.Cx, previous.Cy, current.Cx, current.Cy) / diagonal;
                if (jump > largestJump)
                {
                    largestJump = jump;
                    largestJumpFrame = i;
                }

                // The bounding-box centre is what a box-based reading of the mask sees; it is a
                // different statistic from the pixel centroid and jumps when a run wraps a row.
                double boxJump = Math.Sqrt(
                    Math.Pow((current.Bx - previous.Bx) / width, 2) +
                    Math.Pow((current.By - previous.By) / height, 2));
                if (boxJump > largestBoxJump)
                {
                    largestBoxJump = boxJump;
                    largestBoxJumpFrame = i;
                }
            }
        }

        var boxSamples = result.Boxes.Where(b => b.HasBox).ToList();
        var record = new JsonObject
        {
            ["run"] = result.Run.Id,
            ["clip"] = result.Run.ClipId,
            ["prompt"] = result.Run.Prompt,
            ["responsePath"] = RelativeToTrials(result.Run.ResponsePath),
            ["responseSha256"] = result.ResponseSha256,
            ["frameSize"] = width + "x" + height,
            ["frames"] = result.Frames.Count,
            ["inputFrames"] = result.InputFrames,
            ["emptyFrames"] = result.EmptyFrames,
            ["emptyFrameIndexes"] = new JsonArray(result.Frames.Where(f => f.IsEmpty).Select(f => (JsonNode?)JsonValue.Create(f.Index)).ToArray()),
            ["objectSlotsPerFrame"] = new JsonObject(
                result.ObjectCounts.GroupBy(c => c).OrderBy(g => g.Key)
                    .Select(g => new KeyValuePair<string, JsonNode?>(g.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(g.Count())))),
            ["chosenObjectId"] = result.ChosenObjectId,
            ["objectPixels"] = new JsonObject(result.ObjectPixels.OrderBy(p => p.Key)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(p.Value)))),
            ["objectFrames"] = new JsonObject(result.ObjectFrames.OrderBy(p => p.Key)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(p.Value)))),
            ["perObjectJumps"] = new JsonArray(result.ObjectJumps.Select(o => (JsonNode?)new JsonObject
            {
                ["objectId"] = o.ObjectId,
                ["framesWithMask"] = o.FramesWithMask,
                ["centroidJumpNormalised"] = o.CentroidJump,
                ["centroidJumpFrame"] = o.CentroidFrame,
                ["bboxCentreJumpNormalised"] = o.BboxJump,
                ["bboxCentreJumpFrame"] = o.BboxFrame
            }).ToArray()),
            ["coverage"] = new JsonObject
            {
                ["mean"] = Math.Round(result.MeanCoverage, 6),
                ["min"] = Math.Round(result.Frames.Min(f => f.Area / (double)(width * height)), 6),
                ["max"] = Math.Round(result.Frames.Max(f => f.Area / (double)(width * height)), 6),
                ["perFrame"] = coverage
            },
            ["centroid"] = new JsonObject
            {
                ["perFrameNormalised"] = centroid,
                ["driftFromFirstMax"] = Math.Round(driftFromFirst, 5),
                ["driftFromFirstMaxPx"] = Math.Round(driftFromFirst * diagonal, 2),
                ["driftFromMeanMax"] = Math.Round(driftFromMean, 5),
                ["driftFromMeanMaxPx"] = Math.Round(driftFromMean * diagonal, 2),
                ["largestJump"] = Math.Round(largestJump, 5),
                ["largestJumpPx"] = Math.Round(largestJump * diagonal, 2),
                ["largestJumpFrame"] = largestJumpFrame,
                ["largestBboxCentreJump"] = Math.Round(largestBoxJump, 5),
                ["largestBboxCentreJumpPx"] = Math.Round(largestBoxJump * diagonal, 2),
                ["largestBboxCentreJumpFrame"] = largestBoxJumpFrame
            },
            ["vendorBox"] = new JsonObject
            {
                ["framesWithBox"] = boxSamples.Count,
                ["maskInsideBoxMin"] = boxSamples.Count == 0 ? null : Math.Round(boxSamples.Min(b => b.InsideFraction), 5),
                ["maskInsideBoxMedian"] = boxSamples.Count == 0 ? null : Math.Round(Median(boxSamples.Select(b => b.InsideFraction).ToList()), 5),
                ["centreAnchoredOffsetPctDiagonalMedian"] = boxSamples.Count == 0 ? null : Math.Round(100 * Median(boxSamples.Select(b => b.CentreOffset).ToList()), 2),
                ["centreAnchoredOffsetPctDiagonalMax"] = boxSamples.Count == 0 ? null : Math.Round(100 * boxSamples.Max(b => b.CentreOffset), 2),
                ["cornerAnchoredOffsetPctDiagonalMedian"] = boxSamples.Count == 0 ? null : Math.Round(100 * Median(boxSamples.Select(b => b.CornerOffset).ToList()), 2),
                ["cornerAnchoredOffsetPctDiagonalMax"] = boxSamples.Count == 0 ? null : Math.Round(100 * boxSamples.Max(b => b.CornerOffset), 2),
                ["boxWidthOverMaskWidthMedian"] = boxSamples.Count == 0 ? null : Math.Round(Median(boxSamples.Select(b => b.BoxWidthPx / Math.Max(1, b.MaskWidthPx)).ToList()), 3),
                ["boxHeightOverMaskHeightMedian"] = boxSamples.Count == 0 ? null : Math.Round(Median(boxSamples.Select(b => b.BoxHeightPx / Math.Max(1, b.MaskHeightPx)).ToList()), 3),
                ["centreInsideMaskBoxFraction"] = boxSamples.Count == 0 ? null : Math.Round(boxSamples.Count(b => b.CentreInsideMask) / (double)boxSamples.Count, 4),
                ["gateFloorPercent"] = Sam31VideoRleResponseReader.MinBoxAgreementPercent,
                ["gateViolations"] = result.BoxGateViolations.Count,
                ["gateViolatingObjectIds"] = new JsonArray(result.BoxGateViolations
                    .Select(v => v.ObjectId).Distinct().OrderBy(id => id)
                    .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["gateViolationsPerObject"] = new JsonObject(result.BoxGateViolations
                    .GroupBy(v => v.ObjectId).OrderBy(g => g.Key)
                    .Select(g => new KeyValuePair<string, JsonNode?>(g.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(g.Count())))),
                ["gateMinimumInsidePercent"] = Math.Round(result.BoxGateMinimumInside * 100.0, 2),
                ["gateMinimumFrame"] = result.BoxGateMinimumFrame,
                ["gateMinimumObjectId"] = result.BoxGateMinimumObject,
                ["gateViolationList"] = new JsonArray(result.BoxGateViolations.Select(v => (JsonNode?)new JsonObject
                {
                    ["frame"] = v.Frame,
                    ["objectId"] = v.ObjectId,
                    ["insidePercent"] = v.InsidePercent,
                    ["maskAreaPx"] = v.AreaPx,
                    ["boxWidthPx"] = v.BoxWidthPx
                }).ToArray())
            },
            ["shippingReaderRefused"] = result.ReaderRefusal is not null,
            ["shippingReaderRefusal"] = result.ReaderRefusal
        };

        return record;
    }

    /// <summary>
    /// Decodes two captured documents and compares their masks frame by frame — the determinism claim
    /// is about the decoded mask, not the file (two identical files would prove only that the capture
    /// copied one).
    /// </summary>
    public static JsonObject Compare(T012DecodeResult left, T012DecodeResult right)
    {
        int differing = 0;
        using JsonDocument leftDocument = JsonDocument.Parse(File.ReadAllText(left.Run.ResponsePath));
        using JsonDocument rightDocument = JsonDocument.Parse(File.ReadAllText(right.Run.ResponsePath));
        var leftFrames = leftDocument.RootElement.GetProperty("objects").EnumerateArray().ToList();
        var rightFrames = rightDocument.RootElement.GetProperty("objects").EnumerateArray().ToList();
        for (int frame = 0; frame < Math.Min(leftFrames.Count, rightFrames.Count); frame++)
        {
            string leftRle = RleOf(leftFrames[frame], left.ChosenObjectId);
            string rightRle = RleOf(rightFrames[frame], right.ChosenObjectId);
            if (!string.Equals(leftRle, rightRle, StringComparison.Ordinal))
                differing++;
        }

        return new JsonObject
        {
            ["left"] = left.Run.Id,
            ["right"] = right.Run.Id,
            ["responseFilesByteIdentical"] = left.ResponseSha256 == right.ResponseSha256,
            ["framesCompared"] = Math.Min(leftFrames.Count, rightFrames.Count),
            ["framesWithDifferentRle"] = differing,
            ["decodedMasksIdentical"] = differing == 0 && left.ResponseSha256 == right.ResponseSha256
        };
    }

    private static string RleOf(JsonElement frame, int objectId)
    {
        foreach (JsonElement entry in frame.EnumerateArray())
        {
            if (entry.GetProperty("object_id").GetInt32() == objectId)
                return entry.GetProperty("rle").GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));

    private static double[] ReadBoxValues(JsonElement box)
    {
        var values = new double[4];
        for (int i = 0; i < 4; i++)
            values[i] = box[i].GetDouble();
        return values;
    }

    /// <summary>The reader's own conversion: the vendor's <c>[cx, cy, w, h]</c> to a corner-anchored box.</summary>
    private static Sam31VideoRleDecoder.SegmentationBox ConvertBox(double[] values)
    {
        double x = Math.Clamp(values[0] - values[2] / 2.0, 0.0, 1.0);
        double y = Math.Clamp(values[1] - values[3] / 2.0, 0.0, 1.0);
        return new Sam31VideoRleDecoder.SegmentationBox(
            x, y, Math.Min(values[2], 1.0 - x), Math.Min(values[3], 1.0 - y));
    }

    /// <summary>Fraction of the mask's pixels inside the box grown by the reader's own 2 px of slack.</summary>
    private static double InsideFraction(byte[] pixels, int width, int height, Sam31VideoRleDecoder.SegmentationBox box)
    {
        double left = box.X * width - Sam31VideoRleResponseReader.BoxGrowPixels;
        double top = box.Y * height - Sam31VideoRleResponseReader.BoxGrowPixels;
        double right = (box.X + box.W) * width + Sam31VideoRleResponseReader.BoxGrowPixels;
        double bottom = (box.Y + box.H) * height + Sam31VideoRleResponseReader.BoxGrowPixels;

        long inside = 0, area = 0;
        for (int y = 0; y < height; y++)
        {
            if (y < top || y > bottom)
                continue;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x] == 0)
                    continue;
                area++;
                if (x >= left && x <= right)
                    inside++;
            }
        }

        return area == 0 ? 0 : inside / (double)area;
    }

    internal static (int Area, long SumX, long SumY, int X0, int Y0, int X1, int Y1) MeasureBox(byte[] pixels, int width, int height)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, area = 0;
        long sumX = 0, sumY = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x] == 0)
                    continue;
                area++;
                sumX += x;
                sumY += y;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }

        return (area, sumX, sumY, x0, y0, x1, y1);
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        List<double> sorted = values.OrderBy(v => v).ToList();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static string RelativeToTrials(string path)
    {
        string root = T012Artifacts.TrialsRoot;
        return root.Length > 0 && path.StartsWith(root, StringComparison.Ordinal)
            ? path[root.Length..].TrimStart(Path.DirectorySeparatorChar)
            : path;
    }

    private static Process StartGrayVideoPipe(string outputPath, int width, int height, int fps)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string argument in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gray",
                     "-s", width + "x" + height, "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "-",
                     "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "gray", outputPath
                 })
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
    }

    /// <summary>
    /// One object slot's mask, full resolution, as a PNG — the only way to see that a small edge slot's
    /// vendor run wraps a row (the whole-document mask video holds the CHOSEN object only).
    /// </summary>
    public static void WriteObjectMaskPng(T012Run run, int objectId, int frameIndex, int width, int height, string outputPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(run.ResponsePath));
        byte[]? pixels = null;
        foreach (JsonElement entry in document.RootElement.GetProperty("objects")[frameIndex].EnumerateArray())
        {
            if (entry.GetProperty("object_id").GetInt32() == objectId)
                pixels = Sam31VideoRleDecoder.Decode(entry.GetProperty("rle").GetString(), objectId, width, height).Pixels;
        }

        if (pixels is null)
            throw new InvalidOperationException($"{run.Label} frame {frameIndex} has no object {objectId}");

        WriteGrayFramePng(pixels, width, height, outputPath);
    }

    /// <summary>The document's own top-level <c>rle</c> for one frame — the UNION of every object.</summary>
    public static void WriteUnionMaskPng(T012Run run, int frameIndex, int width, int height, string outputPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(run.ResponsePath));
        string rle = document.RootElement.GetProperty("rle")[frameIndex].GetString() ?? string.Empty;
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(rle, 0, width, height);
        WriteGrayFramePng(mask.Pixels, width, height, outputPath);
    }

    private static void WriteGrayFramePng(byte[] pixels, int width, int height, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string argument in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gray",
                     "-s", width + "x" + height, "-r", "1", "-i", "-", "-frames:v", "1", "-pix_fmt", "gray", outputPath
                 })
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(pixels, 0, pixels.Length);
        process.StandardInput.BaseStream.Close();
        string error = stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not write {outputPath}: {error}");
    }

    private static string Sha256OfText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// One effect render through the real compile step, measured on luma with the plan §7 amendment
/// (D-15): the changed set is measured on luma with the 1 px ring at the mask boundary excluded, so a
/// 4:2:0 chroma sample at the composite edge is not counted as the effect leaking.
/// </summary>
internal sealed record T012LumaFrame(
    int Index,
    int MaskPixels,
    double OutsideMean,
    int OutsideP99,
    double InsideMean,
    double InsideSignedMean,
    double OutsideSignedMean,
    int ChangedPixels,
    double ChangedSetIoU,
    double CentroidErrorPx);

internal sealed class T012LumaReport
{
    public required IReadOnlyList<T012LumaFrame> Frames { get; init; }
    public required int RingLumaChangedSamples { get; init; }
    public required int BeyondRingLumaChangedSamples { get; init; }
    public required int OccludedFrames { get; init; }
    public required int OccludedChangedPixelsMax { get; init; }
    public required double OutsideMean { get; init; }
    public required int OutsideP99 { get; init; }
    public required double InsideSignedMean { get; init; }
    public required double OutsideSignedMean { get; init; }
    public required double InsideMean { get; init; }
    public required double ChangedSetIoUP05 { get; init; }
    public required double CentroidErrorMaxPx { get; init; }

    public JsonObject ToJson() => new()
    {
        ["metric"] = "luma (Y plane), changed set at |dY| >= 1, 1 px mask-boundary ring excluded (plan §7 as amended by D-15)",
        ["outsideMean"] = Math.Round(OutsideMean, 5),
        ["outsideP99"] = OutsideP99,
        ["insideMean"] = Math.Round(InsideMean, 3),
        ["insideSignedMean"] = Math.Round(InsideSignedMean, 3),
        ["outsideSignedMean"] = Math.Round(OutsideSignedMean, 3),
        ["changedSetIoUP05"] = Math.Round(ChangedSetIoUP05, 5),
        ["centroidErrorMaxPx"] = Math.Round(CentroidErrorMaxPx, 3),
        ["ringLumaChangedSamples"] = RingLumaChangedSamples,
        ["beyondRingLumaChangedSamples"] = BeyondRingLumaChangedSamples,
        ["occludedFrames"] = OccludedFrames,
        ["occludedChangedPixelsMax"] = OccludedChangedPixelsMax,
        ["perFrame"] = new JsonArray(Frames.Select(f => (JsonNode?)new JsonObject
        {
            ["index"] = f.Index,
            ["maskPixels"] = f.MaskPixels,
            ["outsideMean"] = Math.Round(f.OutsideMean, 5),
            ["outsideP99"] = f.OutsideP99,
            ["insideMean"] = Math.Round(f.InsideMean, 3),
            ["insideSignedMean"] = Math.Round(f.InsideSignedMean, 3),
            ["outsideSignedMean"] = Math.Round(f.OutsideSignedMean, 3),
            ["changedPixels"] = f.ChangedPixels,
            ["changedSetIoU"] = Math.Round(f.ChangedSetIoU, 5),
            ["centroidErrorPx"] = Math.Round(f.CentroidErrorPx, 3)
        }).ToArray())
    };
}

internal static class T012Luma
{
    public static T012LumaReport Measure(string onPath, string offPath, string maskPath, int width, int height, int ringPx = 1)
    {
        int pixels = width * height;
        // on/off hold a whole yuv420p frame (only the Y plane is read); the mask is one byte per pixel.
        var on = new byte[pixels * 3 / 2];
        var off = new byte[pixels * 3 / 2];
        var mask = new byte[pixels];
        var ring = new byte[pixels];
        var frames = new List<T012LumaFrame>();
        var histogram = new int[256];
        int ringChanged = 0, beyondRingChanged = 0;

        using var onStream = RawStream.Open(onPath, "yuv420p", pixels * 3 / 2);
        using var offStream = RawStream.Open(offPath, "yuv420p", pixels * 3 / 2);
        using var maskStream = RawStream.Open(maskPath, "gray", pixels);
        while (true)
        {
            // yuv420p is 1.5 bytes per pixel; the mask is one byte. Only the Y plane is read below.
            bool hasOn = onStream.TryReadFrame(on, pixels * 3 / 2);
            bool hasOff = offStream.TryReadFrame(off, pixels * 3 / 2);
            bool hasMask = maskStream.TryReadFrame(mask, pixels);
            if (!hasOn && !hasOff && !hasMask)
                break;
            if (!hasOn || !hasOff || !hasMask)
                throw new InvalidDataException($"frame counts differ at frame {frames.Count}: on={hasOn}, off={hasOff}, mask={hasMask}");

            Dilate(mask, ring, width, height, ringPx);
            frames.Add(MeasureFrame(frames.Count, on, off, mask, ring, width, histogram, ref ringChanged, ref beyondRingChanged));
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("no frames decoded");

        var masked = frames.Where(f => f.MaskPixels > 0).ToList();
        var occluded = frames.Where(f => f.MaskPixels == 0).ToList();
        return new T012LumaReport
        {
            Frames = frames,
            RingLumaChangedSamples = ringChanged,
            BeyondRingLumaChangedSamples = beyondRingChanged,
            OccludedFrames = occluded.Count,
            OccludedChangedPixelsMax = occluded.Count == 0 ? 0 : occluded.Max(f => f.ChangedPixels),
            OutsideMean = frames.Average(f => f.OutsideMean),
            OutsideP99 = frames.Max(f => f.OutsideP99),
            InsideSignedMean = masked.Count == 0 ? 0 : masked.Average(f => f.InsideSignedMean),
            OutsideSignedMean = frames.Average(f => f.OutsideSignedMean),
            InsideMean = masked.Count == 0 ? 0 : masked.Average(f => f.InsideMean),
            ChangedSetIoUP05 = Percentile(masked.Select(f => f.ChangedSetIoU).ToList(), 0.05),
            CentroidErrorMaxPx = masked.Count == 0 ? 0 : masked.Max(f => f.CentroidErrorPx)
        };
    }

    private static T012LumaFrame MeasureFrame(
        int index, byte[] on, byte[] off, byte[] mask, byte[] ring, int width, int[] histogram,
        ref int ringChanged, ref int beyondRingChanged)
    {
        Array.Clear(histogram, 0, histogram.Length);
        long maskPixels = 0, insideCount = 0, outsideCount = 0, changed = 0, changedInMask = 0;
        double insideSum = 0, outsideSum = 0, maskSumX = 0, maskSumY = 0, changedSumX = 0, changedSumY = 0;
        double insideSignedSum = 0, outsideSignedSum = 0;

        for (int i = 0; i < mask.Length; i++)
        {
            int signed = on[i] - off[i];
            int delta = Math.Abs(signed);
            bool inMask = mask[i] != 0;
            if (inMask)
            {
                maskPixels++;
                insideCount++;
                insideSum += delta;
                insideSignedSum += signed;
                maskSumX += i % width;
                maskSumY += i / width;
            }
            else if (ring[i] == 0)
            {
                outsideCount++;
                outsideSum += delta;
                outsideSignedSum += signed;
                histogram[Math.Min(delta, 255)]++;
                if (delta >= 1)
                    beyondRingChanged++;
            }
            else if (delta >= 1)
            {
                ringChanged++;
            }

            if (delta < 1)
                continue;
            changed++;
            changedSumX += i % width;
            changedSumY += i / width;
            if (inMask)
                changedInMask++;
        }

        double maskX = maskPixels == 0 ? 0 : maskSumX / maskPixels;
        double maskY = maskPixels == 0 ? 0 : maskSumY / maskPixels;
        double changedX = changed == 0 ? 0 : changedSumX / changed;
        double changedY = changed == 0 ? 0 : changedSumY / changed;
        long union = changed + maskPixels - changedInMask;

        return new T012LumaFrame(
            index,
            (int)maskPixels,
            outsideCount == 0 ? 0 : outsideSum / outsideCount,
            Percentile(histogram, outsideCount, 0.99),
            insideCount == 0 ? 0 : insideSum / insideCount,
            insideCount == 0 ? 0 : insideSignedSum / insideCount,
            outsideCount == 0 ? 0 : outsideSignedSum / outsideCount,
            (int)changed,
            union == 0 ? 1.0 : changedInMask / (double)union,
            maskPixels == 0 || changed == 0
                ? 0
                : Math.Sqrt(Math.Pow(changedX - maskX, 2) + Math.Pow(changedY - maskY, 2)));
    }

    private static void Dilate(byte[] mask, byte[] target, int width, int height, int radius)
    {
        Array.Copy(mask, target, mask.Length);
        for (int pass = 0; pass < radius; pass++)
        {
            var next = new byte[mask.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (target[row + x] == 0)
                        continue;
                    next[row + x] = 255;
                    if (x > 0) next[row + x - 1] = 255;
                    if (x + 1 < width) next[row + x + 1] = 255;
                    if (y > 0) next[row - width + x] = 255;
                    if (y + 1 < height) next[row + width + x] = 255;
                }
            }

            Array.Copy(next, target, target.Length);
        }
    }

    private static int Percentile(int[] histogram, long count, double fraction)
    {
        if (count == 0)
            return 0;
        long wanted = (long)Math.Ceiling(fraction * count);
        long running = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running >= wanted)
                return value;
        }

        return histogram.Length - 1;
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
            return 0;
        List<double> sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Floor(fraction * (sorted.Count - 1))];
    }

    /// <summary>Streams decoded frames out of ffmpeg, one at a time, with no re-timing.</summary>
    internal sealed class RawStream : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private readonly int _frameBytes;

        private RawStream(Process process, int frameBytes)
        {
            _process = process;
            _frameBytes = frameBytes;
            _stdout = process.StandardOutput.BaseStream;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static RawStream Open(string path, string pixelFormat, int frameBytes, string? videoFilter = null)
        {
            var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            var arguments = new List<string> { "-v", "error", "-i", path, "-fps_mode", "passthrough" };
            if (!string.IsNullOrEmpty(videoFilter))
            {
                arguments.Add("-vf");
                arguments.Add(videoFilter);
            }

            arguments.AddRange(["-f", "rawvideo", "-pix_fmt", pixelFormat, "-"]);
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            return new RawStream(Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start"), frameBytes);
        }

        public bool TryReadFrame(byte[] frame) => TryReadFrame(frame, _frameBytes);

        public bool TryReadFrame(byte[] frame, int bytes)
        {
            int first = _stdout.ReadByte();
            if (first < 0)
                return false;
            frame[0] = (byte)first;
            int filled = 1;
            while (filled < bytes)
            {
                int read = _stdout.Read(frame, filled, bytes - filled);
                if (read == 0)
                    throw new InvalidDataException($"a partial frame ({filled} of {bytes} bytes)");
                filled += read;
            }

            return true;
        }

        public void Dispose()
        {
            _stdout.Dispose();
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the check and the kill.
                }
            }

            _process.WaitForExit();
            _ = _stderr.GetAwaiter().GetResult();
            _process.Dispose();
        }
    }
}

/// <summary>
/// T-012's live-model evidence, offline: we captured five real <c>fal-ai/sam-3-1/video-rle</c>
/// responses and two MiniMax clips on 2026-10-18; nothing here calls a vendor. The masks are decoded
/// by the shipping decoder, written as the FFV1 <c>gray</c> video the artifact contract names, and
/// rendered through the REAL <see cref="VideoCompileStepExecutor"/> with real ffmpeg.
///
/// <para>
/// Opt-in: <c>REELBOLT_T012_TRIALS=&lt;initiative&gt;/trials</c> (and optionally
/// <c>REELBOLT_T012_ARTIFACTS=&lt;initiative&gt;/reports/T-012</c>). Without it the tests skip, so the
/// normal suite gate is unaffected.
/// </para>
/// </summary>
public class T012LiveEvidenceTests : IDisposable
{
    private const int Fps = 24;
    private const string MaskStorageKey = "projects/p/agentFiles/segmentation/g0.mkv";
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceStorageKey = "projects/p/userFiles/trial-clip.mp4";
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITestOutputHelper _output;
    private readonly string _scratch = T012Artifacts.Scratch("run");

    public T012LiveEvidenceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    // ---------------------------------------------------------------------------------------
    // 1. Decode: five captured documents, at each clip's OWN frame size.
    // ---------------------------------------------------------------------------------------

    [T012TrialFact]
    public void The_five_captured_responses_decode_at_the_clips_own_frame_size()
    {
        (int width, int height, int frames) = ClipShape(T012Runs.D1.ClipPath);
        (int faceWidth, int faceHeight, int faceFrames) = ClipShape(T012Runs.F1.ClipPath);
        _output.WriteLine($"DASH {width}x{height} {frames} frames; FACE {faceWidth}x{faceHeight} {faceFrames} frames");

        (faceWidth, faceHeight, faceFrames).Should().Be((width, height, frames),
            because: "both trial clips were delivered at the same shape; the masks are decoded at each clip's own size");

        var results = new Dictionary<string, T012DecodeResult>();
        var block = new JsonArray();
        foreach (T012Run run in T012Runs.All)
        {
            T012DecodeResult result = T012Decode.Read(run, width, height, frames);
            results[run.Id] = result;
            JsonObject described = T012Decode.Describe(result);
            block.Add(described);
            _output.WriteLine(
                $"{run.Label} prompt \"{run.Prompt}\": {result.Frames.Count} frames, {result.EmptyFrames} empty, " +
                $"chosen g{result.ChosenObjectId}, coverage mean {result.MeanCoverage:0.####}, " +
                $"largest centroid jump {described["centroid"]!["largestJump"]} " +
                $"({described["centroid"]!["largestJumpPx"]} px) at frame {described["centroid"]!["largestJumpFrame"]}, " +
                $"largest bbox-centre jump {described["centroid"]!["largestBboxCentreJump"]} " +
                $"at frame {described["centroid"]!["largestBboxCentreJumpFrame"]}, " +
                $"box-gate violations {result.BoxGateViolations.Count} (worst " +
                $"{result.BoxGateMinimumInside * 100:0.##}% frame {result.BoxGateMinimumFrame} g{result.BoxGateMinimumObject}), " +
                $"refused={result.ReaderRefusal is not null}");
        }

        // The clip is the frame count the reader was given, one entry per input frame.
        foreach (T012DecodeResult result in results.Values)
            result.InputFrames.Should().Be(frames);

        // D1 = "speedometer", D1b = the same request re-run: byte-identical files, and — the claim that
        // matters — identical decoded masks frame for frame.
        JsonObject determinism = T012Decode.Compare(results["D1"], results["D1b"]);
        determinism["decodedMasksIdentical"]!.GetValue<bool>().Should().BeTrue(
            because: "the determinism re-run must decode to the same mask, " + determinism.ToJsonString());
        _output.WriteLine($"D1 vs D1b: {determinism.ToJsonString()}");

        // The dash runs are documents the shipping reader ACCEPTS, box gate included.
        foreach (string id in new[] { "D1", "D2", "D1b" })
        {
            results[id].ReaderRefusal.Should().BeNull(because: $"{id} is a document the shipping reader accepts");
            results[id].BoxGateViolations.Should().BeEmpty(
                because: $"{id}'s masks must sit inside the vendor's own boxes (measured minimum " +
                         $"{results[id].BoxGateMinimumInside * 100:0.##}%)");
        }

        // FINDING (reported, never patched): F2 ("person") is a document the shipping reader REFUSES —
        // the vendor returned 6..10 object slots per frame for a request whose max_num_objects was 4.
        results["F2"].ReaderRefusal.Should().NotBeNull(
            because: "F2 returned 6..10 objects per frame for a request that asked for at most 4");
        results["F2"].ReaderRefusal!.Should().StartWith(VideoSegmentationBadResponseException.FailureCode)
            .And.Contain("objects");

        // FINDING (reported, never patched): F1 ("face") is ALSO refused by the shipping reader, on the
        // box gate, for a document whose face mask is clean. The offending slots are the small objects
        // near the right frame edge, whose vendor run wraps onto the next row (legal: D-11), so a few
        // percent of their pixels land at x = 0 and fall outside their own 21 px box. Measured through
        // the shipping decoder: every violating object-frame belongs to those edge slots, the face
        // itself is 100% inside its box on all 141 frames.
        results["F1"].ReaderRefusal.Should().NotBeNull(
            because: "the captured face response must reproduce the shipping reader's refusal, not be waved through");
        results["F1"].ReaderRefusal!.Should().StartWith(VideoSegmentationBadResponseException.FailureCode)
            .And.Contain("inside the vendor's own box");
        results["F1"].BoxGateViolations.Should().NotBeEmpty();
        results["F1"].BoxGateViolations.Select(v => v.ObjectId).Distinct().Should().NotContain(results["F1"].ChosenObjectId,
            because: "the refusal must not be caused by the tracked face mask itself");
        results["F1"].BoxGateMinimumInside.Should().BeLessThan(Sam31VideoRleResponseReader.MinBoxAgreementPercent / 100.0);
        _output.WriteLine("F1 refusal: " + results["F1"].ReaderRefusal);
        _output.WriteLine("F2 refusal: " + results["F2"].ReaderRefusal);

        var document = new JsonObject
        {
            ["frameSize"] = width + "x" + height,
            ["clipFrames"] = frames,
            ["fps"] = Fps,
            ["decoder"] = "ReelBolt.Shared.Inference.Sam31VideoRleDecoder + Sam31VideoRleResponseReader (shipping)",
            ["provenance"] = new JsonObject
            {
                ["capturedBy"] = "the maintainers, through the fal connector",
                ["capturedOn"] = "2026-10-18",
                ["endpoint"] = T012Decode.Model,
                ["replayed"] = "offline; no shipping code called a vendor in this run",
                ["cost"] = "rate-card estimate $0.09 per run (9 x 16-frame units), not an invoice"
            },
            ["runs"] = block,
            ["determinism"] = determinism
        };
        File.WriteAllText(Path.Combine(T012Artifacts.Root, "decode.json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The masks the renders consume are written once, at the clip's own size.
        T012Decode.EnsureMask(T012Runs.D1, width, height, frames);
        T012Decode.EnsureMask(T012Runs.F1, width, height, frames);

        // F-1 made visible: the small right-edge slot g2 at the frame where its run first wraps a row
        // (121) and at the frame the reader refuses (125), plus the document's own union for the same
        // frame. The mask video holds only the CHOSEN object, so these are written per slot.
        string framesFolder = T012Artifacts.Folder("frames");
        T012Decode.WriteObjectMaskPng(T012Runs.F1, 2, 121, width, height, Path.Combine(framesFolder, "F1-slot-g2-mask-frame-121.png"));
        T012Decode.WriteObjectMaskPng(T012Runs.F1, 2, 125, width, height, Path.Combine(framesFolder, "F1-slot-g2-mask-frame-125-refused.png"));
        T012Decode.WriteUnionMaskPng(T012Runs.F1, 125, width, height, Path.Combine(framesFolder, "F1-union-mask-frame-125.png"));
    }

    // ---------------------------------------------------------------------------------------
    // 2. Render the demos through the real compile path and measure each one.
    // ---------------------------------------------------------------------------------------

    [T012TrialFact]
    public async Task The_dash_and_face_demos_render_through_the_compile_step_and_are_measured()
    {
        (int width, int height, int frames) = ClipShape(T012Runs.D1.ClipPath);
        double duration = frames / (double)Fps;
        string maskDash = T012Decode.EnsureMask(T012Runs.D1, width, height, frames);
        string maskFace = T012Decode.EnsureMask(T012Runs.F1, width, height, frames);

        T012DecodeResult dash = T012Decode.Read(T012Runs.D1, width, height, frames);
        T012DecodeResult face = T012Decode.Read(T012Runs.F1, width, height, frames);

        var renders = new JsonArray();

        renders.Add(await RenderOneAsync(
            "dash-speedometer-blur", "DASH", T012Runs.D1.ClipPath, maskDash, dash,
            new VideoRegionEffect("speedometer", RegionEffect.Blur), width, height, frames, duration));
        renders.Add(await RenderOneAsync(
            "dash-speedometer-highlight", "DASH", T012Runs.D1.ClipPath, maskDash, dash,
            new VideoRegionEffect("speedometer", RegionEffect.Highlight), width, height, frames, duration));
        renders.Add(await RenderOneAsync(
            "face-face-blur", "FACE", T012Runs.F1.ClipPath, maskFace, face,
            new VideoRegionEffect("face", RegionEffect.Blur), width, height, frames, duration));

        File.WriteAllText(Path.Combine(T012Artifacts.Root, "renders.json"),
            renders.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<JsonObject> RenderOneAsync(
        string name, string clipId, string clipPath, string maskPath, T012DecodeResult decoded,
        VideoRegionEffect effect, int width, int height, int frames, double duration)
    {
        string scratch = Path.Combine(_scratch, name);
        Directory.CreateDirectory(scratch);

        string maskHash = Sha256OfFile(maskPath);
        VideoSegmentationTrack track = new(
            Id: "g0", SourceIndex: 0, Prompt: decoded.Run.Prompt, StartSec: 0, EndSec: duration,
            FrameCount: frames, Fps: Fps, Coverage: decoded.MeanCoverage, Confidence: 0.9,
            MaskStorageKey: MaskStorageKey, MaskContentHash: maskHash, Model: T012Decode.Model,
            ProviderFingerprint: "fal/" + T012Decode.Model + "/captured-2026-10-18",
            BoxKeyframes: null,
            Facts: new VideoSegmentationFacts(decoded.EmptyFrames, 0, 0.5));
        VideoAnalysisArtifact artifact = ArtifactFor(clipPath, duration, track, width, height);

        ExecutorRun off = await CompileAsync(
            artifact, Config(), clipPath, maskPath, Path.Combine(scratch, name + "-off.mp4"));
        off.Result.Status.Should().Be(StepStatus.Completed, because: off.Result.ErrorDetails ?? off.FailureCode);
        File.Exists(off.OutputPath!).Should().BeTrue();

        ExecutorRun on = await CompileAsync(
            artifact, Config(effect), clipPath, maskPath, Path.Combine(scratch, name + "-on.mp4"));
        on.Result.Status.Should().Be(StepStatus.Completed, because: on.Result.ErrorDetails ?? on.FailureCode);
        File.Exists(on.OutputPath!).Should().BeTrue();

        // The EDL carries the effect by name; the prompt is DATA and never reaches the argv.
        on.Edl.Should().NotBeNull();
        on.Edl!["regionEffects"]!["applied"]!.GetValue<bool>().Should().BeTrue();
        on.Edl!["regionEffects"]!["entries"]!.AsArray().Should().ContainSingle();
        on.FilterGraph.Should().Contain("alphamerge");
        on.FilterGraph.Should().NotContain("maskedmerge");
        on.FilterGraph.Should().NotContain(decoded.Run.Prompt);

        string losslessOn = ToLossless(on.OutputPath!, Path.Combine(scratch, name + "-on.mkv"));
        string losslessOff = ToLossless(off.OutputPath!, Path.Combine(scratch, name + "-off.mkv"));
        PromptedRegionBarReport report = PromptedRegionBarHarness.Measure(losslessOn, losslessOff, maskPath);
        T012LumaReport luma = T012Luma.Measure(losslessOn, losslessOff, maskPath, width, height);

        PromptedRegionBarHarness.WriteContactSheet(
            report, losslessOn, losslessOff, maskPath,
            Path.Combine(T012Artifacts.Folder("sheets"), "sheet-" + name + ".png"), samples: 4);
        T012Compile.DeliveryClip(losslessOn, Path.Combine(T012Artifacts.Folder("clips"), name + "-effect-on.mp4"), 3.0);
        T012Compile.DeliveryClip(losslessOff, Path.Combine(T012Artifacts.Folder("clips"), name + "-effect-off.mp4"), 3.0);
        File.WriteAllText(Path.Combine(T012Artifacts.Folder("frames"), name + ".json"), report.ToJson());
        File.WriteAllText(Path.Combine(T012Artifacts.Folder("frames"), name + "-luma.json"),
            luma.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // F1's centroid "jump": the contact sheet a reader needs BEFORE the claim that face tracking is
        // clean. Three numbers are involved and they are NOT the same claim:
        //   * the FACE mask's pixel centroid moves at most ~0.01 of the diagonal frame to frame;
        //   * the mask's BOUNDING-BOX centre jumps ~0.48 when the vendor's masked run for a small
        //     right-edge object wraps a row (legal, D-11), which is the "~0.5 normalised jump" figure;
        //   * the shipping reader refuses the whole document on that same wrapping, via its box gate.
        JsonObject? jump = null;
        if (decoded.Run.Id == "F1")
        {
            double diagonal = Math.Sqrt((double)width * width + (double)height * height);
            T012Sheet.FindLargestJumps(decoded.Frames, width, height, out int centroidFrame, out double centroidJump,
                out _, out _);

            // The ~0.5 jump belongs to a SLOT, not to the tracked face: the small right-edge object
            // g2, whose vendor run wraps a row and whose bounding box therefore spans the frame from
            // frame 121 on. The sheet is built around THAT frame, because that is the claim to check.
            T012ObjectJumps slot = decoded.ObjectJumps.OrderByDescending(j => j.BboxJump).First();
            int boxFrame = slot.BboxFrame;
            double boxJump = slot.BboxJump;
            centroidFrame.Should().BeGreaterThan(0, because: "the tracked mask's largest centroid jump must be locatable");
            boxFrame.Should().BeGreaterThan(0, because: "the ~0.5 normalised bbox-centre jump must be locatable");
            slot.ObjectId.Should().NotBe(decoded.ChosenObjectId,
                because: "the ~0.5 jump must be shown to come from an edge slot, not from the tracked face");

            var wanted = Enumerable.Range(Math.Max(0, boxFrame - 3), 7).Where(i => i < frames).ToList();
            string sheet = Path.Combine(T012Artifacts.Folder("sheets"), "sheet-F1-face-bbox-jump.png");
            T012Sheet.WriteAroundFrames(losslessOff, losslessOn, maskPath, sheet, wanted, width, height);

            // The discontinuities a per-frame object-selection heuristic can jump across, plus the frame
            // the shipping reader refuses: new vendor slots appear at 29 and 68, the wrap starts at 121
            // and the reader's box gate trips at 125.
            var discontinuities = new List<int> { 28, 29, 30, 67, 68, 69, 120, 121, 124, 125, 126 };
            string shapeSheet = Path.Combine(T012Artifacts.Folder("sheets"), "sheet-F1-tracking-shape-changes.png");
            T012Sheet.WriteAroundFrames(losslessOff, losslessOn, maskPath, shapeSheet,
                discontinuities.Where(i => i < frames).ToList(), width, height);

            int violationFrame = decoded.BoxGateViolations.Count == 0 ? boxFrame : decoded.BoxGateViolations[0].Frame;
            string maskPng = Path.Combine(T012Artifacts.Folder("frames"), "F1-mask-frame-" + boxFrame + ".png");
            T012Sheet.WriteMaskFrame(maskPath, maskPng, boxFrame);
            string refusedMaskPng = Path.Combine(T012Artifacts.Folder("frames"), "F1-mask-frame-" + violationFrame + "-refused.png");
            T012Sheet.WriteMaskFrame(maskPath, refusedMaskPng, violationFrame);
            string clipPng = Path.Combine(T012Artifacts.Folder("frames"), "F1-clip-frame-" + boxFrame + ".png");
            T012Sheet.WriteVideoFrame(clipPath, clipPng, boxFrame);

            T012FrameSample from = decoded.Frames[boxFrame - 1], to = decoded.Frames[boxFrame];
            jump = new JsonObject
            {
                ["metric"] = "mask bounding-box centre, normalised by the frame diagonal — the reading that produces ~0.5",
                ["bboxJumpObjectId"] = slot.ObjectId,
                ["bboxJumpSlots"] = new JsonArray(decoded.ObjectJumps
                    .OrderByDescending(j => j.BboxJump)
                    .Select(j => (JsonNode?)new JsonObject
                    {
                        ["objectId"] = j.ObjectId,
                        ["bboxCentreJumpNormalised"] = j.BboxJump,
                        ["bboxCentreJumpFrame"] = j.BboxFrame,
                        ["centroidJumpNormalised"] = j.CentroidJump,
                        ["centroidJumpFrame"] = j.CentroidFrame
                    }).ToArray()),
                ["bboxJumpFrame"] = boxFrame,
                ["bboxJumpNormalised"] = Math.Round(boxJump, 5),
                ["bboxJumpPx"] = Math.Round(boxJump * diagonal, 2),
                ["bboxFromPx"] = new JsonArray(Math.Round(from.Bx, 1), Math.Round(from.By, 1)),
                ["bboxToPx"] = new JsonArray(Math.Round(to.Bx, 1), Math.Round(to.By, 1)),
                ["pixelCentroidJumpFrame"] = centroidFrame,
                ["pixelCentroidJumpNormalised"] = Math.Round(centroidJump, 5),
                ["pixelCentroidJumpPx"] = Math.Round(centroidJump * diagonal, 2),
                ["contactSheet"] = "sheets/" + Path.GetFileName(sheet),
                ["shapeChangesSheet"] = "sheets/" + Path.GetFileName(shapeSheet),
                ["framesInBboxSheet"] = new JsonArray(wanted.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["framesInShapeSheet"] = new JsonArray(discontinuities.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["fullResMaskPng"] = "frames/" + Path.GetFileName(maskPng),
                ["fullResMaskPngAtReaderRefusal"] = "frames/" + Path.GetFileName(refusedMaskPng),
                ["fullResClipPng"] = "frames/" + Path.GetFileName(clipPng),
                ["readerRefusalFrame"] = violationFrame,
                ["boxGateViolations"] = decoded.BoxGateViolations.Count
            };
        }

        JsonObject row = new()
        {
            ["render"] = name,
            ["clip"] = clipId,
            ["effect"] = effect.Effect.ToString(),
            ["target"] = effect.Target,
            ["track"] = new JsonObject
            {
                ["id"] = track.Id,
                ["prompt"] = track.Prompt,
                ["frames"] = track.FrameCount,
                ["fps"] = track.Fps,
                ["maskSha256"] = track.MaskContentHash,
                ["emptyFrames"] = decoded.EmptyFrames,
                ["coverage"] = Math.Round(track.Coverage, 5)
            },
            ["sourceDocument"] = new JsonObject
            {
                ["run"] = decoded.Run.Id,
                ["responseSha256"] = decoded.ResponseSha256,
                ["shippingReaderAccepted"] = decoded.ReaderRefusal is null,
                ["shippingReaderRefusal"] = decoded.ReaderRefusal,
                ["note"] = decoded.ReaderRefusal is null
                    ? "the shipping reader accepts this capture, so this render is the product's own path end to end"
                    : "the shipping reader REFUSES this capture (F-1 in the report), so this render demonstrates the " +
                      "compile half on the captured masks; the analyze step would fail closed before it"
            },
            ["edl"] = on.Edl,
            ["filterGraph"] = on.FilterGraph,
            ["bar"] = new JsonObject
            {
                ["pass"] = report.Pass,
                ["failedChecks"] = new JsonArray(report.Failures.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["outsideMean"] = Math.Round(report.Aggregate.OutsideMean, 5),
                ["outsideP99"] = report.Aggregate.OutsideP99,
                ["insideMean"] = Math.Round(report.Aggregate.InsideMean, 3),
                ["changedSetIoUP05"] = Math.Round(report.Aggregate.ChangedSetIoUP05, 5),
                ["centroidErrorMaxPx"] = Math.Round(report.Aggregate.CentroidErrorMaxPx, 3),
                ["areaErrorP95"] = Math.Round(report.Aggregate.AreaErrorP95, 5),
                ["occludedFrames"] = report.Aggregate.OccludedFrames,
                ["occludedChangedPixelsMax"] = report.Aggregate.OccludedChangedPixelsMax,
                ["maskedFrames"] = report.Aggregate.MaskedFrames,
                ["maskCoverageMean"] = Math.Round(report.Aggregate.MaskCoverageMean, 5),
                ["instrument"] = "PromptedRegionBarHarness.Measure (RGB max-channel |delta| >= 1, 1 px ring excluded outside)"
            },
            ["luma"] = luma.ToJson(),
            ["contactSheet"] = "sheets/sheet-" + name + ".png",
            ["clips"] = new JsonArray(
                "clips/" + name + "-effect-off.mp4",
                "clips/" + name + "-effect-on.mp4"),
            ["f1CentroidJump"] = jump
        };

        _output.WriteLine(
            $"{name}: outsideMean {report.Aggregate.OutsideMean:0.#####} p99 {report.Aggregate.OutsideP99}, " +
            $"insideMean {report.Aggregate.InsideMean:0.##}, IoU p05 {report.Aggregate.ChangedSetIoUP05:0.####}, " +
            $"centroid max {report.Aggregate.CentroidErrorMaxPx:0.##} px, occluded {report.Aggregate.OccludedFrames}/{report.Aggregate.OccludedChangedPixelsMax}, " +
            $"luma outside {luma.OutsideMean:0.#####} inside {luma.InsideMean:0.##} ringChanged {luma.RingLumaChangedSamples} beyondRing {luma.BeyondRingLumaChangedSamples}, " +
            $"failed [{string.Join(", ", report.Failures)}]");
        return row;
    }

    // ---------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------

    private sealed record ExecutorRun(StepExecutionResult Result, string? FailureCode, JsonObject? Edl, string? FilterGraph,
        string? OutputPath);

    private static VideoCompileStepConfig Config(params VideoRegionEffect[] effects) =>
        new(Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1,
            Mode: VideoCompileMode.Reencode,
            KeepWholeSources: true,
            PrePaddingMs: 0,
            PostPaddingMs: 0,
            // Crf 0 is x264 lossless: the bar is a DIFFERENCE instrument, so a delivery quantiser that
            // reacts to the effect would be measured as "the effect changed the whole frame".
            Crf: 0,
            RegionEffects: effects.Length == 0 ? null : effects);

    private static VideoAnalysisArtifact ArtifactFor(
        string clipPath, double durationSec, VideoSegmentationTrack track, int width, int height)
    {
        var media = new VideoAnalysisMedia(durationSec, Fps, 1, width, height);
        return new VideoAnalysisArtifact(
            Version: 2,
            Media: media,
            Shots: [new VideoAnalysisShot("s0", 0.0, durationSec, SourceIndex: 0)],
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: ["s0"],
            Provenance: new VideoAnalysisProvenance(
                VideoTranscriptionMode.Off, false, false,
                SegmentationPrompts: [track.Prompt]),
            Sources: [new VideoAnalysisSourceInfo(0, SourceStorageKey, media)],
            SegmentationTracks: [track]);
    }

    private async Task<ExecutorRun> CompileAsync(
        VideoAnalysisArtifact artifact, VideoCompileStepConfig config,
        string clipPath, string maskPath, string outputPath)
    {
        string artifactJson = JsonSerializer.Serialize(artifact, ConfigOptions);
        JsonObject? edl = null;

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
            {
                if (key == AnalysisKey)
                    File.WriteAllText(destination, artifactJson);
                else if (key.Contains("/segmentation/", StringComparison.Ordinal))
                    File.Copy(maskPath, destination, overwrite: true);
                else if (key.EndsWith(".mp4", StringComparison.Ordinal))
                    File.Copy(clipPath, destination, overwrite: true);
                else
                    File.WriteAllBytes(destination, [0x00, 0x01]);
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (path.EndsWith("edl.json", StringComparison.Ordinal))
                    edl = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-edl.json");
        workspace
            .Setup(w => w.UploadBinaryFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(),
                It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string localPath, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
            {
                File.Copy(localPath, outputPath, overwrite: true);
                return new ProjectWorkspaceFile(Guid.NewGuid(), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UnixEpoch, null);
            });
        workspace.SetupRegisteredRenderPublish(onPublishedLocalPath: p => File.Copy(p, outputPath, overwrite: true));

        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
        VideoCompileStepExecutor.ResetXfadeAvailabilityCacheForTests();

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(_scratch, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 120,
            CompileTimeoutSeconds = 600
        });
        var recording = new RecordingToolRunner(new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance));
        var probe = new FfprobeMediaProbe(recording, options, NullLogger<FfprobeMediaProbe>.Instance);

        var executor = new VideoCompileStepExecutor(
            recording, probe, workspace.Object, new Mock<IServiceScopeFactory>().Object, options,
            NullLogger<VideoCompileStepExecutor>.Instance);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions)
        };

        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "{}",
            StepOutputHistory = [new(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "t012",
            CancellationToken = CancellationToken.None
        });

        string? failureCode = null;
        if (result.Status != StepStatus.Completed)
            failureCode = JsonNode.Parse(result.Output)?["error"]?["code"]?.GetValue<string>();

        return new ExecutorRun(result, failureCode, edl, recording.FilterGraph,
            File.Exists(outputPath) ? outputPath : null);
    }

    private string ToLossless(string encodedPath, string path)
    {
        T012Compile.Run("ffmpeg", [
            "-v", "error", "-y", "-i", encodedPath, "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "yuv420p", path
        ]);
        return path;
    }

    private static (int Width, int Height, int Frames) ClipShape(string path)
    {
        var (width, height, fpsNum, fpsDen, _) = PromptedRegionBarHarness.Probe(path);
        var info = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[]
                 {
                     "-v", "error", "-count_frames", "-select_streams", "v:0",
                     "-show_entries", "stream=nb_read_frames,r_frame_rate", "-of", "default=nw=1", path
                 })
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe did not start");
        string text = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, because: error);
        Dictionary<string, string> values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim());
        int frames = int.Parse(values["nb_read_frames"], CultureInfo.InvariantCulture);
        int fpsNumerator = int.Parse(values["r_frame_rate"].Split('/')[0], CultureInfo.InvariantCulture);
        int fpsDenominator = int.Parse(values["r_frame_rate"].Split('/')[1], CultureInfo.InvariantCulture);
        (fpsNumerator / (double)fpsDenominator).Should().Be(Fps, because: $"{path} is a 24 fps trial clip");
        (fpsNum / (double)fpsDen).Should().Be(Fps);
        return (width, height, frames);
    }

    private static string Sha256OfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The real runner, observed: records the filtergraph and that an encode happened.</summary>
    private sealed class RecordingToolRunner : IVideoToolRunner
    {
        private readonly IVideoToolRunner _inner;

        public RecordingToolRunner(IVideoToolRunner inner) => _inner = inner;

        public string? FilterGraph { get; private set; }

        public bool Encoded { get; private set; }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            Record(args, () => _inner.RunFfmpegAsync(args, timeout, ct));

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            Record(args, () => _inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine));

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            _inner.RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            Record(args, () => _inner.RunFfmpegAsync(workspace, args, timeout, ct));

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine) =>
            Record(args, () => _inner.RunFfmpegAsync(workspace, args, timeout, ct, onStdOutLine));

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            Record(args, () => _inner.RunFfmpegAsync(args, timeout, ct, priority));

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine,
            VideoToolPriority priority) =>
            Record(args, () => _inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine, priority));

        public Task<VideoToolResult> RunFfprobeAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            _inner.RunFfprobeAsync(args, timeout, ct, priority);

        private Task<VideoToolResult> Record(IReadOnlyList<string> args, Func<Task<VideoToolResult>> run)
        {
            if (args.Contains("-filters") || !args.Contains("-map"))
                return run();

            Encoded = true;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "-filter_complex_script")
                    FilterGraph = File.ReadAllText(args[i + 1]);
                else if (args[i] == "-filter_complex")
                    FilterGraph ??= args[i + 1];
            }

            return run();
        }
    }
}

/// <summary>Runs ffmpeg for the evidence renders and encodes the short demo clips.</summary>
internal static class T012Compile
{
    public static void DeliveryClip(string losslessPath, string outputPath, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        Run("ffmpeg", [
            "-v", "error", "-y", "-i", losslessPath, "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", outputPath
        ]);
    }

    public static void Run(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {fileName}");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {error}");
    }
}

/// <summary>
/// The F1 contact sheet: seven consecutive frames around the largest centroid jump, four columns —
/// effect off, effect on, mask, difference x8 — so a reader can SEE whether the effect stayed on the
/// face across the jump instead of trusting the aggregate.
/// </summary>
internal static class T012Sheet
{
    public static void WriteAroundFrames(
        string offPath, string onPath, string maskPath, string outputPath, IReadOnlyList<int> frameIndexes,
        int width, int height)
    {
        List<int> wanted = frameIndexes.ToList();
        int cellWidth = (width / 2) & ~1, cellHeight = (height / 2) & ~1;
        int pixels = cellWidth * cellHeight;
        string scale = $"scale={cellWidth}:{cellHeight}:flags=lanczos";

        var off = new byte[frameIndexes.Count][];
        var on = new byte[frameIndexes.Count][];
        var mask = new byte[frameIndexes.Count][];
        var offFrame = new byte[pixels * 3];
        var onFrame = new byte[pixels * 3];
        // The mask is opened as rgb24 too (ffmpeg replicates grey), so it needs three bytes a pixel.
        var maskFrame = new byte[pixels * 3];
        int index = 0;

        using (var offStream = T012Luma.RawStream.Open(offPath, "rgb24", pixels * 3, scale))
        using (var onStream = T012Luma.RawStream.Open(onPath, "rgb24", pixels * 3, scale))
        using (var maskStream = T012Luma.RawStream.Open(maskPath, "rgb24", pixels * 3, scale))
        {
            while (offStream.TryReadFrame(offFrame) && onStream.TryReadFrame(onFrame) && maskStream.TryReadFrame(maskFrame))
            {
                int slot = wanted.IndexOf(index);
                if (slot >= 0)
                {
                    off[slot] = (byte[])offFrame.Clone();
                    on[slot] = (byte[])onFrame.Clone();
                    mask[slot] = (byte[])maskFrame.Clone();
                }

                index++;
            }
        }

        const int gap = 2;
        int columns = 4;
        int sheetWidth = (columns * cellWidth) + ((columns - 1) * gap);
        int sheetHeight = (frameIndexes.Count * cellHeight) + ((frameIndexes.Count - 1) * gap);
        var sheet = new byte[sheetWidth * sheetHeight * 3];
        for (int i = 0; i < sheet.Length; i += 3)
        {
            sheet[i] = 40;
            sheet[i + 1] = 40;
            sheet[i + 2] = 40;
        }

        for (int row = 0; row < frameIndexes.Count; row++)
        {
            if (off[row] is null || on[row] is null || mask[row] is null)
                throw new InvalidOperationException($"frame {frameIndexes[row]} could not be read for the contact sheet");
            int top = row * (cellHeight + gap);
            for (int y = 0; y < cellHeight; y++)
            {
                for (int x = 0; x < cellWidth; x++)
                {
                    int source = ((y * cellWidth) + x) * 3;
                    Blit(sheet, sheetWidth, x, top + y, off[row], source);
                    Blit(sheet, sheetWidth, cellWidth + gap + x, top + y, on[row], source);
                    Blit(sheet, sheetWidth, (2 * (cellWidth + gap)) + x, top + y, mask[row], source);
                    int delta = Math.Max(
                        Math.Abs(on[row][source] - off[row][source]),
                        Math.Max(Math.Abs(on[row][source + 1] - off[row][source + 1]),
                                 Math.Abs(on[row][source + 2] - off[row][source + 2])));
                    byte amplified = (byte)Math.Min(255, delta * 8);
                    int destination = (((top + y) * sheetWidth) + (3 * (cellWidth + gap)) + x) * 3;
                    sheet[destination] = amplified;
                    sheet[destination + 1] = amplified;
                    sheet[destination + 2] = amplified;
                }
            }

            // The frame number, burned into the first column so the row is identifiable.
            string label = "n=" + frameIndexes[row].ToString(CultureInfo.InvariantCulture);
            int labelX = 8, labelY = top + 8;
            for (int i = 0; i < label.Length; i++)
                DrawGlyph(sheet, sheetWidth, labelX + (i * 12), labelY, label[i]);
        }

        string labels = string.Join(",", new[] { "effect off", "effect on", "mask", "difference x8" }
            .Select((label, column) => string.Create(CultureInfo.InvariantCulture,
                $"drawtext=text='{label}':x={(column * (cellWidth + gap)) + 8}:y=8:fontsize=20:fontcolor=white:box=1:boxcolor=black@0.6")));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (string argument in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                     "-s", sheetWidth.ToString(CultureInfo.InvariantCulture) + "x" + sheetHeight.ToString(CultureInfo.InvariantCulture),
                     "-r", "1", "-i", "-", "-vf", labels, "-frames:v", "1", outputPath
                 })
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(sheet, 0, sheet.Length);
        process.StandardInput.BaseStream.Dispose();
        string error = stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not write {outputPath}: {error}");
    }

    private static void Blit(byte[] sheet, int sheetWidth, int x, int y, byte[] source, int sourceIndex)
    {
        int destination = ((y * sheetWidth) + x) * 3;
        sheet[destination] = source[sourceIndex];
        sheet[destination + 1] = source[sourceIndex + 1];
        sheet[destination + 2] = source[sourceIndex + 2];
    }

    /// <summary>The two "jump" statistics, on the pixel centroid and on the bounding-box centre.</summary>
    public static void FindLargestJumps(
        IReadOnlyList<T012FrameSample> frames, int width, int height,
        out int centroidFrame, out double centroidJump, out int boxFrame, out double boxJump)
    {
        double diagonal = Math.Sqrt((double)width * width + (double)height * height);
        centroidFrame = -1;
        centroidJump = 0;
        boxFrame = -1;
        boxJump = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            T012FrameSample previous = frames[i - 1], current = frames[i];
            if (previous.IsEmpty || current.IsEmpty)
                continue;

            double distance = Math.Sqrt(
                Math.Pow(current.Cx - previous.Cx, 2) + Math.Pow(current.Cy - previous.Cy, 2)) / diagonal;
            if (distance > centroidJump)
            {
                centroidJump = distance;
                centroidFrame = current.Index;
            }

            double box = Math.Sqrt(
                Math.Pow((current.Bx - previous.Bx) / width, 2) +
                Math.Pow((current.By - previous.By) / height, 2));
            if (box > boxJump)
            {
                boxJump = box;
                boxFrame = current.Index;
            }
        }
    }

    /// <summary>One full-resolution mask frame as a PNG — the wrap is a 1-row detail that a scaled sheet hides.</summary>
    public static void WriteMaskFrame(string maskPath, string outputPath, int frame)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        T012Compile.Run("ffmpeg", [
            "-v", "error", "-y", "-i", maskPath, "-vf", "select=eq(n\\," + frame.ToString(CultureInfo.InvariantCulture) + ")",
            "-frames:v", "1", "-pix_fmt", "gray", outputPath
        ]);
    }

    /// <summary>One full-resolution source frame as a PNG, for the same reason.</summary>
    public static void WriteVideoFrame(string videoPath, string outputPath, int frame)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        T012Compile.Run("ffmpeg", [
            "-v", "error", "-y", "-i", videoPath, "-vf", "select=eq(n\\," + frame.ToString(CultureInfo.InvariantCulture) + ")",
            "-frames:v", "1", outputPath
        ]);
    }

    /// <summary>A 5x7 white block glyph for the digits and 'n=' — enough to label a row without a font.</summary>
    private static void DrawGlyph(byte[] sheet, int sheetWidth, int x, int y, char character)
    {
        string[] rows = character switch
        {
            '0' => ["111", "101", "101", "101", "111"],
            '1' => ["010", "110", "010", "010", "111"],
            '2' => ["111", "001", "111", "100", "111"],
            '3' => ["111", "001", "111", "001", "111"],
            '4' => ["101", "101", "111", "001", "001"],
            '5' => ["111", "100", "111", "001", "111"],
            '6' => ["111", "100", "111", "101", "111"],
            '7' => ["111", "001", "001", "001", "001"],
            '8' => ["111", "101", "111", "101", "111"],
            '9' => ["111", "101", "111", "001", "111"],
            '=' => ["000", "111", "000", "111", "000"],
            'n' => ["000", "101", "111", "111", "101"],
            _ => ["000", "000", "000", "000", "000"]
        };

        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] != '1')
                    continue;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = x + (column * 2) + dx;
                        int py = y + (row * 2) + dy;
                        if (px < 0 || px >= sheetWidth || py < 0)
                            continue;
                        int destination = ((py * sheetWidth) + px) * 3;
                        if (destination + 2 >= sheet.Length)
                            continue;
                        sheet[destination] = 255;
                        sheet[destination + 1] = 255;
                        sheet[destination + 2] = 255;
                    }
                }
            }
        }
    }
}

/// <summary>Skips unless the captured trials are pointed at and ffmpeg is on PATH.</summary>
public sealed class T012TrialFactAttribute : FactAttribute
{
    public T012TrialFactAttribute()
    {
        if (!T012Artifacts.TrialsAvailable)
            Skip = $"T-012 evidence needs the captured trials: set {T012Artifacts.TrialsVariable}=<trials-dir>.";
        else if (new FfmpegFactAttribute().Skip is { } why)
            Skip = why;
    }
}
