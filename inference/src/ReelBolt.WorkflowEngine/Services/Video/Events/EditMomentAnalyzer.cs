using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>What happened to the content of the cells an event covers.</summary>
internal enum MomentKind
{
    Appear,
    Disappear,
    Move,
    Change,
    Pulse
}

/// <summary>One burst of change found by <see cref="EditMomentAnalyzer"/>, before it is given a type.</summary>
internal sealed record MomentCandidate(
    int Frame, int End, int FirstCross, int Cells, double X0, double Y0, double X1, double Y1, MomentKind Kind,
    int Significant, int Pulses, double Still, double Energy, double PeakShare, double EdgeBefore, double EdgeAfter,
    double PulseUp, double SweepX, double SweepY, double SweepFit, double Grow, bool Intro, bool Panel, bool PlainCut = false)
{
    public double Area => Cells / (double)EditMomentAnalyzer.Cells;
}

/// <summary>
/// Frame-level moments a sound belongs on in an edited video (motion graphics, footage panels, burned-in captions):
/// hard cuts (frame-exact, also a cut inside a panel), soft transitions, things that appear, leave, move or change in
/// place, flashes, and the words of running captions (tagged so no sound lands on them).
/// <para>
/// The picture is read as a 32x18 grid of cells. A cell that sits in still surroundings and changes is the start of an
/// event. Inside footage the rule is stricter, and it rests on one observation: footage shown inside a 60 fps edit holds
/// each of its frames for two or three output frames, so it changes on isolated frames, while a graphic animates on
/// every frame. A cell inside a footage panel therefore only counts when it changes several frames running. Cells that
/// start changing together, next to each other, are one event (a wavefront); what happened to their content (empty to
/// full, full to empty, both, or replaced) gives the type.
/// </para>
/// Deterministic, no model and no paid call. Memory stays bounded: per-cell data is kept only for the shot being read.
/// </summary>
internal sealed class EditMomentAnalyzer : IFrameConsumer
{
    private const int W = FrameSignalSampler.Width, H = FrameSignalSampler.Height;
    private const int Pixels = W * H;
    internal const int CellPx = 5;
    internal const int CellsX = W / CellPx;
    internal const int CellsY = H / CellPx;
    internal const int Cells = CellsX * CellsY;
    private const float CellArea = CellPx * CellPx;

    // ---- constants (grey levels 0..255 on the 160x90 grid; times in seconds, turned into frames with the analysed rate) ----

    /// <summary>Mean absolute change of a cell that counts as a change.</summary>
    internal const float ChangeLevel = 1.0f;
    /// <summary>A cell that changes by more than this flickers (footage grain, encoder noise).</summary>
    internal const float FlickerLevel = 0.3f;
    /// <summary>Footage that holds frames: the cell changed on this share of the recent frames, each time for one frame only.</summary>
    internal const double IsolatedShare = 0.13;
    /// <summary>Footage or animation at the full rate: the cell changed on this share of the recent frames.</summary>
    internal const double FullRateShare = 0.8;
    /// <summary>This many such cells make a panel.</summary>
    internal const int PanelMinCells = 8;
    /// <summary>Inside a panel that holds frames, a change counts only as part of this many changed frames running.</summary>
    internal const int RunFrames = 4;
    /// <summary>Inside full-rate footage a change must also stand this many times above the cell's usual change.</summary>
    internal const double FullRateFactor = 5.0;
    internal const double AmbientQuantile = 0.8;
    internal const double AmbientSeconds = 1.5;
    /// <summary>What starts this soon after a cut belongs to the cut.</summary>
    internal const double IntroSeconds = 0.1;
    /// <summary>A burst bridges this long without change (a 24 fps picture holds three frames at 60).</summary>
    internal const double GapSeconds = 0.07;
    internal const double MaxBurstSeconds = 3.0;
    /// <summary>Neighbouring cells whose changes start within this long of each other are one wavefront.</summary>
    internal const double JoinSeconds = 5 / 60.0;
    /// <summary>A small group that started at most this long before a bigger neighbour, and is still changing, is its seed.</summary>
    internal const double SeedSeconds = 0.25;
    /// <summary>A slow start is walked back at most this long while the cells were already moving a little.</summary>
    internal const double WalkBackSeconds = 0.5;
    internal const float WalkBackLevel = 0.25f;
    internal const int StateFrames = 8;

    /// <summary>A hard cut: mean absolute change of the frame at least this, a spike against its neighbours and against the surrounding second.</summary>
    internal const double CutMeanDiff = 9.0;
    internal const double CutSpikeRatio = 2.0;
    internal const double CutMeanDiffStill = 4.0;
    internal const double CutStillRatio = 0.125;
    internal const int CutNeighbourFrames = 3;
    internal const double CutSustainRatio = 3.0;
    internal const double CutSustainQuantile = 0.75;
    /// <summary>A cut that replaces at least this share of the cells is a cut of the whole picture; below it a panel was cut.</summary>
    internal const double CutShare = 0.45;
    /// <summary>The frame after a cut still changing this much is the cut settling (a blended frame).</summary>
    internal const double SettleShare = 0.25;

    internal const double SignificantLuma = 4.0;
    internal const double SignificantEdge = 1.5;
    internal const double PulsePeak = 8.0;
    internal const int MinCellsStill = 2;
    internal const int MinCellsLive = 4;
    internal const double MinEnergy = 30.0;
    /// <summary>A soft transition touches this share of the cells, and this share is changing at the same time.</summary>
    internal const double GlobalArea = 0.45;
    internal const double GlobalPeak = 0.30;
    internal const double PanelCutArea = 0.12;
    internal const double BlankEdge = 2.0;

    // ---- a full picture on one side of a cut (see IsFullPicture) ----
    /// <summary>The picture before a cut is read from this many seconds before it...</summary>
    internal const double PictureBeforeSeconds = 0.7;
    /// <summary>...up to this close to it; the picture after, from <see cref="PictureSettleSeconds"/> to <see cref="PictureAfterSeconds"/> after it.</summary>
    internal const double PictureGuardSeconds = 0.1;
    internal const double PictureSettleSeconds = 0.2;
    internal const double PictureAfterSeconds = 0.5;
    /// <summary>Fewest frames a side needs to be read at all.</summary>
    internal const int PictureMinFrames = 4;
    /// <summary>A cell that changes on at most this share of the frames is still.</summary>
    internal const double PictureStillShare = 0.05;
    /// <summary>The picture, bars set aside, must be at least this share of the frame.</summary>
    internal const double PictureMinArea = 0.25;
    /// <summary>Under this share of flat cells the area is a picture; from it up, a designed page.</summary>
    internal const double PictureMaxFlat = 0.2;
    internal const double BlankLuma = 12.0;
    internal const double MajorShare = 0.6;
    internal const double MinorShare = 0.25;
    internal const double EdgeGainShare = 0.8;
    internal const double EdgeGrowth = 2.5;
    internal const double TextAspect = 2.5;
    internal const double TextMaxHeight = 0.13;
    internal const double TextEdge = 8.0;
    internal const double CaptionWindowSeconds = 2.5;
    internal const int CaptionMinNeighbours = 2;
    internal const double CaptionDy = 0.06;
    internal const double CaptionDx = 0.25;
    internal const double CaptionMaxArea = 0.12;
    /// <summary>A caption word is always weak: it ranks last in every list.</summary>
    internal const double CaptionStrength = 0.2;

    /// <summary>A whole-frame flash: mean brightness up this many levels within two frames and back within <see cref="FlashMaxSeconds"/>.</summary>
    internal const double FlashRise = 10.0;
    internal const double FlashMaxSeconds = 0.1;
    internal const double FlashReturn = 0.4;
    /// <summary>After a flash the picture is back to what it was: its cells differ from before by at most this on average.</summary>
    internal const double FlashSameLevel = 6.0;
    /// <summary>A local flash (a camera flash in a crowd): this many near-white pixels light up in one ninth of the frame and are gone again.</summary>
    internal const int BrightLevel = 235;
    internal const int SparkPixels = 20;
    internal const double SparkMaxSeconds = 0.12;
    internal const double SparkReturn = 0.6;
    internal const double SparkMaxBaseShare = 0.4;
    internal const double SparkMaxRegionLuma = 140.0;
    /// <summary>A pixel lit by a flash was at least this many levels darker a frame before; this share of the jump must be such pixels.</summary>
    internal const int SparkStep = 30;
    internal const double SparkNewShare = 0.5;

    /// <summary>This many separate things starting within <see cref="CoherentFrames"/> frames of each other are one move of the layout.</summary>
    internal const int CoherentMin = 6;
    internal const int CoherentFrames = 2;
    internal const double CoherentJoinSeconds = 0.5;

    /// <summary>The whole picture starts moving: the share of changed cells climbs through <see cref="GlobalMoveOn"/> from below <see cref="GlobalMoveLow"/> and reaches <see cref="GlobalMoveHigh"/>.</summary>
    internal const float GlobalMoveOn = 0.15f;
    internal const float GlobalMoveLow = 0.08f;
    internal const float GlobalMoveHigh = 0.25f;
    internal const float GlobalMoveSeed = 0.04f;
    internal const double GlobalMoveLookSeconds = 0.75;
    internal const double GlobalMoveSustainSeconds = 0.25;
    internal const double GlobalMoveNearSeconds = 0.3;

    /// <summary>A shot longer than this is read in pieces (with an overlap), so memory stays bounded however long a take is.</summary>
    internal const double MaxSpanSeconds = 20.0;
    internal const double SpanOverlapSeconds = 5.0;
    internal const double SpanAheadSeconds = 3.0;

    /// <summary>At most this many moments are returned; the strongest are kept (ties: earliest) and the rest are counted in <see cref="DroppedMoments"/>.</summary>
    internal const int MaxMoments = 1500;
    /// <summary>At most this many bursts are kept while reading; a pathologically busy source stops recording instead of growing without bound.</summary>
    internal const int MaxCandidates = 60_000;

    /// <summary>How many moments the cap removed in the last <see cref="Complete"/>; non-zero means the list is not complete.</summary>
    public int DroppedMoments { get; private set; }

    /// <summary>Frames of per-cell data held right now: stays below one span however long the video is.</summary>
    internal int BufferedFrames => _cellDiff.Count;

    private readonly double _fps;
    private readonly double _aspectW, _aspectH;

    // per frame, whole video (small)
    private readonly List<float> _globalDiff = [];
    private readonly List<float> _luma = [];
    private readonly List<float> _changedShare = [];
    private readonly List<int> _bright = [];   // 9 per frame
    private readonly List<int> _newBright = []; // 9 per frame: pixels that were clearly darker a frame ago
    private readonly List<float> _regionLuma = []; // 9 per frame: mean brightness of each ninth

    // per cell, only the frames still needed
    private readonly List<float[]> _cellDiff = [];
    private readonly List<float[]> _cellLuma = [];
    private readonly List<float[]> _cellEdge = [];
    private int _base;

    private readonly byte[] _previous = new byte[Pixels];
    private int _frames;

    private readonly List<(int Frame, double Share, double Diff)> _cuts = [];
    private readonly List<(int Frame, int End, double Rise)> _flashes = [];

    /// <summary>Hard cuts with a full picture on both sides and no designed page on either: plain cuts between two shots.</summary>
    private readonly HashSet<int> _plainCuts = [];

    /// <summary>Stretches of frames that show one full picture (see <see cref="IsFullPicture"/>), in order; a few numbers per shot.</summary>
    private readonly List<(int From, int To)> _fullPictures = [];
    private int _pictureFrom;
    private readonly HashSet<int> _flashFrames = [];
    private int _cutCursor = 1;
    private int _flashCursor = 3;
    private int _spanStart;
    private bool _spanStartsWithCut;
    private int _emitFrom;
    private readonly List<MomentCandidate> _candidates = [];
    private bool _candidatesTruncated;

    /// <param name="fps">Frames per second of the frames handed to <see cref="OnFrame"/>.</param>
    /// <param name="sourceWidth">Width of the source picture (the grid is always 160x90, so shapes need the real aspect).</param>
    /// <param name="sourceHeight">Height of the source picture.</param>
    public EditMomentAnalyzer(double fps = 60, int sourceWidth = 16, int sourceHeight = 9)
    {
        _fps = fps > 0 && double.IsFinite(fps) ? Math.Clamp(fps, 1.0, 240.0) : 60.0;
        _aspectW = sourceWidth > 0 ? sourceWidth : 16;
        _aspectH = sourceHeight > 0 ? sourceHeight : 9;
    }

    private int Frames(double seconds) => (int)Math.Round(seconds * _fps, MidpointRounding.AwayFromZero);

    public void OnFrame(int index, ReadOnlySpan<byte> gray)
    {
        float[] diff = new float[Cells], luma = new float[Cells], edge = new float[Cells];
        long sum = 0, diffSum = 0;
        Span<int> bright = stackalloc int[9];
        Span<int> newBright = stackalloc int[9];
        Span<long> regionSum = stackalloc long[9];
        Span<int> regionCount = stackalloc int[9];
        bool first = _frames == 0;
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            int cellRow = y / CellPx * CellsX;
            int regionRow = Math.Min(2, y / (H / 3)) * 3;
            for (int x = 0; x < W; x++)
            {
                int i = row + x;
                int v = gray[i];
                int c = cellRow + x / CellPx;
                sum += v;
                luma[c] += v;
                int region = regionRow + (x < 53 ? 0 : x < 107 ? 1 : 2);
                regionSum[region] += v;
                regionCount[region]++;
                if (v >= BrightLevel)
                {
                    bright[region]++;
                    if (!first && _previous[i] <= BrightLevel - SparkStep)
                        newBright[region]++;
                }

                // detail inside the cell only: a neighbour's edge must not make an empty cell look full
                int g = 0;
                if (x % CellPx != 0)
                    g += Math.Abs(v - gray[i - 1]);
                if (y % CellPx != 0)
                    g += Math.Abs(v - gray[i - W]);
                edge[c] += g;
                if (first)
                    continue;
                int d = Math.Abs(v - _previous[i]);
                diffSum += d;
                diff[c] += d;
            }
        }

        int changed = 0;
        for (int c = 0; c < Cells; c++)
        {
            luma[c] /= CellArea;
            edge[c] /= CellArea;
            diff[c] /= CellArea;
            if (diff[c] > ChangeLevel)
                changed++;
        }

        _cellDiff.Add(diff);
        _cellLuma.Add(luma);
        _cellEdge.Add(edge);
        _globalDiff.Add(first ? 0 : (float)diffSum / Pixels);
        _luma.Add((float)sum / Pixels);
        _changedShare.Add((float)changed / Cells);
        for (int r = 0; r < 9; r++)
        {
            _bright.Add(bright[r]);
            _newBright.Add(newBright[r]);
            _regionLuma.Add(regionCount[r] > 0 ? (float)regionSum[r] / regionCount[r] : 0f);
        }

        gray[..Pixels].CopyTo(_previous);
        _frames++;
        Advance(final: false);
    }

    public List<VideoEvent> Complete()
    {
        Advance(final: true);
        if (_frames - _spanStart >= 4)
            AnalyseSpan(_spanStart, _frames, _spanStartsWithCut, _emitFrom, int.MaxValue);
        if (IsFullPicture(_frames - 1 - Frames(PictureBeforeSeconds), _frames - 1))
            _fullPictures.Add((_pictureFrom, _frames - 1));
        _cellDiff.Clear();
        _cellLuma.Clear();
        _cellEdge.Clear();

        List<Moment> moments = [];
        foreach ((int frame, int end, double rise) in _flashes)
            moments.Add(new Moment(VideoEventType.Flash, frame, end, null, Math.Clamp(rise / 60.0, 0.4, 1.0), 0.7));
        foreach ((int frame, double share, double diff) in _cuts)
            if (share >= CutShare)
                moments.Add(new Moment(VideoEventType.Transition, frame, null, null, Math.Min(1.0, Math.Max(0.4, diff / 40.0)), 0.9,
                    Kind: _plainCuts.Contains(frame) ? VideoEventKinds.Footage : null));

        foreach (MomentCandidate candidate in _candidates.OrderBy(c => c.Frame).ThenBy(c => c.X0).ThenBy(c => c.Y0).ThenBy(c => c.X1).ThenBy(c => c.Y1))
        {
            if (candidate.Intro || InFlash(candidate.Frame))
                continue;
            Moment? moment = Classify(candidate);
            if (moment is not null)
                moments.Add(moment);
        }

        // a flash seen twice (as cells that lit up and as near-white pixels) is one flash
        // and on a still page, something bright that arrives or travels is that thing: the cells have already said
        // what it is. Inside footage the cells only saw the flash itself, so there the flash stands.
        int[] flashFrames = [.. moments.Where(m => m.Type == VideoEventType.Flash).Select(m => m.Frame).Order()];
        List<Moment> things = [.. moments.Where(m => m.Area > 0 && m.InStillSurroundings && m.Type != VideoEventType.Flash && m.Region is not null)];
        foreach (Moment spark in Sparks())
        {
            if (Near(flashFrames, spark.Frame, 3))
                continue;
            VideoEventRegion at = spark.Region!;
            bool sameThing = things.Any(m => m.Frame - 2 <= spark.Frame && spark.Frame <= (m.End ?? m.Frame) + 2
                && m.Region!.X < at.X + at.W && at.X < m.Region.X + m.Region.W && m.Region.Y < at.Y + at.H && at.Y < m.Region.Y + m.Region.H);
            if (!sameThing)
                moments.Add(spark);
        }

        // what starts inside a soft transition is part of it
        List<(int Start, int End)> soft = [.. moments.Where(m => m.Type == VideoEventType.Transition && m.Soft && m.End is not null).Select(m => (m.Frame, m.End!.Value))];
        if (soft.Count > 0)
            moments = [.. moments.Where(m => m.Type == VideoEventType.Transition || !soft.Any(s => s.Start - 2 <= m.Frame && m.Frame <= s.End))];

        moments = MergeCoherent(moments);
        TagCaptions(moments);
        // the whole picture moving inside a filmed shot is the camera, not a designed move
        moments.AddRange(GlobalMoves(moments).Select(m => InFullPicture(m.Frame) ? m with { Kind = VideoEventKinds.Footage } : m));

        List<VideoEvent> events = [.. moments.Select(m => m.ToEvent())];
        DroppedMoments = Math.Max(0, events.Count - MaxMoments) + (_candidatesTruncated ? 1 : 0);
        if (events.Count > MaxMoments)
            events = [.. events.OrderByDescending(e => e.Strength).ThenBy(e => e.Frame).ThenBy(e => e.Type).Take(MaxMoments)];
        return [.. events.OrderBy(e => e.Frame).ThenBy(e => e.Type)];
    }

    // ---- reading along: flashes, cuts, and one span of cells at a time ---------------------------------------

    private void Advance(bool final)
    {
        int n = _frames;
        int flashMax = Math.Max(2, Frames(FlashMaxSeconds));
        int half = Frames(0.5);

        // whole-frame flashes are decided first: a frame inside one is not a cut
        int flashLimit = final ? n - 1 : n - flashMax - 2;
        while (_flashCursor < flashLimit)
        {
            int t = _flashCursor;
            double basis = (_luma[t - 1] + _luma[t - 2] + _luma[t - 3]) / 3.0;
            double peak = Math.Max(_luma[t], _luma[t + 1]);
            double rise = peak - basis;
            int back = -1;
            if (rise >= FlashRise && _luma[t] >= basis + FlashRise / 2 && _luma[t - 1] <= basis + 2)
            {
                for (int k = t + 1; k <= Math.Min(n - 1, t + flashMax); k++)
                {
                    if (_luma[k] <= basis + FlashReturn * rise)
                    {
                        back = k;
                        break;
                    }
                }
            }

            // a flash leaves the picture as it was; a bright shot between two other shots of a montage does not
            if (back < 0 || !SamePicture(t - 1, back))
            {
                _flashCursor++;
                continue;
            }

            _flashes.Add((t, back, rise));
            for (int k = t - 1; k <= back + 1; k++)
                _flashFrames.Add(k);
            _flashCursor = back + 1;
        }

        int cutLimit = final ? n - 1 : Math.Min(n - half - 1, _flashCursor - 2);
        while (_cutCursor < cutLimit)
        {
            int t = _cutCursor++;
            if (!IsCut(t, n, half) || _flashFrames.Contains(t))
                continue;
            _cuts.Add((t, _changedShare[t], _globalDiff[t]));
            if (_changedShare[t] < CutShare || t <= _spanStart)
                continue;
            // read while the frames on both sides are still held: a cut from one full picture to another is a plain cut
            bool fullBefore = IsFullPicture(Math.Max(_spanStart + 1, t - Frames(PictureBeforeSeconds)), t - Frames(PictureGuardSeconds));
            if (fullBefore)
                _fullPictures.Add((_pictureFrom, t - 1));
            _pictureFrom = t;
            if (fullBefore && IsFullPicture(t + Frames(PictureSettleSeconds), Math.Min(n - 1, t + Frames(PictureAfterSeconds))))
                _plainCuts.Add(t);
            AnalyseSpan(_spanStart, t, _spanStartsWithCut, _emitFrom, int.MaxValue);
            _spanStart = t;
            _spanStartsWithCut = true;
            _emitFrom = t;
            Trim(t);
        }

        // a long take is read in pieces so the per-cell data never grows past one piece
        int maxSpan = Frames(MaxSpanSeconds), ahead = Frames(SpanAheadSeconds), overlap = Frames(SpanOverlapSeconds);
        while (_cutCursor - _spanStart >= maxSpan + ahead)
        {
            int until = _emitFrom + maxSpan - (_emitFrom - _spanStart);
            if (until <= _emitFrom)
                until = _emitFrom + maxSpan;
            AnalyseSpan(_spanStart, until + ahead, _spanStartsWithCut, _emitFrom, until);
            if (IsFullPicture(until - Frames(PictureBeforeSeconds), until))
                _fullPictures.Add((_pictureFrom, until - 1));
            _pictureFrom = until;
            _emitFrom = until;
            _spanStart = until - overlap;
            _spanStartsWithCut = false;
            Trim(_spanStart);
        }
    }

    /// <summary>
    /// True when frames [<paramref name="from"/>, <paramref name="to"/>] show a picture that fills its area: after setting
    /// aside bars (whole rows, then whole columns, of flat cells that never change, from the edges inward), what is left is
    /// at least <see cref="PictureMinArea"/> of the frame and under <see cref="PictureMaxFlat"/> of it is flat. A designed
    /// page (flat background with elements on it) is not; filmed footage, full frame or letterboxed, is. So is a photo or
    /// a document shown full frame: the pixels cannot tell those from footage, which is why the picture model, when it is
    /// asked, looks at exactly these cuts first. False when the window is too short to say.
    /// </summary>
    private bool IsFullPicture(int from, int to)
    {
        from = Math.Max(from, _base);
        to = Math.Min(to, _base + _cellDiff.Count - 1);
        int frames = to - from + 1;
        if (frames < PictureMinFrames)
            return false;

        Span<bool> flat = stackalloc bool[Cells];
        Span<bool> blank = stackalloc bool[Cells];
        for (int c = 0; c < Cells; c++)
        {
            double edge = 0;
            int active = 0;
            for (int t = from; t <= to; t++)
            {
                edge += _cellEdge[t - _base][c];
                if (_cellDiff[t - _base][c] > FlickerLevel)
                    active++;
            }

            flat[c] = edge / frames < BlankEdge;
            blank[c] = flat[c] && active <= PictureStillShare * frames;
        }

        int top = 0, bottom = CellsY, left = 0, right = CellsX;
        while (top < bottom && RowIs(blank, top, 0, CellsX))
            top++;
        while (bottom > top && RowIs(blank, bottom - 1, 0, CellsX))
            bottom--;
        while (left < right && bottom > top && ColumnIs(blank, left, top, bottom))
            left++;
        while (right > left && bottom > top && ColumnIs(blank, right - 1, top, bottom))
            right--;

        int area = (bottom - top) * (right - left);
        if (area < PictureMinArea * Cells)
            return false;
        int flatCells = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (flat[y * CellsX + x])
                    flatCells++;
        return flatCells < PictureMaxFlat * area;
    }

    private static bool RowIs(ReadOnlySpan<bool> cells, int y, int x0, int x1)
    {
        for (int x = x0; x < x1; x++)
            if (!cells[y * CellsX + x])
                return false;
        return true;
    }

    private static bool ColumnIs(ReadOnlySpan<bool> cells, int x, int y0, int y1)
    {
        for (int y = y0; y < y1; y++)
            if (!cells[y * CellsX + x])
                return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="frame"/> lies in a stretch that showed one full picture (filmed footage, full frame or
    /// letterboxed; see <see cref="IsFullPicture"/>). Valid once <see cref="Complete"/> has run.
    /// </summary>
    public bool InFullPicture(int frame)
    {
        foreach ((int from, int to) in _fullPictures)
            if (frame >= from && frame <= to)
                return true;
        return false;
    }

    /// <summary>True when two frames show the same picture: their cells differ by less than <see cref="FlashSameLevel"/> on average.</summary>
    private bool SamePicture(int a, int b)
    {
        if (a < _base || b < _base || a - _base >= _cellLuma.Count || b - _base >= _cellLuma.Count)
            return true;
        float[] x = _cellLuma[a - _base], y = _cellLuma[b - _base];
        double sum = 0;
        for (int c = 0; c < Cells; c++)
            sum += Math.Abs(x[c] - y[c]);
        return sum / Cells <= FlashSameLevel;
    }

    private void Trim(int keepFrom)
    {
        int drop = Math.Min(keepFrom - _base, _cellDiff.Count);
        if (drop <= 0)
            return;
        _cellDiff.RemoveRange(0, drop);
        _cellLuma.RemoveRange(0, drop);
        _cellEdge.RemoveRange(0, drop);
        _base += drop;
    }

    private bool IsCut(int t, int n, int half)
    {
        double g = _globalDiff[t];
        if (g < CutMeanDiff)
        {
            // two alike pictures (pages of one book) differ little on average, but when nothing else moves and most
            // of the picture changed in one frame, it is still a cut
            if (g < CutMeanDiffStill || _changedShare[t] < CutShare)
                return false;
            for (int k = Math.Max(0, t - CutNeighbourFrames); k <= Math.Min(n - 1, t + CutNeighbourFrames); k++)
                if (k != t && _globalDiff[k] > g * CutStillRatio)
                    return false;
            return true;
        }

        // a 24 fps picture at 60 fps changes every second or third frame: only a jump bigger than every change around it is a cut
        double around = 0;
        for (int k = Math.Max(0, t - CutNeighbourFrames); k <= Math.Min(n - 1, t + CutNeighbourFrames); k++)
            if (k != t)
                around = Math.Max(around, _globalDiff[k]);
        if (g < CutSpikeRatio * around)
            return false;
        // busy footage changes a lot on most frames; a montage of cuts does not, so the bar is the upper quartile of the surrounding second
        int lo = Math.Max(0, t - half), hi = Math.Min(n - 1, t + half);
        double[] window = new double[hi - lo];
        int m = 0;
        for (int k = lo; k <= hi; k++)
            if (k != t)
                window[m++] = _globalDiff[k];
        return m == 0 || g >= CutSustainRatio * Quantile(window, m, CutSustainQuantile);
    }

    private bool InFlash(int frame)
    {
        foreach ((int f, int end, _) in _flashes)
            if (frame >= f - 1 && frame <= end + 1)
                return true;
        return false;
    }

    /// <summary>The q-quantile of the first <paramref name="count"/> values, linear between the two nearest ranks. Sorts the array in place.</summary>
    private static double Quantile(double[] values, int count, double q)
    {
        if (count <= 0)
            return 0;
        Array.Sort(values, 0, count);
        double position = q * (count - 1);
        int lo = (int)Math.Floor(position), hi = (int)Math.Ceiling(position);
        return values[lo] + (values[hi] - values[lo]) * (position - lo);
    }

    // ---- the whole picture starts moving -----------------------------------------------------------------------

    private static bool Near(int[] sorted, int frame, int window)
    {
        int i = Array.BinarySearch(sorted, frame - window);
        if (i < 0)
            i = ~i;
        return i < sorted.Length && sorted[i] <= frame + window;
    }

    /// <summary>
    /// Many separate things that start to change in the same instant are one move of the layout (a pan or a zoom of
    /// a page of scattered elements), or one group arriving or leaving together: one moment, not one per element.
    /// What starts while they are still changing is part of it.
    /// </summary>
    private List<Moment> MergeCoherent(List<Moment> moments)
    {
        List<Moment> local = [.. moments.Where(m => m.Area > 0).OrderBy(m => m.Frame)];
        if (local.Count < CoherentMin)
            return moments;
        HashSet<Moment> gone = [];
        List<Moment> merged = [];
        int i = 0;
        while (i < local.Count)
        {
            int j = i;
            while (j + 1 < local.Count && local[j + 1].Frame - local[i].Frame <= CoherentFrames)
                j++;
            if (j - i + 1 < CoherentMin)
            {
                i++;
                continue;
            }

            int end = local[i].Frame;
            for (int k = i; k <= j; k++)
                end = Math.Max(end, local[k].End ?? local[k].Frame);
            while (j + 1 < local.Count && local[j + 1].Frame <= end)
                j++;

            double x0 = 1, y0 = 1, x1 = 0, y1 = 0, area = 0;
            int appear = 0, disappear = 0, count = j - i + 1;
            for (int k = i; k <= j; k++)
            {
                Moment m = local[k];
                gone.Add(m);
                area += m.Area;
                if (m.Type is VideoEventType.Appear or VideoEventType.TextIn)
                    appear++;
                else if (m.Type is VideoEventType.Disappear or VideoEventType.TextOut)
                    disappear++;
                if (m.Region is { } r)
                {
                    x0 = Math.Min(x0, r.X);
                    y0 = Math.Min(y0, r.Y);
                    x1 = Math.Max(x1, r.X + r.W);
                    y1 = Math.Max(y1, r.Y + r.H);
                }
            }

            VideoEventRegion region = new(Math.Round(x0, 4), Math.Round(y0, 4), Math.Round(Math.Max(0, x1 - x0), 4), Math.Round(Math.Max(0, y1 - y0), 4));
            double strength = Math.Clamp(0.2 + Math.Sqrt(area / 0.06) * 0.5, 0.2, 1.0);
            VideoEventType type = appear >= MajorShare * count ? VideoEventType.Appear
                : disappear >= MajorShare * count ? VideoEventType.Disappear
                : VideoEventType.CameraMoveStart;
            merged.Add(new Moment(type, local[i].Frame, end, type == VideoEventType.CameraMoveStart ? null : region, strength, 0.6));
            i = j + 1;
        }

        if (gone.Count == 0)
            return moments;
        // a move that eases in shows as one burst after another: bursts that follow closely are the same move
        int join = Frames(CoherentJoinSeconds);
        List<Moment> joined = [];
        foreach (Moment m in merged)
        {
            Moment? last = joined.Count > 0 ? joined[^1] : null;
            if (last is not null && last.Type == VideoEventType.CameraMoveStart && m.Type == VideoEventType.CameraMoveStart
                && m.Frame - (last.End ?? last.Frame) <= join)
            {
                joined[^1] = last with { End = Math.Max(last.End ?? last.Frame, m.End ?? m.Frame), Strength = Math.Max(last.Strength, m.Strength) };
                continue;
            }

            joined.Add(m);
        }

        List<Moment> result = [.. moments.Where(m => !gone.Contains(m))];
        result.AddRange(joined);
        return result;
    }

    /// <summary>
    /// A pan, push or zoom of a whole graphic layout has no clean front: it eases in, and cell after cell starts to
    /// change. It shows in the share of changed cells instead, which climbs from near nothing to a quarter of the
    /// picture and stays up. The share is taken as the lowest of three frames running, so footage that holds frames
    /// (it changes on isolated frames) does not count, and footage at the full rate never starts from near nothing.
    /// A move that begins with a cut or a transition already found is that cut or transition.
    /// </summary>
    private List<Moment> GlobalMoves(List<Moment> found)
    {
        List<Moment> moves = [];
        int n = _frames;
        if (n < 8)
            return moves;
        float[] share = new float[n];
        share[0] = _changedShare[0];
        share[n - 1] = _changedShare[n - 1];
        for (int t = 1; t < n - 1; t++)
            share[t] = Math.Min(_changedShare[t - 1], Math.Min(_changedShare[t], _changedShare[t + 1]));

        int look = Frames(GlobalMoveLookSeconds), sustain = Frames(GlobalMoveSustainSeconds), back = Frames(WalkBackSeconds), near = Frames(GlobalMoveNearSeconds);
        int[] taken = [.. found.Where(m => m.Type is VideoEventType.Transition or VideoEventType.CameraMoveStart).Select(m => m.Frame).Order()];
        int i = 1;
        while (i < n - 1)
        {
            int t = i++;
            if (share[t] < GlobalMoveOn || share[t - 1] >= GlobalMoveOn)
                continue;
            float low = float.MaxValue, high = 0;
            for (int k = Math.Max(0, t - look); k < t; k++)
                low = Math.Min(low, share[k]);
            int hi = Math.Min(n, t + look);
            for (int k = t; k < hi; k++)
                high = Math.Max(high, share[k]);
            if (low >= GlobalMoveLow || high < GlobalMoveHigh)
                continue;
            int above = 0;
            for (int k = t; k < Math.Min(n, t + 2 * sustain); k++)
                if (share[k] >= GlobalMoveOn)
                    above++;
            if (above < sustain)
                continue;

            int start = t;
            while (start - 1 >= 0 && t - (start - 1) <= back && share[start - 1] > GlobalMoveSeed)
                start--;
            int end = t, quiet = 0;
            while (end + 1 < n && quiet < 5)
            {
                end++;
                quiet = share[end] < GlobalMoveLow ? quiet + 1 : 0;
            }

            end -= quiet;
            i = end + 1;
            if (Near(taken, start, near))
                continue;
            moves.Add(new Moment(VideoEventType.CameraMoveStart, start, end, null, Math.Clamp(0.4 + high, 0.4, 1.0), 0.6));
        }

        return moves;
    }

    // ---- local flashes ---------------------------------------------------------------------------------------

    private List<Moment> Sparks()
    {
        List<Moment> sparks = [];
        int n = _frames;
        int maxFrames = Math.Max(2, Frames(SparkMaxSeconds));
        HashSet<int> cutFrames = [.. _cuts.Select(c => c.Frame)];
        const int regionPixels = Pixels / 9;
        int last = -10;
        for (int t = 3; t < n - 1; t++)
        {
            if (cutFrames.Contains(t) || cutFrames.Contains(t - 1) || t - last <= 3 || _flashFrames.Contains(t))
                continue;
            int bestJump = 0, bestRegion = -1;
            for (int r = 0; r < 9; r++)
            {
                int basis = Math.Max(_bright[(t - 1) * 9 + r], Math.Max(_bright[(t - 2) * 9 + r], _bright[(t - 3) * 9 + r]));
                int jump = _bright[t * 9 + r] - basis;
                if (jump < SparkPixels || basis > SparkMaxBaseShare * regionPixels)
                    continue;
                // a flash stands out of a scene that is not bright already; a white page is no place for one
                if (_regionLuma[(t - 1) * 9 + r] > SparkMaxRegionLuma)
                    continue;
                // a white page hovering around the level flickers by a level or two; a flash lifts its pixels by many
                if (_newBright[t * 9 + r] < SparkNewShare * jump)
                    continue;
                bool back = false;
                for (int k = t + 1; k <= Math.Min(n - 1, t + maxFrames); k++)
                {
                    if (_bright[k * 9 + r] <= basis + (1 - SparkReturn) * jump)
                    {
                        back = true;
                        break;
                    }
                }

                if (back && jump > bestJump)
                {
                    bestJump = jump;
                    bestRegion = r;
                }
            }

            if (bestRegion < 0)
                continue;
            VideoEventRegion region = new(Math.Round(bestRegion % 3 / 3.0, 4), Math.Round(bestRegion / 3 / 3.0, 4), 0.3333, 0.3333);
            sparks.Add(new Moment(VideoEventType.Flash, t, null, region, Math.Clamp(0.35 + bestJump / 200.0, 0.4, 1.0), 0.6));
            last = t;
        }

        return sparks;
    }

    // ---- one span of cells ------------------------------------------------------------------------------------

    private readonly record struct Episode(
        int Cell, int Start, int End, float LumaBefore, float LumaAfter, float EdgeBefore, float EdgeAfter, float Spread, float Peak, float Up, byte Mode, bool Intro);

    /// <summary>
    /// Reads the cells of frames [<paramref name="a"/>, <paramref name="b"/>) and records the bursts whose first frame lies in
    /// [<paramref name="emitFrom"/>, <paramref name="emitTo"/>).
    /// </summary>
    private void AnalyseSpan(int a, int b, bool startsWithCut, int emitFrom, int emitTo)
    {
        b = Math.Min(b, _frames);
        int n = b - a;
        if (n < 4 || a < _base)
            return;
        float[][] d = new float[n][];
        float[][] L = new float[n][], E = new float[n][];
        for (int t = 0; t < n; t++)
        {
            d[t] = (float[])_cellDiff[a - _base + t].Clone();
            L[t] = _cellLuma[a - _base + t];
            E[t] = _cellEdge[a - _base + t];
        }

        Array.Clear(d[0]);
        int first = 1;
        if (startsWithCut)
        {
            // a cut that lands between two frames leaves one blended frame: it belongs to the cut
            while (first < Math.Min(n, 3) && ShareAbove(d[first], ChangeLevel) >= SettleShare)
            {
                Array.Clear(d[first]);
                first++;
            }
        }

        // a cut inside a panel: the cells it replaced in one frame are not an animation
        foreach ((int cutFrame, double share, _) in _cuts)
        {
            int t = cutFrame - a;
            if (share >= CutShare || t <= 0 || t >= n)
                continue;
            List<int> cells = [];
            for (int c = 0; c < Cells; c++)
                if (d[t][c] > ChangeLevel)
                    cells.Add(c);
            if (cells.Count == 0)
                continue;
            int after = Math.Min(n - 1, t + 1);
            if (cutFrame >= emitFrom && cutFrame < emitTo)
            {
                // a letterboxed shot cut to another: the panel is the whole picture and the rest is bars
                bool plain = IsFullPicture(Math.Max(a + 1, cutFrame - Frames(PictureBeforeSeconds)), cutFrame - Frames(PictureGuardSeconds))
                    && IsFullPicture(cutFrame + Frames(PictureSettleSeconds), Math.Min(b - 1, cutFrame + Frames(PictureAfterSeconds)));
                AddCandidate(PanelCut(cells, a + t, L[t - 1], L[after], E[t - 1], E[after]) with { PlainCut = plain });
            }
            int still = 0;
            foreach (int c in cells)
            {
                d[t][c] = 0;
                if (t + 1 < n && d[t + 1][c] > ChangeLevel)
                    still++;
            }

            if (t + 1 < n && still >= SettleShare * cells.Count)
                foreach (int c in cells)
                    d[t + 1][c] = 0;
        }

        byte[][] mode = Modes(d, n);
        int window = Frames(AmbientSeconds);
        int warm = startsWithCut ? Frames(IntroSeconds) : 0;
        int gap = Math.Max(2, Frames(GapSeconds));
        int maxLength = Frames(MaxBurstSeconds);
        int join = Math.Max(2, Frames(JoinSeconds));
        int seed = Math.Max(join, Frames(SeedSeconds));

        List<Episode> episodes = [];
        double[] scratch = new double[window];
        for (int c = 0; c < Cells; c++)
        {
            int t = first;
            while (t < n)
            {
                if (d[t][c] <= ChangeLevel)
                {
                    t++;
                    continue;
                }

                byte m = mode[t][c];
                double ambient = m == 2 ? PastQuantile(d, c, t, window, scratch) : 0.0;
                if (!Started(d, c, t, n, m, ambient))
                {
                    t++;
                    continue;
                }

                int s = t, e = t;
                for (int k = s + 1; k < n && k - e <= gap && k - s < maxLength; k++)
                    if (Started(d, c, k, n, m, ambient))
                        e = k;

                int lb0 = Math.Max(0, s - StateFrames), la1 = Math.Min(n, e + StateFrames + 1);
                float lumaBefore = s > lb0 ? Mean(L, c, lb0, s) : L[s][c];
                float edgeBefore = s > lb0 ? Mean(E, c, lb0, s) : E[s][c];
                float lumaAfter = la1 > e + 1 ? Mean(L, c, e + 1, la1) : L[e][c];
                float edgeAfter = la1 > e + 1 ? Mean(E, c, e + 1, la1) : E[e][c];
                float spread = s - lb0 >= 3 ? Deviation(L, c, lb0, s, lumaBefore) : 0f;
                float peak = 0, up = float.NegativeInfinity;
                for (int k = s; k <= e; k++)
                {
                    float dev = L[k][c] - lumaBefore;
                    peak = Math.Max(peak, Math.Abs(dev));
                    up = Math.Max(up, dev);
                }

                episodes.Add(new Episode(c, s, e, lumaBefore, lumaAfter, edgeBefore, edgeAfter, spread, peak, up, m, s < warm));
                t = e + gap + 1;
            }
        }

        if (episodes.Count == 0)
            return;

        foreach (List<Episode> group in Group(episodes, join, seed))
        {
            MomentCandidate? candidate = Summarise(group, a, n, d, L, E, first, warm);
            if (candidate is not null && candidate.Frame >= emitFrom && candidate.Frame < emitTo)
                AddCandidate(candidate);
        }
    }

    private void AddCandidate(MomentCandidate candidate)
    {
        if (_candidates.Count >= MaxCandidates)
        {
            _candidatesTruncated = true;
            return;
        }

        _candidates.Add(candidate);
    }

    private static double ShareAbove(float[] row, float level)
    {
        int count = 0;
        for (int c = 0; c < Cells; c++)
            if (row[c] > level)
                count++;
        return count / (double)Cells;
    }

    private static float Mean(float[][] rows, int cell, int from, int to)
    {
        double sum = 0;
        for (int k = from; k < to; k++)
            sum += rows[k][cell];
        return (float)(sum / (to - from));
    }

    private static float Deviation(float[][] rows, int cell, int from, int to, float mean)
    {
        double sum = 0;
        for (int k = from; k < to; k++)
            sum += (rows[k][cell] - mean) * (rows[k][cell] - mean);
        return (float)Math.Sqrt(sum / (to - from));
    }

    private static double PastQuantile(float[][] d, int cell, int t, int window, double[] scratch)
    {
        int lo = Math.Max(0, t - window);
        int count = t - lo;
        if (count < 1)
            return 0;
        for (int k = 0; k < count; k++)
            scratch[k] = d[lo + k][cell];
        return Quantile(scratch, count, AmbientQuantile);
    }

    /// <summary>Length (capped at <see cref="RunFrames"/>) of the run of changed frames around <paramref name="t"/>.</summary>
    private static int RunAt(float[][] d, int cell, int t, int n)
    {
        if (d[t][cell] <= ChangeLevel)
            return 0;
        int lo = t;
        while (lo - 1 >= 0 && t - lo < RunFrames && d[lo - 1][cell] > ChangeLevel)
            lo--;
        int hi = t;
        while (hi + 1 < n && hi - lo + 1 < RunFrames && d[hi + 1][cell] > ChangeLevel)
            hi++;
        return hi - lo + 1;
    }

    /// <summary>Mode 0: still surroundings. 1: inside footage that holds frames. 2: inside footage or animation at the full rate.</summary>
    private static bool Started(float[][] d, int cell, int t, int n, byte mode, double ambient) => mode switch
    {
        0 => d[t][cell] > ChangeLevel,
        1 => RunAt(d, cell, t, n) >= RunFrames,
        _ => d[t][cell] > ChangeLevel + FullRateFactor * ambient && RunAt(d, cell, t, n) >= RunFrames
    };

    /// <summary>
    /// Per frame and cell, where footage plays: 1 inside the rectangle of cells that flicker on isolated frames (held
    /// cadence), 2 inside the rectangle of cells that change on nearly every frame, 0 elsewhere. The window is the past
    /// <see cref="AmbientSeconds"/>; at the start of a span, where there is no past yet, its first seconds.
    /// </summary>
    private byte[][] Modes(float[][] d, int n)
    {
        int window = Frames(AmbientSeconds);
        byte[][] mode = new byte[n][];
        int[] isolated = new int[Cells], active = new int[Cells];
        bool[] held = new bool[Cells], full = new bool[Cells];
        int lo = 0, hi = 0;

        bool Active(int t, int c) => t >= 0 && t < n && d[t][c] > FlickerLevel;
        void Add(int t, int sign)
        {
            for (int c = 0; c < Cells; c++)
            {
                if (!Active(t, c))
                    continue;
                active[c] += sign;
                if (!Active(t - 1, c) && !Active(t + 1, c))
                    isolated[c] += sign;
            }
        }

        for (int t = 0; t < n; t++)
        {
            int wantHi = Math.Min(n, Math.Max(t, Math.Min(n, window)));
            int wantLo = Math.Max(0, wantHi - window);
            while (hi < wantHi)
                Add(hi++, +1);
            while (lo < wantLo)
                Add(lo++, -1);
            int span = Math.Max(1, wantHi - wantLo);
            int heldCount = 0, fullCount = 0;
            for (int c = 0; c < Cells; c++)
            {
                held[c] = isolated[c] >= IsolatedShare * span;
                full[c] = active[c] >= FullRateShare * span;
                if (held[c])
                    heldCount++;
                if (full[c])
                    fullCount++;
            }

            byte[] row = new byte[Cells];
            if (heldCount >= PanelMinCells)
                FillPanel(held, row, 1);
            if (fullCount >= PanelMinCells)
                FillPanel(full, row, 2);
            mode[t] = row;
        }

        return mode;
    }

    /// <summary>Marks the rectangle spanned by the rows and columns that hold at least two member cells.</summary>
    private static void FillPanel(bool[] member, byte[] row, byte value)
    {
        Span<int> perRow = stackalloc int[CellsY];
        Span<int> perColumn = stackalloc int[CellsX];
        for (int c = 0; c < Cells; c++)
        {
            if (!member[c])
                continue;
            perRow[c / CellsX]++;
            perColumn[c % CellsX]++;
        }

        int y0 = -1, y1 = -1, x0 = -1, x1 = -1;
        for (int y = 0; y < CellsY; y++)
        {
            if (perRow[y] < 2)
                continue;
            if (y0 < 0)
                y0 = y;
            y1 = y;
        }

        for (int x = 0; x < CellsX; x++)
        {
            if (perColumn[x] < 2)
                continue;
            if (x0 < 0)
                x0 = x;
            x1 = x;
        }

        if (y0 < 0 || x0 < 0)
            return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                row[y * CellsX + x] = value;
    }

    /// <summary>Groups bursts into wavefronts, then lets a bigger group absorb the small seed it grew from. Deterministic order.</summary>
    private static List<List<Episode>> Group(List<Episode> episodes, int join, int seed)
    {
        int count = episodes.Count;
        int[] parent = new int[count];
        for (int i = 0; i < count; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        List<int>?[] byCell = new List<int>?[Cells];
        for (int i = 0; i < count; i++)
            (byCell[episodes[i].Cell] ??= []).Add(i);

        for (int i = 0; i < count; i++)
        {
            Episode a = episodes[i];
            int x = a.Cell % CellsX, y = a.Cell / CellsX;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= CellsX || yy >= CellsY)
                        continue;
                    List<int>? bucket = byCell[yy * CellsX + xx];
                    if (bucket is null)
                        continue;
                    foreach (int j in bucket)
                        if (j > i && Math.Abs(a.Start - episodes[j].Start) <= join)
                            parent[Find(i)] = Find(j);
                }
            }
        }

        Dictionary<int, List<Episode>> byRoot = [];
        for (int i = 0; i < count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<Episode>? list))
                byRoot[root] = list = [];
            list.Add(episodes[i]);
        }

        List<(List<Episode> Items, int Start, int End, HashSet<int> CellSet)> groups = [.. byRoot.Values
            .Select(g => (Items: g, Start: g.Min(e => e.Start), End: g.Max(e => e.End), CellSet: g.Select(e => e.Cell).ToHashSet()))
            .OrderBy(g => g.Start).ThenBy(g => g.Items.Min(e => e.Cell))];
        bool[] gone = new bool[groups.Count];
        int from = 0;
        for (int b = 0; b < groups.Count; b++)
        {
            while (from < b && groups[b].Start - groups[from].Start > seed)
                from++;
            for (int a = from; a < b; a++)
            {
                if (gone[a])
                    continue;
                var (itemsA, startA, endA, cellsA) = groups[a];
                var (itemsB, startB, endB, cellsB) = groups[b];
                if (startB - startA <= 0 || startB > endA + 1 || 3 * cellsA.Count > cellsB.Count || !Touches(cellsA, cellsB))
                    continue;
                cellsB.UnionWith(cellsA);
                groups[b] = ([.. itemsA, .. itemsB], startA, Math.Max(endA, endB), cellsB);
                gone[a] = true;
                break;
            }
        }

        List<List<Episode>> result = [];
        for (int g = 0; g < groups.Count; g++)
            if (!gone[g])
                result.Add(groups[g].Items);
        return result;
    }

    private static bool Touches(HashSet<int> a, HashSet<int> b)
    {
        foreach (int c in a)
        {
            int x = c % CellsX, y = c / CellsX;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && yy >= 0 && xx < CellsX && yy < CellsY && b.Contains(yy * CellsX + xx))
                        return true;
                }
            }
        }

        return false;
    }

    private MomentCandidate? Summarise(List<Episode> group, int a, int n, float[][] d, float[][] L, float[][] E, int first, int warm)
    {
        int count = group.Count;
        int significant = 0, pulses = 0, still = 0;
        double energy = 0, pulseUp = 0;
        int s = int.MaxValue, e = int.MinValue;
        int x0 = CellsX, y0 = CellsY, x1 = -1, y1 = -1;
        bool anyIntro = false;
        HashSet<int> cellSet = [];
        bool[] isSignificant = new bool[count];
        for (int i = 0; i < count; i++)
        {
            Episode q = group[i];
            double dL = q.LumaAfter - q.LumaBefore, dE = q.EdgeAfter - q.EdgeBefore;
            bool sig = Math.Abs(dL) > SignificantLuma + 2 * q.Spread || Math.Abs(dE) > SignificantEdge;
            bool pulse = !sig && q.Peak > PulsePeak + 2 * q.Spread;
            isSignificant[i] = sig;
            if (sig)
            {
                significant++;
                energy += Math.Abs(dL) + 4 * Math.Abs(dE);
            }
            else if (pulse)
            {
                pulses++;
                energy += q.Peak;
                pulseUp += q.Up;
            }

            if (q.Mode == 0)
                still++;
            anyIntro |= q.Intro;
            s = Math.Min(s, q.Start);
            e = Math.Max(e, q.End);
            int x = q.Cell % CellsX, y = q.Cell / CellsX;
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            y1 = Math.Max(y1, y);
            cellSet.Add(q.Cell);
        }

        if (significant + pulses == 0)
            return null;

        // walk a slow start back: the cells were already moving a little before the first one crossed the bar
        int back = Frames(WalkBackSeconds);
        int s0 = s;
        while (s0 - 1 >= first && s - (s0 - 1) <= back && MeanOver(d[s0 - 1], cellSet) > WalkBackLevel)
            s0--;

        // how much of the picture changes at the same time
        int[] occupancy = new int[e - s + 2];
        foreach (Episode q in group)
        {
            occupancy[q.Start - s]++;
            occupancy[q.End - s + 1]--;
        }

        int running = 0, peakOccupancy = 0;
        for (int k = 0; k < occupancy.Length; k++)
        {
            running += occupancy[k];
            peakOccupancy = Math.Max(peakOccupancy, running);
        }

        (double sweepX, double sweepY, double sweepFit) = PlaneFit(group);
        double grow = GrowFit(group, s);

        MomentKind kind = MomentKind.Pulse;
        double edgeBefore = 0, edgeAfter = 0;
        foreach (Episode q in group)
        {
            edgeBefore += q.EdgeBefore;
            edgeAfter += q.EdgeAfter;
        }

        if (significant > 0)
        {
            int fb = Math.Max(0, s - 1), fa = Math.Min(n - 1, e + 1);
            List<Episode> sig = [];
            for (int i = 0; i < count; i++)
                if (isSignificant[i])
                    sig.Add(group[i]);
            kind = ContentKind(sig, BackgroundLevel(L[fb], E[fb]), BackgroundLevel(L[fa], E[fa]));
        }

        return new MomentCandidate(
            a + s0, a + e, a + s, cellSet.Count,
            x0 / (double)CellsX, y0 / (double)CellsY, (x1 + 1) / (double)CellsX, (y1 + 1) / (double)CellsY, kind,
            significant, pulses, still / (double)count, energy, peakOccupancy / (double)Cells, edgeBefore, edgeAfter,
            pulses > 0 ? pulseUp / pulses : 0, sweepX, sweepY, sweepFit, grow, anyIntro && s < warm, Panel: false);
    }

    private static double MeanOver(float[] row, HashSet<int> cells)
    {
        double sum = 0;
        foreach (int c in cells)
            sum += row[c];
        return sum / cells.Count;
    }

    private MomentCandidate PanelCut(List<int> cells, int frame, float[] lumaBefore, float[] lumaAfter, float[] edgeBefore, float[] edgeAfter)
    {
        int x0 = CellsX, y0 = CellsY, x1 = -1, y1 = -1;
        double eb = 0, ea = 0;
        List<Episode> states = [];
        foreach (int c in cells)
        {
            int x = c % CellsX, y = c / CellsX;
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            y1 = Math.Max(y1, y);
            eb += edgeBefore[c];
            ea += edgeAfter[c];
            states.Add(new Episode(c, 0, 0, lumaBefore[c], lumaAfter[c], edgeBefore[c], edgeAfter[c], 0, 0, 0, 0, false));
        }

        MomentKind kind = ContentKind(states, BackgroundLevel(lumaBefore, edgeBefore), BackgroundLevel(lumaAfter, edgeAfter), byDetail: false);
        return new MomentCandidate(
            frame, frame, frame, cells.Count,
            x0 / (double)CellsX, y0 / (double)CellsY, (x1 + 1) / (double)CellsX, (y1 + 1) / (double)CellsY, kind,
            cells.Count, 0, 1.0, double.MaxValue, cells.Count / (double)Cells, eb, ea, 0, 0, 0, 0, 0, Intro: false, Panel: true);
    }

    /// <summary>Level of the flat background of one frame: the fullest 16-level bin of the flat cells, or null when the frame has none.</summary>
    private static double? BackgroundLevel(float[] luma, float[] edge)
    {
        Span<int> histogram = stackalloc int[16];
        int flat = 0;
        for (int c = 0; c < Cells; c++)
        {
            if (edge[c] >= BlankEdge)
                continue;
            flat++;
            histogram[Math.Min(15, (int)(luma[c] / 16))]++;
        }

        if (flat < 0.15 * Cells)
            return null;
        int best = 0;
        for (int b = 1; b < 16; b++)
            if (histogram[b] > histogram[best])
                best = b;
        double sum = 0;
        int n = 0;
        for (int c = 0; c < Cells; c++)
        {
            if (edge[c] >= BlankEdge || Math.Min(15, (int)(luma[c] / 16)) != best)
                continue;
            sum += luma[c];
            n++;
        }

        return sum / n;
    }

    /// <summary>What happened to the content of a set of cells: it appeared on empty background, left, moved, or was replaced.</summary>
    private static MomentKind ContentKind(List<Episode> cells, double? backgroundBefore, double? backgroundAfter, bool byDetail = true)
    {
        int appear = 0, disappear = 0;
        double gain = 0, loss = 0, edgeBefore = 0, edgeAfter = 0;
        foreach (Episode q in cells)
        {
            bool emptyBefore = backgroundBefore is { } bb && q.EdgeBefore < BlankEdge && Math.Abs(q.LumaBefore - bb) < BlankLuma;
            bool emptyAfter = backgroundAfter is { } ba && q.EdgeAfter < BlankEdge && Math.Abs(q.LumaAfter - ba) < BlankLuma;
            if (emptyBefore && !emptyAfter)
                appear++;
            if (!emptyBefore && emptyAfter)
                disappear++;
            double dE = q.EdgeAfter - q.EdgeBefore;
            gain += Math.Max(0, dE);
            loss += Math.Max(0, -dE);
            edgeBefore += q.EdgeBefore;
            edgeAfter += q.EdgeAfter;
        }

        double appearShare = appear / (double)cells.Count, disappearShare = disappear / (double)cells.Count;
        if (appearShare >= MajorShare && disappearShare < MinorShare)
            return MomentKind.Appear;
        if (disappearShare >= MajorShare && appearShare < MinorShare)
            return MomentKind.Disappear;
        if (appearShare >= MinorShare && disappearShare >= MinorShare)
            return MomentKind.Move;
        if (!byDetail)
            return MomentKind.Change;
        // not on empty background, but the detail of the cells grew a lot: something was added to what is there
        double ratio = gain / (gain + loss + 1e-6);
        if (ratio >= EdgeGainShare && edgeAfter >= EdgeGrowth * edgeBefore && disappearShare < MinorShare)
            return MomentKind.Appear;
        // losing detail means leaving only where there is no flat background to compare with: a flat shape that covers
        // a photo also loses detail, and that is something arriving
        if (backgroundBefore is null && backgroundAfter is null && ratio <= 1 - EdgeGainShare && edgeBefore >= EdgeGrowth * edgeAfter)
            return MomentKind.Disappear;

        return MomentKind.Change;
    }

    /// <summary>Least squares start ~ a + b*x + c*y over the group's cells: the slope (frames per cell) and how much it explains.</summary>
    private static (double B, double C, double Fit) PlaneFit(List<Episode> group)
    {
        int n = group.Count;
        if (n < 6)
            return (0, 0, 0);
        int min = int.MaxValue, max = int.MinValue;
        double mx = 0, my = 0, mt = 0;
        foreach (Episode q in group)
        {
            min = Math.Min(min, q.Start);
            max = Math.Max(max, q.Start);
            mx += q.Cell % CellsX;
            my += q.Cell / CellsX;
            mt += q.Start;
        }

        if (max - min < 3)
            return (0, 0, 0);
        mx /= n;
        my /= n;
        mt /= n;
        double sxx = 0, sxy = 0, syy = 0, sxt = 0, syt = 0, stt = 0;
        foreach (Episode q in group)
        {
            double dx = q.Cell % CellsX - mx, dy = q.Cell / CellsX - my, dt = q.Start - mt;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
            sxt += dx * dt;
            syt += dy * dt;
            stt += dt * dt;
        }

        if (stt < 1e-9)
            return (0, 0, 0);
        double det = sxx * syy - sxy * sxy;
        if (det < 1e-9)
        {
            if (sxx > 1e-9)
            {
                double slope = sxt / sxx;
                return (slope, 0, Math.Clamp(slope * sxt / stt, 0, 1));
            }

            if (syy > 1e-9)
            {
                double slope = syt / syy;
                return (0, slope, Math.Clamp(slope * syt / stt, 0, 1));
            }

            return (0, 0, 0);
        }

        double b = (sxt * syy - syt * sxy) / det, c = (syt * sxx - sxt * sxy) / det;
        return (b, c, Math.Clamp((b * sxt + c * syt) / stt, 0, 1));
    }

    /// <summary>Correlation of a cell's start with its distance from the first cells: near 1 when the event grows outward from a seed.</summary>
    private static double GrowFit(List<Episode> group, int start)
    {
        int n = group.Count;
        if (n < 6)
            return 0;
        int max = group.Max(q => q.Start);
        if (max - start < 3)
            return 0;
        double ex = 0, ey = 0;
        int early = 0;
        foreach (Episode q in group)
        {
            if (q.Start > start + 1)
                continue;
            ex += q.Cell % CellsX;
            ey += q.Cell / CellsX;
            early++;
        }

        ex /= early;
        ey /= early;
        double[] radius = new double[n];
        double mr = 0, mt = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = (group[i].Cell % CellsX - ex) / CellsX, dy = (group[i].Cell / CellsX - ey) / CellsY;
            radius[i] = Math.Sqrt(dx * dx + dy * dy);
            mr += radius[i];
            mt += group[i].Start;
        }

        mr /= n;
        mt /= n;
        double srr = 0, stt = 0, srt = 0;
        for (int i = 0; i < n; i++)
        {
            double dr = radius[i] - mr, dt = group[i].Start - mt;
            srr += dr * dr;
            stt += dt * dt;
            srt += dr * dt;
        }

        return srr < 1e-12 || stt < 1e-12 ? 0 : srt / Math.Sqrt(srr * stt);
    }

    // ---- naming -----------------------------------------------------------------------------------------------

    private sealed record Moment(
        VideoEventType Type, int Frame, int? End, VideoEventRegion? Region, double Strength, double Confidence,
        string? Direction = null, string? Speed = null, bool Soft = false, double Area = 0, bool InStillSurroundings = false,
        string? Kind = null)
    {
        public VideoEventType Type { get; set; } = Type;
        public double Strength { get; set; } = Strength;

        public VideoEvent ToEvent() =>
            new(Type, Frame, End is { } end && end > Frame ? end : null, Region, Strength, VideoEventOrigin.Pixels, Confidence, Direction, Speed, Kind);
    }

    private Moment? Classify(MomentCandidate ev)
    {
        int duration = ev.End - ev.FirstCross + 1;
        double w = ev.X1 - ev.X0, h = ev.Y1 - ev.Y0;
        if (!ev.Panel)
        {
            int minCells = ev.Still >= 0.8 ? MinCellsStill : MinCellsLive;
            if (ev.Cells < minCells || ev.Energy < MinEnergy)
                return null;
        }

        string? direction = null, speed = null;
        if (ev.SweepFit >= 0.5 && Math.Abs(ev.SweepX) + Math.Abs(ev.SweepY) > 1e-6)
        {
            // start = a + bx*x + by*y (frames per cell), so the front travels along the gradient at 1/|gradient| cells per frame
            double g2 = ev.SweepX * ev.SweepX + ev.SweepY * ev.SweepY;
            double vx = ev.SweepX / g2 / CellsX * _fps, vy = ev.SweepY / g2 / CellsY * _fps;
            direction = VideoEventWords.DirectionWord(vx, vy);
            speed = VideoEventWords.SpeedWord(Math.Sqrt(vx * vx + vy * vy));
        }

        VideoEventRegion region = new(Math.Round(ev.X0, 4), Math.Round(ev.Y0, 4), Math.Round(w, 4), Math.Round(h, 4));
        double strength = Math.Clamp(0.2 + Math.Sqrt(ev.Area / 0.06) * 0.5, 0.2, 1.0);
        double confidence = ev.Still >= 0.8 ? 0.7 : 0.6;

        if (ev.Area >= GlobalArea && ev.PeakShare >= GlobalPeak)
            return new Moment(VideoEventType.Transition, ev.Frame, ev.End, null, strength, 0.8, direction, speed, Soft: duration > 2);

        if (ev.Kind == MomentKind.Pulse || ev.Pulses > 2 * ev.Significant)
        {
            // something that passes through lights its cells one after another; a flash lights them together
            if (direction is not null && duration >= 4)
                return new Moment(VideoEventType.MoveStart, ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
            if (ev.PulseUp >= 12 && duration <= Frames(0.3))
                return new Moment(VideoEventType.Flash, ev.Frame, ev.End, region, strength, 0.6);
            return null;
        }

        if (ev.Panel && ev.Kind is MomentKind.Change or MomentKind.Move && ev.Area >= PanelCutArea)
            return new Moment(VideoEventType.Transition, ev.Frame, null, region, strength, 0.85, Kind: ev.PlainCut ? VideoEventKinds.Footage : null);

        double trueAspect = w * _aspectW / Math.Max(1e-6, h * _aspectH);
        bool textShaped = trueAspect >= TextAspect && h <= TextMaxHeight;
        if (ev.Grow >= 0.5 && duration >= 3 && direction is null)
            direction = "grow";

        switch (ev.Kind)
        {
            case MomentKind.Appear:
                return new Moment(textShaped && ev.EdgeAfter / ev.Cells >= TextEdge ? VideoEventType.TextIn : VideoEventType.Appear,
                    ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
            case MomentKind.Disappear:
                return new Moment(textShaped && ev.EdgeBefore / ev.Cells >= TextEdge ? VideoEventType.TextOut : VideoEventType.Disappear,
                    ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
            case MomentKind.Move when direction is not null || duration >= 4:
                return new Moment(VideoEventType.MoveStart, ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
            case MomentKind.Change when direction == "grow":
                // content was there before (footage, a photo), and something spread over it from one spot
                return new Moment(VideoEventType.Appear, ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
            default:
                return new Moment(VideoEventType.Change, ev.Frame, ev.End, region, strength, confidence, direction, speed, Area: ev.Area, InStillSurroundings: ev.Still >= 0.8);
        }
    }

    /// <summary>
    /// Small changes that keep coming back in one band are the words of running captions: a rhythm of the edit, not
    /// moments. A title that shows once, or a change elsewhere, keeps its type.
    /// </summary>
    private void TagCaptions(List<Moment> moments)
    {
        List<Moment> small = [.. moments
            .Where(m => m.Region is { } r && r.H <= TextMaxHeight && m.Area > 0 && m.Area <= CaptionMaxArea
                && m.Type is VideoEventType.TextIn or VideoEventType.TextOut or VideoEventType.Change or VideoEventType.Appear or VideoEventType.Disappear)
            .OrderBy(m => m.Frame)];
        double window = CaptionWindowSeconds * _fps;
        bool[] tick = new bool[small.Count];
        int lo = 0;
        for (int i = 0; i < small.Count; i++)
        {
            Moment e = small[i];
            while (lo < i && e.Frame - small[lo].Frame > window)
                lo++;
            double cy = e.Region!.Y + e.Region.H / 2, cx = e.Region.X + e.Region.W / 2;
            int neighbours = 0;
            for (int j = lo; j < small.Count && small[j].Frame - e.Frame <= window; j++)
            {
                if (j == i || Math.Abs(small[j].Frame - e.Frame) < 2)
                    continue;
                VideoEventRegion o = small[j].Region!;
                if (Math.Abs(o.Y + o.H / 2 - cy) <= CaptionDy && Math.Abs(o.X + o.W / 2 - cx) <= CaptionDx)
                    neighbours++;
            }

            tick[i] = neighbours >= CaptionMinNeighbours;
        }

        for (int i = 0; i < small.Count; i++)
        {
            if (!tick[i])
                continue;
            small[i].Type = VideoEventType.CaptionTick;
            small[i].Strength = Math.Min(small[i].Strength, CaptionStrength);
        }
    }
}
