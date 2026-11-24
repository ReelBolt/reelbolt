namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Who a person said they are and what they start from (table <c>user_onboarding_profiles</c>,
/// docs/design/new-user-funnel.md section 5). One row per USER, never per organization; removed with the
/// account. <see cref="Persona"/> and <see cref="SuggestedPersona"/> are tokens of
/// <c>AcquisitionVocabulary.Personas</c>, <see cref="Material"/> of <c>AcquisitionVocabulary.Materials</c>:
/// no free text reaches this table.
/// <para>
/// Two writers: the Go API inserts the row at account creation with only <see cref="SuggestedPersona"/>
/// (the persona of the landing page the person came from, <c>ON CONFLICT DO NOTHING</c>); the Inference API
/// writes the person's own answer or their skip.
/// </para>
/// </summary>
public class UserOnboardingProfile
{
    public Guid UserId { get; set; }

    /// <summary>The persona the person chose; null until they answer.</summary>
    public string? Persona { get; set; }

    /// <summary>What the person starts from; null until they answer.</summary>
    public string? Material { get; set; }

    /// <summary>The persona the landing page suggested; a preselection only, never an answer.</summary>
    public string? SuggestedPersona { get; set; }

    /// <summary>The plan the person picked on the pricing page before signing up; written by Go at signup, cleared by the person's own action.</summary>
    public string? PickedPlan { get; set; }

    /// <summary>When the person answered the two questions.</summary>
    public DateTime? AnsweredAt { get; set; }

    /// <summary>When the person skipped them.</summary>
    public DateTime? SkippedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
