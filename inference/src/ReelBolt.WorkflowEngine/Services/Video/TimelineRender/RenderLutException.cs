using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>A LUT file the timeline references that the <c>.cube</c> parser refuses; carries which file so the step can name it.</summary>
public sealed class RenderLutException : Exception
{
    public RenderLutException(AssetRef file, CubeLutException reason)
        : base(reason.Message, reason)
    {
        File = file;
        Reason = reason;
    }

    public AssetRef File { get; }

    public CubeLutException Reason { get; }
}
