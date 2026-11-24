namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>One frame's whole-picture motion: how far the content shifted (X right, Y down) and spread from the centre.</summary>
internal readonly record struct CameraShift(float X, float Y, float Zoom);

/// <summary>A velocity reversal or abrupt stop of a tracked moving blob: the frame just before it, where, how fast.</summary>
internal readonly record struct BlobImpact(int Frame, float X, float Y, float SpeedBefore);

/// <summary>Everything the single pass over the frames leaves behind, small enough to keep for the whole video.</summary>
internal sealed class FrameAnalysis
{
    public int FrameCount { get; init; }
    /// <summary>Per frame: magnitude of the global motion fit in 160 px units, whether or not the frame looks camera-driven.</summary>
    public required List<float> CameraMagnitude { get; init; }
    /// <summary>Per frame: the global motion is a camera move (it covers the frame and explains the change).</summary>
    public required List<bool> CameraDriven { get; init; }
    /// <summary>
    /// Per frame: the signed parts of the same fit, which way the picture's content went since the frame before.
    /// X is to the right, Y is down (both in pixels of the 160 px grid); Zoom is positive when the content spreads
    /// out from the centre (in pixels, 80 px from the centre). The camera goes the other way from X and Y.
    /// </summary>
    public required List<CameraShift> CameraShifts { get; init; }
    public required List<BlobImpact> Impacts { get; init; }
}

/// <summary>
/// Single-pass pixel analysis over the 160x90 gray frames: only the previous frame is held, never the video.
/// Per frame it measures
/// <list type="bullet">
/// <item>per cell (5x5 px, 32x18): the temporal difference and a local translation fit;</item>
/// <item>per frame: a robust global motion fit (translation and zoom) for camera moves;</item>
/// <item>per moving blob: velocity reversals and abrupt stops (impacts).</item>
/// </list>
/// It reads only what <see cref="EditMomentAnalyzer"/> cannot: the camera and the impacts of a tracked object.
/// <see cref="VisualEventClassifier"/> turns the two traces into events.
/// All thresholds are global constants for gray values in 0..255 on the 160x90 grid; none depends on a clip.
/// </summary>
internal sealed class FrameEventAnalyzer : IFrameConsumer
{
    private const int W = FrameSignalSampler.Width;
    private const int H = FrameSignalSampler.Height;
    internal const int CellPx = 5;
    internal const int CellsX = W / CellPx;
    internal const int CellsY = H / CellPx;
    private const int Cells = CellsX * CellsY;
    private const float CellArea = CellPx * CellPx;

    /// <summary>A cell counts as moving (for blob tracking) when it changes by this much and a translation explains it.</summary>
    private const float MovingDiff = 0.3f;
    private const float MovingExplained = 0.25f;
    /// <summary>Camera: share of cells that changed, minimum global motion, minimum explained variance (a fade explains ~0).</summary>
    private const float CameraCoverage = 0.6f;
    private const float CameraMinMotion = 0.01f;
    private const float CameraMinExplained = 0.06f;
    private const float CoverageDiff = 0.02f;
    /// <summary>Hard cap on recorded impacts: a pathologically busy source stops recording instead of growing without bound.</summary>
    private const int DefaultMaxImpacts = 250_000;

    /// <summary>The impact cap in force; only tests lower it.</summary>
    internal int ImpactCap { get; init; } = DefaultMaxImpacts;

    private readonly float[] _prev = new float[W * H];
    private readonly float[] _cur = new float[W * H];
    private readonly float[] _it = new float[W * H];
    private readonly float[] _ix = new float[W * H];
    private readonly float[] _iy = new float[W * H];
    private readonly float[] _xn = new float[W * H];
    private readonly float[] _yn = new float[W * H];
    private readonly float[] _weight = new float[W * H];
    private readonly int[] _residualHistogram = new int[2001];

    private readonly float[] _cd = new float[Cells];
    private readonly float[] _sxx = new float[Cells], _sxy = new float[Cells], _syy = new float[Cells];
    private readonly float[] _sxt = new float[Cells], _syt = new float[Cells], _stt = new float[Cells];
    private readonly float[] _u = new float[Cells], _v = new float[Cells], _rho = new float[Cells];

    private readonly List<float> _cameraMagnitude = [];
    private readonly List<bool> _cameraDriven = [];
    private readonly List<CameraShift> _cameraShifts = [];
    private readonly List<BlobImpact> _impacts = [];
    private List<Blob> _blobs = [];
    private int _frames;

    public FrameEventAnalyzer()
    {
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                _xn[y * W + x] = (x - W / 2f + 0.5f) / 80f;
                _yn[y * W + x] = (y - H / 2f + 0.5f) / 80f;
            }
        }
    }

    public void OnFrame(int index, ReadOnlySpan<byte> gray)
    {
        _frames = index + 1;
        for (int i = 0; i < _cur.Length; i++)
            _cur[i] = gray[i];

        if (index == 0)
        {
            Array.Copy(_cur, _prev, _cur.Length);
            _cameraMagnitude.Add(0f);
            _cameraDriven.Add(false);
            _cameraShifts.Add(default);
            return;
        }

        MeasureCells();
        float cameraMagnitude = MeasureCamera(out bool cameraDriven, out CameraShift cameraShift);
        _cameraMagnitude.Add(cameraMagnitude);
        _cameraDriven.Add(cameraDriven);
        _cameraShifts.Add(cameraShift);
        LocalTranslation();
        TrackBlobs(index, cameraDriven);
        Array.Copy(_cur, _prev, _cur.Length);
    }

    public FrameAnalysis Complete() => new()
    {
        FrameCount = _frames,
        CameraMagnitude = _cameraMagnitude,
        CameraDriven = _cameraDriven,
        CameraShifts = _cameraShifts,
        Impacts = _impacts
    };

    // ---- per-cell and per-pixel measurements ------------------------------------------------------------

    private void MeasureCells()
    {
        Array.Clear(_cd);
        Array.Clear(_sxx);
        Array.Clear(_sxy);
        Array.Clear(_syy);
        Array.Clear(_sxt);
        Array.Clear(_syt);
        Array.Clear(_stt);

        // Pass A: temporal difference (kept per pixel) and the averaged image the gradients are taken on.
        for (int i = 0; i < _cur.Length; i++)
            _it[i] = _cur[i] - _prev[i];

        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            int cellRow = y / CellPx * CellsX;
            for (int x = 0; x < W; x++)
            {
                int i = row + x;
                float gx = 0f, gy = 0f;
                if (x > 0 && x < W - 1)
                    gx = ((_cur[i + 1] + _prev[i + 1]) - (_cur[i - 1] + _prev[i - 1])) * 0.25f;
                if (y > 0 && y < H - 1)
                    gy = ((_cur[i + W] + _prev[i + W]) - (_cur[i - W] + _prev[i - W])) * 0.25f;
                _ix[i] = gx;
                _iy[i] = gy;

                int c = cellRow + x / CellPx;
                float it = _it[i];
                _cd[c] += MathF.Abs(it);
                _sxx[c] += gx * gx;
                _sxy[c] += gx * gy;
                _syy[c] += gy * gy;
                _sxt[c] += gx * it;
                _syt[c] += gy * it;
                _stt[c] += it * it;
            }
        }

        for (int c = 0; c < Cells; c++)
        {
            _cd[c] /= CellArea;
            _sxx[c] /= CellArea;
            _sxy[c] /= CellArea;
            _syy[c] /= CellArea;
            _sxt[c] /= CellArea;
            _syt[c] /= CellArea;
            _stt[c] /= CellArea;
        }
    }

    /// <summary>
    /// Robust global motion by gradient-based least squares: u = tx + s*(x - cx), v = ty + s*(y - cy), coordinates in
    /// units of 80 px. Pixels whose residual is large (moving objects, uncovered background) are re-weighted out over
    /// three passes. Returns |tx| + |ty| + |s|, the displacement of a frame corner in 160 px units, and the signed
    /// parts in <paramref name="shift"/> (all zero when the fit could not be solved).
    /// </summary>
    private float MeasureCamera(out bool cameraDriven, out CameraShift shift)
    {
        cameraDriven = false;
        shift = default;
        int n = _it.Length;
        double tx = 0, ty = 0, s = 0;
        Array.Fill(_weight, 1f);

        for (int pass = 0; pass < 3; pass++)
        {
            double a00 = 1e-3, a01 = 0, a02 = 0, a11 = 1e-3, a12 = 0, a22 = 1e-3, b0 = 0, b1 = 0, b2 = 0;
            for (int i = 0; i < n; i++)
            {
                float w = _weight[i];
                if (w <= 0f)
                    continue;
                double g0 = _ix[i], g1 = _iy[i], g2 = _ix[i] * _xn[i] + _iy[i] * _yn[i];
                double y = -_it[i];
                a00 += w * g0 * g0;
                a01 += w * g0 * g1;
                a02 += w * g0 * g2;
                a11 += w * g1 * g1;
                a12 += w * g1 * g2;
                a22 += w * g2 * g2;
                b0 += w * g0 * y;
                b1 += w * g1 * y;
                b2 += w * g2 * y;
            }

            if (!Solve3(a00, a01, a02, a11, a12, a22, b0, b1, b2, out tx, out ty, out s))
                return 0f;

            Array.Clear(_residualHistogram);
            int counted = 0;
            for (int i = 0; i < n; i++)
            {
                if (_weight[i] <= 0f)
                    continue;
                float res = MathF.Abs(_it[i] + (float)(_ix[i] * tx + _iy[i] * ty + (_ix[i] * _xn[i] + _iy[i] * _yn[i]) * s));
                _residualHistogram[Math.Min(2000, (int)(res * 50f))]++;
                counted++;
            }

            float median = 0.001f;
            int seen = 0;
            for (int b = 0; b < _residualHistogram.Length; b++)
            {
                seen += _residualHistogram[b];
                if (seen * 2 >= counted)
                {
                    median = (b + 0.5f) / 50f;
                    break;
                }
            }

            float limit = 2.5f * median + 0.5f;
            for (int i = 0; i < n; i++)
            {
                float res = MathF.Abs(_it[i] + (float)(_ix[i] * tx + _iy[i] * ty + (_ix[i] * _xn[i] + _iy[i] * _yn[i]) * s));
                float a = MathF.Abs(_ix[i]) + MathF.Abs(_iy[i]) + MathF.Abs(_ix[i] * _xn[i] + _iy[i] * _yn[i]);
                _weight[i] = res < limit && a > 0.5f ? 1f : 0f;
            }
        }

        float magnitude = (float)(Math.Abs(tx) + Math.Abs(ty) + Math.Abs(s));
        shift = new CameraShift((float)tx, (float)ty, (float)s);

        double total = 0, residual = 0;
        for (int i = 0; i < n; i++)
        {
            float it = _it[i];
            float pred = (float)(_ix[i] * tx + _iy[i] * ty + (_ix[i] * _xn[i] + _iy[i] * _yn[i]) * s);
            total += it * it;
            residual += (it + pred) * (it + pred);
        }

        float explained = total > 1e-9 ? (float)(1 - residual / total) : 0f;
        int covered = 0;
        for (int c = 0; c < Cells; c++)
            if (_cd[c] > CoverageDiff)
                covered++;

        cameraDriven = magnitude > CameraMinMotion && covered >= CameraCoverage * Cells && explained >= CameraMinExplained;
        return magnitude;
    }

    private static bool Solve3(
        double a00, double a01, double a02, double a11, double a12, double a22, double b0, double b1, double b2,
        out double x0, out double x1, out double x2)
    {
        double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
        x0 = x1 = x2 = 0;
        if (Math.Abs(det) < 1e-12)
            return false;
        x0 = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
        x1 = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
        x2 = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
        return true;
    }

    /// <summary>Per cell: translation (u, v) from the gradients of the 3x3 cells around it, and the share of the change it explains.</summary>
    private void LocalTranslation()
    {
        for (int cy = 0; cy < CellsY; cy++)
        {
            for (int cx = 0; cx < CellsX; cx++)
            {
                float sxx = 0, sxy = 0, syy = 0, sxt = 0, syt = 0, stt = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = cy + dy;
                    if (yy < 0 || yy >= CellsY)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = cx + dx;
                        if (xx < 0 || xx >= CellsX)
                            continue;
                        int k = yy * CellsX + xx;
                        sxx += _sxx[k];
                        sxy += _sxy[k];
                        syy += _syy[k];
                        sxt += _sxt[k];
                        syt += _syt[k];
                        stt += _stt[k];
                    }
                }

                const float reg = 1f;
                float a = sxx + reg, b = syy + reg;
                float det = a * b - sxy * sxy;
                int c = cy * CellsX + cx;
                float u = -(b * sxt - sxy * syt) / det;
                float v = -(a * syt - sxy * sxt) / det;
                float res = stt + 2 * (u * sxt + v * syt) + u * u * sxx + 2 * u * v * sxy + v * v * syy;
                _u[c] = u;
                _v[c] = v;
                _rho[c] = stt > 1e-3f ? Math.Clamp(1 - res / stt, 0f, 1f) : 0f;
            }
        }
    }

    // ---- moving blobs: velocity reversals and abrupt stops -----------------------------------------------

    private sealed record Blob(float X, float Y, float Vx, float Vy);

    private readonly int[] _label = new int[Cells];
    private readonly int[] _queue = new int[Cells];

    private void TrackBlobs(int t, bool cameraDriven)
    {
        List<Blob> current = [];
        if (!cameraDriven)
        {
            Array.Clear(_label);
            for (int seed = 0; seed < Cells; seed++)
            {
                if (_label[seed] != 0 || !IsMoving(seed))
                    continue;
                int head = 0, tail = 0;
                _queue[tail++] = seed;
                _label[seed] = 1;
                float mass = 0, sx = 0, sy = 0, su = 0, sv = 0;
                int count = 0;
                while (head < tail)
                {
                    int k = _queue[head++];
                    float w = _cd[k];
                    int kx = k % CellsX, ky = k / CellsX;
                    mass += w;
                    sx += w * kx;
                    sy += w * ky;
                    su += w * _u[k];
                    sv += w * _v[k];
                    count++;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = kx + dx, ny = ky + dy;
                            if (nx < 0 || ny < 0 || nx >= CellsX || ny >= CellsY)
                                continue;
                            int nk = ny * CellsX + nx;
                            if (_label[nk] == 0 && IsMoving(nk))
                            {
                                _label[nk] = 1;
                                _queue[tail++] = nk;
                            }
                        }
                    }
                }

                if (count >= 2 && mass > 0)
                    current.Add(new Blob(sx / mass, sy / mass, su / mass, sv / mass));
            }
        }

        foreach (Blob blob in current)
        {
            Blob? best = null;
            float bestDistance = 4f;
            foreach (Blob previous in _blobs)
            {
                float distance = MathF.Sqrt((blob.X - previous.X) * (blob.X - previous.X) + (blob.Y - previous.Y) * (blob.Y - previous.Y));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = previous;
                }
            }

            if (best is null)
                continue;

            float before = MathF.Sqrt(best.Vx * best.Vx + best.Vy * best.Vy);
            float after = MathF.Sqrt(blob.Vx * blob.Vx + blob.Vy * blob.Vy);
            float dot = best.Vx * blob.Vx + best.Vy * blob.Vy;
            // Reversal: moving at 1 px/frame or more, then the opposite way. Abrupt stop: fast, then under 40% of the speed.
            bool reversal = before >= 1.0f && dot < -0.3f * before * after && after >= 0.4f;
            bool stop = before >= 1.5f && after < 0.4f * before;
            if ((reversal || stop) && _impacts.Count < ImpactCap)
                _impacts.Add(new BlobImpact(t - 1, blob.X, blob.Y, before));
        }

        _blobs = current;
    }

    private bool IsMoving(int c) => _cd[c] > MovingDiff && _rho[c] > MovingExplained;
}
