#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// Evaluates the corner expressions of the real <c>perspective</c> filter string: the grammar
/// <c>ScreenInsertFilterBuilder</c> emits (numbers, <c>in</c>, <c>+ - *</c>, <c>if</c>, <c>lt</c>, parentheses).
/// Parsing the production string — not re-deriving it — is what makes S5 "exactly as ffmpeg evaluates it".
/// </summary>
internal static class FfmpegExpr
{
    public static Func<double, double> Parse(string text)
    {
        var p = new Parser(text);
        Func<double, double> f = p.ParseSum();
        p.End();
        return f;
    }

    private sealed class Parser(string s)
    {
        private int _i;

        public void End()
        {
            Skip();
            if (_i != s.Length)
                throw new FormatException($"Trailing text at {_i} in '{s}'.");
        }

        public Func<double, double> ParseSum()
        {
            Func<double, double> left = ParseProduct();
            while (true)
            {
                Skip();
                if (Peek('+'))
                {
                    _i++;
                    Func<double, double> a = left, b = ParseProduct();
                    left = x => a(x) + b(x);
                }
                else if (Peek('-'))
                {
                    _i++;
                    Func<double, double> a = left, b = ParseProduct();
                    left = x => a(x) - b(x);
                }
                else
                {
                    return left;
                }
            }
        }

        private Func<double, double> ParseProduct()
        {
            Func<double, double> left = ParseFactor();
            while (true)
            {
                Skip();
                if (!Peek('*'))
                    return left;
                _i++;
                Func<double, double> a = left, b = ParseFactor();
                left = x => a(x) * b(x);
            }
        }

        private Func<double, double> ParseFactor()
        {
            Skip();
            if (Peek('-'))
            {
                _i++;
                Func<double, double> inner = ParseFactor();
                return x => -inner(x);
            }

            if (Peek('('))
            {
                _i++;
                Func<double, double> inner = ParseSum();
                Expect(')');
                return inner;
            }

            if (char.IsDigit(s[_i]) || s[_i] == '.')
            {
                int start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.'))
                    _i++;
                if (_i < s.Length && (s[_i] == 'E' || s[_i] == 'e'))
                {
                    _i++;
                    if (_i < s.Length && (s[_i] == '+' || s[_i] == '-'))
                        _i++;
                    while (_i < s.Length && char.IsDigit(s[_i]))
                        _i++;
                }

                double v = double.Parse(s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                return _ => v;
            }

            int idStart = _i;
            while (_i < s.Length && char.IsLetter(s[_i]))
                _i++;
            string id = s[idStart.._i];
            if (id.Length == 0)
                throw new FormatException($"Unexpected '{s[_i]}' at {_i} in '{s}'.");
            if (id == "in")
                return x => x;

            Expect('(');
            var args = new List<Func<double, double>> { ParseSum() };
            Skip();
            while (Peek(','))
            {
                _i++;
                args.Add(ParseSum());
                Skip();
            }

            Expect(')');
            return id switch
            {
                "lt" => x => args[0](x) < args[1](x) ? 1.0 : 0.0,
                "if" => x => args[0](x) != 0 ? args[1](x) : (args.Count > 2 ? args[2](x) : 0.0),
                _ => throw new FormatException($"Unknown function '{id}'.")
            };
        }

        private void Expect(char c)
        {
            Skip();
            if (!Peek(c))
                throw new FormatException($"Expected '{c}' at {_i} in '{s}'.");
            _i++;
        }

        private bool Peek(char c) => _i < s.Length && s[_i] == c;

        private void Skip()
        {
            while (_i < s.Length && s[_i] == ' ')
                _i++;
        }
    }
}

/// <summary>
/// S5: a tracker track (S4) through the compile step's own keyframe sampling and conversion
/// (<c>SampleInsertKeyframes</c> / <c>BuildInsertKeyframes</c>: overscan by centroid expansion and the
/// source-to-canvas fit), written as ffmpeg <c>perspective</c> expressions by
/// <c>ScreenInsertFilterBuilder.BuildPerspectiveFilter</c>, then evaluated per output frame by parsing that very string.
/// </summary>
internal static class CompileStage
{
    internal sealed record Result(
        string Name,
        double Overscan,
        VideoCompileStepExecutor.SourceCanvasFit Fit,
        IReadOnlyList<InsertQuadFrame> Keyframes,
        string PerspectiveFilter,
        double OutputStartSec,
        double OutputEndSec,
        int FpsNum,
        int FpsDen,
        /// <summary>Per output frame of the insert branch (frame 0 = insert start), canvas px: what the filter pins.</summary>
        List<Quad> CanvasPx,
        /// <summary>The truth pushed through the same overscan and fit (canvas px) — the ideal the expression approximates.</summary>
        List<Quad> IdealCanvasPx,
        /// <summary>Canvas px mapped back to source px through the inverse fit.</summary>
        List<Quad> SourcePx,
        List<Quad> IdealSourcePx,
        double AgreementMaxDiff);

    public static Result Run(
        string name, VideoInsertRegionTrack track, Func<int, Quad> truthAt, int sw, int sh,
        int canvasW, int canvasH, bool cover, double overscan, int fpsNum, int fpsDen, double duration)
    {
        double fps = fpsNum / (double)fpsDen;
        var canvas = new VideoAnalysisMedia(duration, fpsNum, fpsDen, canvasW, canvasH);
        var source = new VideoAnalysisMedia(duration, fpsNum, fpsDen, sw, sh);
        var fit = cover
            ? VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true)
            : VideoCompileStepExecutor.SourceCanvasFit.Identity(canvas);

        long totalFrames = (long)Math.Round(duration * fps);
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, duration, 0, duration, 0, totalFrames) };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        (double Start, double End) window = timeline.MapWindowToOutput(track.StartSec, track.EndSec, track.SourceIndex)
            ?? throw new InvalidOperationException("Track window does not map through the timeline.");

        IReadOnlyList<InsertQuadFrame> keyframes = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, timeline, canvas, window, new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1).MaxInsertExprKeyframes,
            overscan, fit);
        string perspective = ScreenInsertFilterBuilder.BuildPerspectiveFilter(keyframes);

        Func<double, double>[] exprs = new[] { "x0", "y0", "x1", "y1", "x2", "y2", "x3", "y3" }
            .Select(k => FfmpegExpr.Parse(Regex.Match(perspective, $@"\b{k}='([^']*)'").Groups[1].Value))
            .ToArray();

        // Semantic cross-check against an independent piecewise-linear reading of the keyframes.
        double agreement = 0;
        Func<InsertQuadFrame, double>[] picks = [k => k.X0, k => k.Y0, k => k.X1, k => k.Y1, k => k.X2, k => k.Y2, k => k.X3, k => k.Y3];
        long last = keyframes[^1].FrameIndex;
        foreach (long probe in new[] { 0L, 1L, last / 7, last / 3, last / 2, last * 2 / 3, last - 1, last, last + 5 })
        {
            if (probe < 0)
                continue;
            for (int c = 0; c < 8; c++)
            {
                double got = exprs[c](probe + ScreenInsertFilterBuilder.PerspectiveFirstFrame);
                double want = Piecewise(keyframes, picks[c], probe);
                agreement = Math.Max(agreement, Math.Abs(got - want));
            }
        }

        if (agreement > 1e-6)
            throw new InvalidOperationException($"Expression evaluator disagrees with the piecewise-linear definition by {agreement}.");

        long startFrame = (long)Math.Round(window.Start * fps);
        var canvasPx = new List<Quad>();
        var idealCanvas = new List<Quad>();
        var srcPx = new List<Quad>();
        var idealSrc = new List<Quad>();
        for (long i = 0; i <= last; i++)
        {
            long g = startFrame + i;
            if (g >= totalFrames)
                break;
            double t = g / fps;
            double[] v = exprs.Select(e => e(i + ScreenInsertFilterBuilder.PerspectiveFirstFrame)).ToArray();
            Quad c = new(t, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
            Quad ideal = Ideal(truthAt((int)g), t, sw, sh, fit, overscan);
            canvasPx.Add(c);
            idealCanvas.Add(ideal);
            srcPx.Add(ToSource(c, fit, sw, sh));
            idealSrc.Add(ToSource(ideal, fit, sw, sh));
        }

        return new Result(name, overscan, fit, keyframes, perspective, window.Start, window.End, fpsNum, fpsDen, canvasPx, idealCanvas, srcPx, idealSrc, agreement);
    }

    /// <summary>The compile step's overscan + fit applied to a ground-truth quad: the ideal canvas corners.</summary>
    private static Quad Ideal(Quad truthSrc, double t, int sw, int sh, VideoCompileStepExecutor.SourceCanvasFit fit, double overscan)
    {
        double cx = (truthSrc.X0 + truthSrc.X1 + truthSrc.X2 + truthSrc.X3) / 4.0 / sw;
        double cy = (truthSrc.Y0 + truthSrc.Y1 + truthSrc.Y2 + truthSrc.Y3) / 4.0 / sh;
        double Ex(double xPx) => fit.OffsetX + (cx + (xPx / sw - cx) * (1 + overscan)) * fit.Width;
        double Ey(double yPx) => fit.OffsetY + (cy + (yPx / sh - cy) * (1 + overscan)) * fit.Height;
        return new Quad(t, Ex(truthSrc.X0), Ey(truthSrc.Y0), Ex(truthSrc.X1), Ey(truthSrc.Y1), Ex(truthSrc.X2), Ey(truthSrc.Y2), Ex(truthSrc.X3), Ey(truthSrc.Y3));
    }

    private static Quad ToSource(Quad c, VideoCompileStepExecutor.SourceCanvasFit fit, int sw, int sh)
    {
        double X(double v) => (v - fit.OffsetX) / fit.Width * sw;
        double Y(double v) => (v - fit.OffsetY) / fit.Height * sh;
        return new Quad(c.T, X(c.X0), Y(c.Y0), X(c.X1), Y(c.Y1), X(c.X2), Y(c.Y2), X(c.X3), Y(c.Y3));
    }

    /// <summary>Independent piecewise-linear value at insert-branch frame <paramref name="f"/>: hold after the last key, extrapolate before the first.</summary>
    private static double Piecewise(IReadOnlyList<InsertQuadFrame> keys, Func<InsertQuadFrame, double> pick, long f)
    {
        if (keys.Count == 1 || f >= keys[^1].FrameIndex)
            return pick(keys[^1]);
        int i = 0;
        while (i + 2 < keys.Count && f >= keys[i + 1].FrameIndex)
            i++;
        double f0 = keys[i].FrameIndex, f1 = keys[i + 1].FrameIndex;
        return pick(keys[i]) + (f - f0) * (pick(keys[i + 1]) - pick(keys[i])) / (f1 - f0);
    }
}
