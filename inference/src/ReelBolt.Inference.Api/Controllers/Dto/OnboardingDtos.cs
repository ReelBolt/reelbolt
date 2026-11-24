namespace ReelBolt.Inference.Api.Controllers.Dto;

public sealed record OnboardingTourDto(string Status, int? Version, DateTime? UpdatedAt);

public sealed record OnboardingChecklistItemDto(string Key, bool Done);

public sealed record OnboardingChecklistDto(bool Dismissed, IReadOnlyList<OnboardingChecklistItemDto> Items);

/// <summary>
/// The welcome answers (docs/design/new-user-funnel.md section 6). Every member is null until it happens:
/// <see cref="SuggestedPersona"/> is the persona of the landing page the person signed up from (a
/// preselection, never an answer); <see cref="Persona"/> and <see cref="Material"/> are what they answered.
/// </summary>
public sealed record OnboardingProfileDto(
    string? Persona, string? Material, string? SuggestedPersona, DateTime? AnsweredAt, DateTime? SkippedAt, string? PickedPlan = null);

/// <summary>
/// <c>GET</c>/<c>PUT /api/v1/onboarding</c>. <see cref="Profile"/> is always present (an object of nulls for
/// a person nothing is known about); <see cref="StarterTemplateKey"/> is the recipe their first project
/// starts with, null until they say what they start from.
/// </summary>
public sealed record OnboardingResponse(
    OnboardingTourDto Tour, OnboardingChecklistDto Checklist, OnboardingProfileDto Profile, string? StarterTemplateKey);

/// <summary>What <c>PUT /api/v1/onboarding/profile</c> answers: the stored profile and the starter recipe that follows from it.</summary>
public sealed record OnboardingProfileResponse(OnboardingProfileDto Profile, string? StarterTemplateKey);

/// <summary>
/// The body of <c>PUT /api/v1/onboarding/profile</c>: the two answers (<see cref="Persona"/> and
/// <see cref="Material"/>, both required, both from their closed set) or the skip (<see cref="Skipped"/>
/// true and nothing else).
/// </summary>
public sealed class SaveOnboardingProfileRequest
{
    public string? Persona { get; set; }

    public string? Material { get; set; }

    public bool? Skipped { get; set; }
}

public sealed class OnboardingTourInput
{
    public string? Status { get; set; }

    public int? Version { get; set; }
}

public sealed class SaveOnboardingRequest
{
    public OnboardingTourInput? Tour { get; set; }

    public bool? ChecklistDismissed { get; set; }

    public List<string>? Marks { get; set; }
}

public sealed class OnboardingEventRequest
{
    public string? Event { get; set; }

    public string? Item { get; set; }
}
