using System.Text;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// What an assistant turn did before it stopped without a reply, kept so the thread can say so.
/// </summary>
/// <remarks>
/// The model's reply only exists once its whole tool loop has finished, so there is no half-written text to
/// save when a turn is cut short (its wall clock, nobody coming back to a turn, an error). What there IS, and what
/// a reader needs, is the record of the actions it took: each permission card and where it ended. The thread gets
/// that as an ordinary assistant message, so a dropped turn never leaves a question with no answer and never
/// leaves a change nobody told the user about.
/// </remarks>
public sealed class AssistantInterruptedTurn
{
    private readonly object _gate = new();
    private readonly List<Guid> _order = [];
    private readonly Dictionary<Guid, (string Title, string State)> _actions = [];

    /// <summary>True once the turn has shown at least one action card.</summary>
    public bool HasActions
    {
        get
        {
            lock (_gate)
            {
                return _order.Count > 0;
            }
        }
    }

    /// <summary>Records the newest state of an action card (the same frames the chat stream carries).</summary>
    public void Note(AssistantActionFrame frame)
    {
        lock (_gate)
        {
            if (!_actions.ContainsKey(frame.Id))
                _order.Add(frame.Id);

            _actions[frame.Id] = (frame.Title, frame.State);
        }
    }

    /// <summary>The plain-language assistant message for a turn that stopped early.</summary>
    /// <param name="wallClockFired">True when the turn ran past its hard time limit rather than being left unattended.</param>
    public string Compose(bool wallClockFired)
    {
        var text = new StringBuilder(wallClockFired
            ? "This took too long, so I stopped before I could finish."
            : "This answer was interrupted before I could finish it.");

        lock (_gate)
        {
            if (_order.Count == 0)
            {
                text.Append(" Nothing was changed. Ask me again and I will pick it up.");
                return text.ToString();
            }

            text.Append(" Here is what happened before it stopped:");
            foreach (Guid id in _order)
            {
                (string title, string state) = _actions[id];
                text.Append("\n- ").Append(title).Append(": ").Append(Describe(state));
            }
        }

        text.Append("\nAsk me to carry on if you want me to continue.");
        return text.ToString();
    }

    private static string Describe(string state) => state switch
    {
        AssistantActionState.Done => "done.",
        AssistantActionState.Failed => "it didn't go through.",
        AssistantActionState.Declined => "you chose not to allow it, so nothing was changed.",
        AssistantActionState.Expired => "no answer was given, so nothing was changed.",
        AssistantActionState.Allowed => "it was allowed and started, but I could not confirm it finished. Please check.",
        _ => "it was waiting for your OK and was not done."
    };
}
