namespace ReelBolt.Shared.Inference;

/// <summary>
/// Marker for an <see cref="ISpeechSynthesisClient"/> decorator (metering, ...). A decorator must NOT
/// claim <see cref="IVoiceCloningClient"/> / <see cref="IVoiceLibraryClient"/> itself: callers use an
/// <c>as</c> test to learn whether the vendor supports them. They reach the vendor client through
/// <see cref="SpeechSynthesisClientUnwrapExtensions.Unwrap{T}"/> instead.
/// </summary>
public interface IWrappedSpeechSynthesisClient
{
    ISpeechSynthesisClient Inner { get; }
}

public static class SpeechSynthesisClientUnwrapExtensions
{
    private const int MaxDepth = 16;

    /// <summary>The client itself if it is a <typeparamref name="T"/>, else the first <typeparamref name="T"/> down the wrapper chain; null if none.</summary>
    public static T? Unwrap<T>(this ISpeechSynthesisClient? client) where T : class
    {
        ISpeechSynthesisClient? current = client;
        for (int depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            if (current is T match)
                return match;
            current = (current as IWrappedSpeechSynthesisClient)?.Inner;
        }

        return null;
    }
}
