namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// A single-channel float image plus its central-difference gradients — one level of an
/// <see cref="ImagePyramid"/>. Float rather than byte so pyramid levels and bilinear samples keep
/// sub-intensity precision, which Lucas-Kanade's least squares depends on.
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    private float[]? _gx;
    private float[]? _gy;

    public GrayImage(int width, int height, float[] pixels)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("Pixel buffer does not match dimensions.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>One frame of a packed 8-bit gray buffer.</summary>
    public static GrayImage FromGray8(byte[] buffer, int offset, int width, int height)
    {
        var pixels = new float[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = buffer[offset + i];
        return new GrayImage(width, height, pixels);
    }

    /// <summary>Horizontal gradient (Scharr), computed once on first use.</summary>
    public float[] GradX => _gx ??= ComputeGradients().Gx;

    /// <summary>Vertical gradient (Scharr), computed once on first use.</summary>
    public float[] GradY => _gy ??= ComputeGradients().Gy;

    private (float[] Gx, float[] Gy) ComputeGradients()
    {
        // Scharr 3x3, divided by 32 so the gradient is in intensity units per pixel. Scharr rather
        // than Sobel for its better rotational symmetry — LK's normal matrix is direction-sensitive.
        var gx = new float[Pixels.Length];
        var gy = new float[Pixels.Length];
        int w = Width, h = Height;
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                float tl = Pixels[ym * w + xm], tc = Pixels[ym * w + x], tr = Pixels[ym * w + xp];
                float ml = Pixels[y * w + xm], mr = Pixels[y * w + xp];
                float bl = Pixels[yp * w + xm], bc = Pixels[yp * w + x], br = Pixels[yp * w + xp];
                gx[y * w + x] = (3 * (tr - tl) + 10 * (mr - ml) + 3 * (br - bl)) / 32f;
                gy[y * w + x] = (3 * (bl - tl) + 10 * (bc - tc) + 3 * (br - tr)) / 32f;
            }
        }

        _gx = gx;
        _gy = gy;
        return (gx, gy);
    }

    /// <summary>Bilinear sample with edge clamping.</summary>
    public float Sample(double x, double y) => SampleOf(Pixels, x, y);

    public float SampleGradX(double x, double y) => SampleOf(GradX, x, y);

    public float SampleGradY(double x, double y) => SampleOf(GradY, x, y);

    private float SampleOf(float[] data, double x, double y)
    {
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x > Width - 1) x = Width - 1;
        if (y > Height - 1) y = Height - 1;
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        float a = data[y0 * Width + x0], b = data[y0 * Width + x1];
        float c = data[y1 * Width + x0], d = data[y1 * Width + x1];
        return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
    }

    /// <summary>Half-resolution copy after a separable [1 4 6 4 1]/16 Gaussian — the standard pyramid reduce.</summary>
    public GrayImage Reduce()
    {
        int w = Width, h = Height;
        int nw = Math.Max(1, (w + 1) / 2), nh = Math.Max(1, (h + 1) / 2);
        var tmp = new float[nw * h];
        for (int y = 0; y < h; y++)
        {
            for (int nx = 0; nx < nw; nx++)
            {
                int x = nx * 2;
                tmp[y * nw + nx] =
                    (Pixels[y * w + Math.Clamp(x - 2, 0, w - 1)] +
                     4 * Pixels[y * w + Math.Clamp(x - 1, 0, w - 1)] +
                     6 * Pixels[y * w + x] +
                     4 * Pixels[y * w + Math.Clamp(x + 1, 0, w - 1)] +
                     Pixels[y * w + Math.Clamp(x + 2, 0, w - 1)]) / 16f;
            }
        }

        var outp = new float[nw * nh];
        for (int ny = 0; ny < nh; ny++)
        {
            int y = ny * 2;
            for (int x = 0; x < nw; x++)
            {
                outp[ny * nw + x] =
                    (tmp[Math.Clamp(y - 2, 0, h - 1) * nw + x] +
                     4 * tmp[Math.Clamp(y - 1, 0, h - 1) * nw + x] +
                     6 * tmp[y * nw + x] +
                     4 * tmp[Math.Clamp(y + 1, 0, h - 1) * nw + x] +
                     tmp[Math.Clamp(y + 2, 0, h - 1) * nw + x]) / 16f;
            }
        }

        return new GrayImage(nw, nh, outp);
    }
}

/// <summary>A Gaussian pyramid: level 0 is the full image, each next level half the size.</summary>
public sealed class ImagePyramid
{
    public IReadOnlyList<GrayImage> Levels { get; }

    public ImagePyramid(GrayImage baseImage, int levels)
    {
        var list = new List<GrayImage> { baseImage };
        for (int i = 1; i < Math.Max(1, levels); i++)
        {
            GrayImage last = list[^1];
            if (last.Width < 16 || last.Height < 16)
                break;
            list.Add(last.Reduce());
        }

        Levels = list;
    }
}
