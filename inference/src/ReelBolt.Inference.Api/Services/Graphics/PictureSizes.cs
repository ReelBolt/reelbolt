using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Graphics;

/// <summary>
/// The pixel sizes the server needs and does not store: a picture document's canvas when it has none of its
/// own, and a layer's bitmap size when its row does not record it (a <c>Source</c> or <c>Image</c> layer is
/// the user's own upload, and nothing measures an upload's pixels). Read from the image's header — a handful
/// of bytes, no raster library — and never trusted beyond being a size.
/// </summary>
public static class PictureSizes
{
    /// <summary>The picture size used when nothing says how big the canvas is.</summary>
    public const int FallbackWidth = 1280;

    public const int FallbackHeight = 720;

    /// <summary>How much of an image is read to find its pixel size (headers sit at the start).</summary>
    private const int HeaderReadBytes = 512 * 1024;

    /// <summary>
    /// The longest side a NEW picture's canvas gets from its photo (editor remediation W0.8): a bigger photo
    /// is scaled down, both sides together, so the aspect is kept and the browser never composites a 48 MP
    /// canvas.
    /// </summary>
    public const int MaxNewCanvasLongSide = 4096;

    /// <summary>
    /// The canvas a new picture opens at for a photo of <paramref name="width"/> x <paramref name="height"/>
    /// pixels, and the uniform scale that fits the photo's source layer into it: the photo's own size when
    /// its long side is at most <see cref="MaxNewCanvasLongSide"/>, else both sides scaled so the long side
    /// is exactly that (8000 x 6000 opens at 4096 x 3072 with the layer at scale 0.512). Both sides are then
    /// kept at least <see cref="EditImageDocumentSupport.MinCanvasSide"/>, the size a later PATCH accepts: a
    /// tiny or extreme-aspect photo is scaled up so its short side reaches it (long side still capped, so the
    /// aspect is kept as far as the bounds allow) and the layer's scale then covers the canvas (10 x 10 opens
    /// at 16 x 16, 10000 x 30 at 4096 x 16).
    /// </summary>
    public static (int CanvasWidth, int CanvasHeight, double Scale) FitNewCanvas(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        const int minSide = EditImageDocumentSupport.MinCanvasSide;
        int longSide = Math.Max(width, height);
        double scale = longSide <= MaxNewCanvasLongSide ? 1 : (double)MaxNewCanvasLongSide / longSide;
        int canvasWidth = ScaleSide(width, scale);
        int canvasHeight = ScaleSide(height, scale);
        if (canvasWidth >= minSide && canvasHeight >= minSide) return (canvasWidth, canvasHeight, scale);

        // Too small for a canvas: grow both sides until the short one reaches the minimum, the long one
        // never past the cap, then cover the canvas with the photo.
        double grow = (double)minSide / Math.Min(width, height);
        canvasWidth = Math.Clamp(ScaleSide(width, grow), minSide, MaxNewCanvasLongSide);
        canvasHeight = Math.Clamp(ScaleSide(height, grow), minSide, MaxNewCanvasLongSide);
        return (canvasWidth, canvasHeight, Math.Max((double)canvasWidth / width, (double)canvasHeight / height));

        static int ScaleSide(int side, double by) =>
            Math.Max(1, (int)Math.Round(side * by, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The size a stored photo is SHOWN at, read from its header (EXIF orientation applied, see
    /// <see cref="ImageHeader.TryReadDisplaySize"/>) without decoding it: the object is read in chunks, at most
    /// <see cref="ImageHeader.PrefixBytes"/>, and reading stops as soon as the header answers. <c>null</c> when
    /// the header is unreadable, the format unknown or the object missing; never throws for those.
    /// </summary>
    public static async Task<(int Width, int Height)?> MeasureDisplaySizeAsync(
        IFileStorageService storage, Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, storageKey, ct);
            byte[] head = new byte[ImageHeader.PrefixBytes];
            int total = 0;
            int read;
            while (total < head.Length && (read = await stream.ReadAsync(head.AsMemory(total, Math.Min(64 * 1024, head.Length - total)), ct)) > 0)
            {
                total += read;
                if (ImageHeader.TryReadDisplaySize(head.AsSpan(0, total), out int w, out int h) && w > 0 && h > 0)
                    return (w, h);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The document's canvas: its own stored size, else the editor's hint, else the source layer's size, else
    /// 1280 x 720.
    /// </summary>
    public static async Task<(int Width, int Height)> CanvasAsync(
        IFileStorageService storage, Guid projectId, EditImageDocument document, int? hintWidth, int? hintHeight, CancellationToken ct)
    {
        if (document.CanvasWidth is { } cw && document.CanvasHeight is { } ch)
            return (cw, ch);
        if (hintWidth is { } hw && hintHeight is { } hh)
            return (hw, hh);

        EditImageLayer? source = document.Layers.FirstOrDefault(l => l.Kind == EditImageLayerKind.Source);
        if (source is not null && await LayerAsync(storage, projectId, source, ct) is { } size)
            return size;

        return (FallbackWidth, FallbackHeight);
    }

    /// <summary>A bitmap layer's pixel size: its recorded size, else its image header; null for a text layer or an unreadable image.</summary>
    public static async Task<(int Width, int Height)?> LayerAsync(
        IFileStorageService storage, Guid projectId, EditImageLayer layer, CancellationToken ct)
    {
        if (layer.Width is { } w && layer.Height is { } h && w > 0 && h > 0)
            return (w, h);
        if (layer.Kind == EditImageLayerKind.Text || string.IsNullOrWhiteSpace(layer.StorageKey))
            return null;

        return await HeaderSizeAsync(storage, projectId, layer.StorageKey, ct);
    }

    /// <summary>
    /// The refusal for upscaling <paramref name="layer"/>, or null when it may run. The source's pixels are the larger of
    /// its recorded size and the size its header declares (the same rule the look sizer applies). A source whose size
    /// cannot be established (no recorded size and no readable header), or whose recorded size is not positive, is
    /// refused: the upscale is priced at the preset's bound, so an unknown size cannot be allowed (fail closed). Read
    /// before any provider is chosen, hold is taken or byte is sent.
    /// </summary>
    public static async Task<string?> UpscaleRefusalAsync(
        IFileStorageService storage, Guid projectId, EditImageLayer layer, CancellationToken ct)
    {
        (int Width, int Height)? recorded = null;
        if (layer.Width is not null || layer.Height is not null)
        {
            if (layer.Width is not { } w || layer.Height is not { } h || w <= 0 || h <= 0)
                return UpscaleSourceLimit.UnreadableMessage;
            recorded = (w, h);
        }

        (int Width, int Height)? header = null;
        if (layer.Kind != EditImageLayerKind.Text && !string.IsNullOrWhiteSpace(layer.StorageKey))
            header = await HeaderSizeAsync(storage, projectId, layer.StorageKey, ct);

        (int Width, int Height)? size = (recorded, header) switch
        {
            ({ } r, { } hd) => (long)r.Width * r.Height >= (long)hd.Width * hd.Height ? r : hd,
            ({ } r, null) => r,
            (null, { } hd) => hd,
            _ => null,
        };
        if (size is not { } known)
            return UpscaleSourceLimit.UnreadableMessage;

        return UpscaleSourceLimit.Allows(known.Width, known.Height) ? null : UpscaleSourceLimit.TooLargeMessage;
    }

    /// <summary>A stored image's pixel size from its header; null when the object cannot be read or its header declares no size.</summary>
    public static async Task<(int Width, int Height)?> HeaderSizeAsync(
        IFileStorageService storage, Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, storageKey, ct);
            byte[] head = new byte[HeaderReadBytes];
            int total = 0;
            int read;
            while (total < head.Length && (read = await stream.ReadAsync(head.AsMemory(total), ct)) > 0)
                total += read;
            return ImageHeader.TryReadSize(head.AsSpan(0, total), out int width, out int height) && width > 0 && height > 0
                ? (width, height)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
