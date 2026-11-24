namespace ReelBolt.WorkflowEngine.Services.Video;

using System;
using ReelBolt.Shared.Workflows;

/// <summary>
/// The REAL author of one agent-authored timeline track: the <c>agent_definitions</c> row that
/// produced the upstream step's decision, the inference provider that step resolved to, and that
/// step's own newest <c>WorkflowStepResult</c> id.
///
/// <para>
/// Every field is a fact read from the execution's own step graph and database rows — never a
/// label inferred from which <see cref="ReelBolt.Shared.Workflows.TimelineItem"/> fields happen
/// to be populated, which is exactly the fabricated-provenance defect
/// <c>TimelineItem.AgentName</c>/<c>ProviderName</c>/<c>StepResultId</c> exist to remove
/// (issue #123 §1). The three facts are independent, so a track may legitimately carry some and
/// not others: a step result row that has not been persisted yet loses only
/// <see cref="StepResultId"/>, and a provider-resolution failure loses only
/// <see cref="ProviderName"/>.
/// </para>
/// </summary>
/// <param name="AgentDefinitionId">The <c>agent_definitions</c> row that authored the track. For a room step this is the room's DIRECTOR, never the step's own placeholder FK.</param>
/// <param name="AgentName">That row's <see cref="ReelBolt.Shared.Data.Models.AgentDefinition.Name"/>.</param>
/// <param name="ProviderName">The name of the inference provider the authoring step resolved to; null when resolution failed or no resolver was available.</param>
/// <param name="StepResultId">The newest <c>WorkflowStepResult</c> id for the authoring step in this execution; null when no such row exists yet.</param>
public sealed record TimelineItemProvenance(
    Guid? AgentDefinitionId,
    string? AgentName,
    string? ProviderName,
    Guid? StepResultId);

/// <summary>
/// One nullable provenance slot per agent-authored track kind, resolved by the EXECUTOR (which
/// owns the DbContext scope and the execution's step graph) and consumed by
/// <see cref="TimelineManifestBuilder"/> (which stays pure, synchronous and I/O-free).
///
/// <para>
/// Track mapping: <see cref="Decision"/> → the video (cuts) items, <see cref="Graphics"/> → the
/// graphics items, <see cref="Music"/> → the music item, <see cref="Sfx"/> → the sfx items, and
/// <see cref="ColorGrade"/> → video items only under the precedence rule documented on
/// <c>TimelineManifestBuilder.BuildVideoTrack</c>. Insert and dialogue items are deterministic —
/// no agent authored them — and are never stamped from any slot here.
/// </para>
///
/// <para>
/// A null slot (or a null context) means "unresolved", which is a legitimate outcome: the builder
/// then leaves the affected items' provenance fields null, exactly as it did before this type
/// existed.
/// </para>
///
/// <para>
/// <see cref="OutputPolicy"/> is the one slot that is NOT a track author: the trial output policy
/// (B8b) is decided deterministically from the run's plan entitlements, not written by an agent, so
/// it is stamped once onto the manifest's output metadata rather than onto any item.
/// </para>
/// </summary>
public sealed record TimelineProvenanceContext(
    TimelineItemProvenance? Decision = null,
    TimelineItemProvenance? Graphics = null,
    TimelineItemProvenance? Music = null,
    TimelineItemProvenance? Sfx = null,
    TimelineItemProvenance? ColorGrade = null,
    TimelineOutputPolicy? OutputPolicy = null);
