namespace ReelBolt.Shared.Inference;

/// <summary>
/// What an ordered <see cref="VideoGenerationRequest.Images"/> list means to ONE video model: whether
/// it reads a first frame, and whether it also reads a SECOND, last frame.
/// <para>
/// The ordinal convention is the whole contract, and it is stated once here because three clients
/// share it: <c>Images[0]</c> is the FIRST frame and <c>Images[1]</c> — where a model accepts one — is
/// the LAST frame. There is no per-image role on <see cref="VideoGenerationImageInput"/> and none is
/// added: the list is what the caller already builds, and each vendor's own role/field name is
/// translated from the index by the client that documents it.
/// </para>
/// <para>
/// Append-only: a new level is a new member at the end, never a renumbering.
/// </para>
/// </summary>
public enum VideoImageInputSupport
{
    /// <summary>No image input at all: a request carrying images cannot be carried by this model.</summary>
    None = 0,

    /// <summary>A first frame only (<c>Images[0]</c>); a last frame is not part of the model's input.</summary>
    FirstFrame = 1,

    /// <summary>A first frame (<c>Images[0]</c>) and a last frame (<c>Images[1]</c>).</summary>
    FirstAndLastFrame = 2
}

/// <summary>
/// A video generation client that can answer, offline and without spending anything, what one of ITS
/// models accepts as input images.
/// <para>
/// <b>Deliberately not a member of <see cref="IVideoGenerationClient"/>.</b> That interface is frozen
/// and every fake in the suite is built against it — the same reasoning
/// <see cref="IVideoGenerationPriceOracle"/> records for the same decision. This one is narrower than
/// the price oracle: it is a pure declaration, so a client answers it from the same model switch its
/// <see cref="IVideoGenerationClient.Describe"/> already uses and can never need the network.
/// </para>
/// <para>
/// The answer is what makes a text-only model refuse BEFORE the budget gate reserves anything: the
/// caller that holds a resolved client can ask this immediately after resolving the provider and
/// before any ledger row exists, rather than discovering the mismatch at submit time, after a
/// reservation. The clients ALSO enforce their own answer at submit time, so a caller that skips the
/// pre-check still cannot make a model carry an image it does not document.
/// </para>
/// </summary>
public interface IVideoImageInputSupport
{
    /// <summary>
    /// How many input images this client's <paramref name="model"/> accepts, and which of them it
    /// reads. Pure: no network call, ever.
    /// </summary>
    /// <param name="model">The provider-specific model identity, exactly as a request carries it.</param>
    /// <exception cref="NotSupportedException">The client has no such model.</exception>
    /// <exception cref="ArgumentException">The value is not a usable model identity for this client.</exception>
    VideoImageInputSupport ImageInputSupport(string model);
}

/// <summary>
/// The ONE rule that turns a declared <see cref="VideoImageInputSupport"/> plus a request's image count
/// into an answer, so every vendor refuses the same request for the same reason and the reason names
/// the model rather than the client.
/// </summary>
public static class VideoImageInput
{
    /// <summary>The most images any supported model reads: one first frame and one last frame.</summary>
    public const int MaxImages = 2;

    /// <summary>
    /// What <paramref name="model"/> accepts according to the client, or
    /// <see cref="VideoImageInputSupport.None"/> when the client declares nothing.
    /// <para>
    /// A client that does not implement <see cref="IVideoImageInputSupport"/> declares nothing, and
    /// "nothing declared" is answered as <see cref="VideoImageInputSupport.None"/> rather than as an
    /// assumption: the closed set of video generation kinds is MiniMax, Higgsfield and ComfyUI and all
    /// three declare, so the fallback only ever describes a client that genuinely said nothing. It is
    /// fail-closed on purpose — a caller checking before it reserves must not invent support for an
    /// unknown provider — and it is never a claim about the vendor.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public static VideoImageInputSupport DeclaredImageInputSupport(this IVideoGenerationClient client, string model)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client is IVideoImageInputSupport declared
            ? declared.ImageInputSupport(model)
            : VideoImageInputSupport.None;
    }

    /// <summary>
    /// The pre-reserve guard: throws unless <paramref name="model"/> accepts exactly
    /// <paramref name="imageCount"/> images. Sends no request and reserves nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="imageCount"/> is negative, or exceeds <see cref="MaxImages"/>.</exception>
    /// <exception cref="NotSupportedException">The model's declared support cannot carry these images.</exception>
    public static void EnsureAccepts(this IVideoGenerationClient client, string model, int imageCount)
    {
        ArgumentNullException.ThrowIfNull(client);
        Validate(client.DeclaredImageInputSupport(model), model, imageCount);
    }

    /// <summary>
    /// The rule itself, pure: a zero count is always acceptable, a count above <see cref="MaxImages"/>
    /// never is, an unordered model accepts no images, and a first-frame-only model accepts no second
    /// image. Both exception types are the ones a step executor already maps to a configuration
    /// failure, so the caller needs no new catch.
    /// </summary>
    /// <param name="support">What the model declared.</param>
    /// <param name="model">The model identity, named in the message.</param>
    /// <param name="imageCount">How many images the request carries.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="imageCount"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="imageCount"/> exceeds <see cref="MaxImages"/>.</exception>
    /// <exception cref="NotSupportedException">The declared support cannot carry <paramref name="imageCount"/> images.</exception>
    public static void Validate(VideoImageInputSupport support, string model, int imageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(imageCount);

        if (imageCount == 0)
        {
            return;
        }

        string named = string.IsNullOrWhiteSpace(model) ? "(no model)" : $"'{model}'";

        if (imageCount > MaxImages)
        {
            throw new ArgumentException(
                $"This request carries {imageCount} input images, but at most {MaxImages} are ever sent: " +
                "Images[0] is the first frame and Images[1] is the last frame. Extra images are not a " +
                "shape any supported video model documents.",
                nameof(imageCount));
        }

        if (support == VideoImageInputSupport.None)
        {
            throw new NotSupportedException(
                $"{named} accepts no input image: this request carries {imageCount}. The model declares " +
                "no image input (its documented input is text only), so it is a text-only model for this " +
                "request and the images cannot be sent. Use a model that accepts a first frame " +
                "(FirstFrame) or a first and a last frame (FirstAndLastFrame).");
        }

        if (imageCount == MaxImages && support != VideoImageInputSupport.FirstAndLastFrame)
        {
            throw new NotSupportedException(
                $"{named} accepts a first frame only (FirstFrame): this request carries {MaxImages} " +
                "images, and its second image would be a last frame, which this model does not document. " +
                "Remove the last frame or use a model that accepts one.");
        }
    }
}
