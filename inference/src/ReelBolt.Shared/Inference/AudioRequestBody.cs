using System.Globalization;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the JSON body of one audio request from the typed request and the endpoint's catalogue entry, refusing
/// anything the endpoint does not declare and every out-of-range value BEFORE a request is sent. Shared by the fal
/// and the direct ElevenLabs clients, whose fields are the same vendor's.
/// </summary>
internal static class AudioRequestBody
{
    /// <summary>The body, plus the <c>output_format</c> token to send (null when the endpoint has none).</summary>
    /// <exception cref="ArgumentException">The request breaks the endpoint's schema.</exception>
    public static (Dictionary<string, object?> Body, string? OutputFormat) Build(AudioEndpoint ep, AudioGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(ep);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != ep.Kind)
        {
            throw Refuse(ep, "generates " + ep.Kind + ", but the request asks for " + request.Kind);
        }

        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        bool hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt);

        if (request.CompositionPlanJson is { } planJson)
        {
            if (ep.PlanShape == AudioPlanShape.None) throw Refuse(ep, "documents no composition plan field");
            if (hasPrompt)
            {
                throw Refuse(ep, "takes a prompt OR a composition plan, never both (the vendor documents them as " +
                                 "mutually exclusive)");
            }

            if (request.DurationSec is not null)
            {
                throw Refuse(ep, "takes its length from the composition plan's section durations, so a duration " +
                                 "must not be set as well");
            }

            if (request.Instrumental is not null)
            {
                throw Refuse(ep, "honours instrumental only with a prompt, not with a composition plan");
            }

            (JsonElement plan, double totalSec) = AudioCompositionPlan.Validate(planJson, ep.PlanShape, ep.Id);
            RequireDuration(ep, totalSec, "the composition plan's total duration");
            body["composition_plan"] = plan;
        }
        else
        {
            if (!hasPrompt)
            {
                throw Refuse(ep, ep.PlanShape == AudioPlanShape.None
                    ? "needs a prompt"
                    : "needs a prompt or a composition plan");
            }

            if (ep.PromptMaxLength is { } max && request.Prompt!.Length > max)
            {
                throw Refuse(ep, "takes a prompt of at most " + max + " characters; it is " + request.Prompt.Length);
            }

            body[ep.PromptField] = request.Prompt;

            if (request.DurationSec is { } seconds)
            {
                RequireDuration(ep, seconds, "the duration");
                body[ep.DurationField] = ep.DurationInMilliseconds
                    ? (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)
                    : seconds;
            }
            else if (ep.DurationRequired)
            {
                throw Refuse(ep, "needs a duration from " + Num(ep.MinDurationSec) + " to " + Num(ep.MaxDurationSec) +
                                 " seconds: an unset length is the vendor's choice and cannot be priced");
            }

            if (request.Instrumental is { } instrumental)
            {
                if (ep.InstrumentalField is null) throw Refuse(ep, "documents no instrumental field");
                body[ep.InstrumentalField] = instrumental;
            }
        }

        if (request.Loop is { } loop)
        {
            if (ep.LoopField is null) throw Refuse(ep, "documents no loop field");
            body[ep.LoopField] = loop;
        }

        if (request.PromptInfluence is { } influence)
        {
            if (ep.PromptInfluenceField is null) throw Refuse(ep, "documents no prompt influence field");
            if (influence is < 0 or > 1 || double.IsNaN(influence))
            {
                throw Refuse(ep, "takes a prompt influence from 0 to 1; got " + Num(influence));
            }

            body[ep.PromptInfluenceField] = influence;
        }

        string? format = null;
        if (ep.HasOutputFormat)
        {
            format = request.OutputFormat ?? AudioOutputFormats.Default;
            if (!AudioOutputFormats.IsOffered(format)) throw Refuse(ep, AudioOutputFormats.Refusal(format));
        }
        else if (request.OutputFormat is not null)
        {
            throw Refuse(ep, "documents no output format field (it returns " +
                             (ep.FixedFileType?.Extension ?? "its own format") + ")");
        }

        return (body, format);
    }

    /// <summary>The file type a finished request produces: the endpoint's fixed type, or the one its format names.</summary>
    public static (string MimeType, string Extension) FileTypeOf(AudioEndpoint ep, string? format) =>
        ep.FixedFileType ?? AudioOutputFormats.FileTypeOf(format ?? AudioOutputFormats.Default);

    private static void RequireDuration(AudioEndpoint ep, double seconds, string what)
    {
        if (double.IsNaN(seconds) || seconds < ep.MinDurationSec || seconds > ep.MaxDurationSec)
        {
            throw Refuse(ep, "takes a duration from " + Num(ep.MinDurationSec) + " to " + Num(ep.MaxDurationSec) +
                             " seconds; " + what + " is " + Num(seconds));
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static ArgumentException Refuse(AudioEndpoint ep, string why) =>
        new("'" + ep.Id + "' " + why + ". Nothing was sent.", "request");
}
