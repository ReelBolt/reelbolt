using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// The <c>Agents:&lt;name&gt;:DecisionGate</c> configuration name for a decision-gate site, kept in
/// exactly one place, and the one non-throwing reader of the block's <c>Mode</c>.
///
/// <para>
/// Two independent callers need this mapping and they must never disagree about which config block
/// to read: <c>AgentStepExecutor</c> resolves the <c>Mode</c>/<c>AcceptAt</c>/<c>MinMargin</c>/
/// <c>ProviderId</c> the gate actually runs with, and <c>StepCachePolicy</c> asks the same question
/// about the same block — "is this step's decision mode <see cref="DecisionGateMode.Off"/>?" — to
/// decide whether the step may be served from the step-result cache. A second copy of the mapping
/// could point the two at different blocks, and the policy would then happily cache a step the
/// executor does gate (or refuse to cache one it does not) — the exact class of disagreement the
/// policy rule exists to close.
/// </para>
///
/// <para>
/// The same argument applies to the <b>value</b> read, not just the key: <see cref="ReadMode"/> is
/// the single place either caller may turn that string into a <see cref="DecisionGateMode"/>. When
/// each caller held its own <c>Configuration.GetValue&lt;DecisionGateMode?&gt;</c> the two could
/// drift, and both inherited that call's throw.
/// </para>
/// </summary>
public static class DecisionGateAgentConfig
{
    /// <summary>
    /// The <c>Agents:&lt;name&gt;:DecisionGate</c> config name for an agent type, or <c>null</c>
    /// when that agent type is not a decision-gate site at all. Callers must treat <c>null</c> as
    /// "no gate here", never as a licence to guess a name.
    /// </summary>
    public static string? ConfigNameFor(AgentType agentType) => agentType switch
    {
        AgentType.Colorist => "Colorist",
        AgentType.MusicSupervisor => "MusicSupervisor",
        _ => null,
    };

    /// <summary>The full configuration key <see cref="ReadMode"/> reads for a gate site.</summary>
    public static string ModeKeyFor(string configName) => $"Agents:{configName}:DecisionGate:Mode";

    /// <summary>
    /// The configured decision-gate mode for a site, or <see cref="DecisionGateMode.Off"/> when the
    /// key is absent, blank, or not a string this enum parses.
    ///
    /// <para>
    /// <b>Never throws, for any input.</b> Deliberately not
    /// <c>Configuration.GetValue&lt;DecisionGateMode?&gt;</c>: that call throws
    /// <see cref="InvalidOperationException"/> (wrapped from the type converter, for a non-empty
    /// unparseable string) and a throw out of this configuration read marked the step <c>Failed</c>
    /// and aborted the whole execution — <c>AgentStepExecutor.RunPreAgentGateAsync</c> resolved the
    /// mode <i>before</i> its own <c>try</c> block, and <c>WorkflowExecutorService</c> excludes
    /// <see cref="InvalidOperationException"/> from retry. <c>StepCachePolicy</c> held the identical
    /// call, where it degraded to "no cache" instead (both its call sites sit inside a
    /// <c>catch (Exception)</c>). A typo in one appsettings key must be inert: no provider call, the
    /// agent runs, and the step completes exactly as phase 1 did — never a failed run and never a
    /// silently different cacheability between the two readers.
    /// </para>
    ///
    /// <para>
    /// <b>What it accepts.</b> Parsing is <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// with <c>ignoreCase: true</c>, which is the enum converter's own semantics — so this is not a
    /// behaviour change for any value that previously bound successfully. Case and surrounding
    /// whitespace are tolerated (<c>"gate"</c>, <c>"GATE"</c> and <c>" Gate "</c> all resolve to
    /// <see cref="DecisionGateMode.Gate"/>, i.e. a lowercase <c>gate</c> still <b>arms</b> the gate,
    /// exactly as before), and a numeric string binds by ordinal position (<c>"2"</c> is
    /// <see cref="DecisionGateMode.Gate"/>). An <b>out-of-range</b> number (<c>"99"</c>) also still
    /// parses, to an undefined enum value — kept deliberately, because
    /// <see cref="DecisionGateMode"/>'s exhaustive switch already promises that an unrecognised
    /// <i>value</i> means "do nothing", and narrowing it to <see cref="DecisionGateMode.Off"/> here
    /// would change the step-cache answer for a value that is inert at every other layer.
    /// </para>
    ///
    /// <para>
    /// The only outcome that changes is the one that used to throw: a non-blank string that names no
    /// member and is not a number resolves to <see cref="DecisionGateMode.Off"/> and logs a warning
    /// naming the offending key and value, so a typo is discoverable without being fatal. An absent
    /// or blank value is the ordinary "not configured" case and is silent. Without
    /// <paramref name="logger"/> the warning is simply not emitted — never a throw.
    /// </para>
    /// </summary>
    /// <param name="configuration">
    /// The host configuration, or <c>null</c> when a caller has none. Null reads as an absent key,
    /// which is <see cref="DecisionGateMode.Off"/>.
    /// </param>
    /// <param name="configName">A <see cref="ConfigNameFor"/> result, i.e. <c>"Colorist"</c> or <c>"MusicSupervisor"</c>.</param>
    /// <param name="logger">Optional. Passed by both callers so neither path is the silent one.</param>
    public static DecisionGateMode ReadMode(
        IConfiguration? configuration, string configName, ILogger? logger = null)
    {
        string key = ModeKeyFor(configName);
        string? configured = configuration?[key];

        // Absent or blank is the normal local case — the shipped appsettings keeps every
        // DecisionGate example commented out — and it is silent.
        if (string.IsNullOrWhiteSpace(configured))
            return DecisionGateMode.Off;

        if (Enum.TryParse(configured, ignoreCase: true, out DecisionGateMode mode))
            return mode;

        logger?.LogWarning(
            "Unrecognised decision-gate mode '{ConfiguredMode}' at configuration key '{ConfigKey}'; treating it as {FallbackMode} (no decision provider will be called). Valid values are {ValidValues}.",
            configured,
            key,
            nameof(DecisionGateMode.Off),
            string.Join(", ", Enum.GetNames<DecisionGateMode>()));

        return DecisionGateMode.Off;
    }
}
