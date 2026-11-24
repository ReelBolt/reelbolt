using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// Turns the NAMES of avatars and objects into the avatars of one project. The assistant and MCP name things, never
/// ids (docs/design/avatar-objects.md "Avatar tools for the assistant and MCP"); the picture tool and its approval
/// card both resolve through here, so they cannot disagree about which avatar a name means.
/// </summary>
public static class ProjectSubjectNames
{
    /// <summary>The most names listed in a refusal.</summary>
    public const int MaxNamesListed = 20;

    /// <summary>
    /// The avatars <paramref name="names"/> mean among <paramref name="avatars"/> (the avatars of ONE project the
    /// caller may see), in the order named, or the plain sentence that says which name is unknown or means more than
    /// one, with the names that can be used.
    /// </summary>
    /// <remarks>
    /// A name is matched exactly first, then without regard to case. An avatar whose consent was withdrawn is matched
    /// only when no live avatar has the name: a name is unique among live avatars, and a withdrawn one is still named
    /// so the caller is told why it cannot be used rather than that it does not exist.
    /// </remarks>
    public static (IReadOnlyList<ProjectAvatar>? Avatars, string? Problem) Resolve(
        IReadOnlyList<ProjectAvatar> avatars, IReadOnlyList<string>? names)
    {
        ArgumentNullException.ThrowIfNull(avatars);
        if (names is not { Count: > 0 })
            return (null, "name at least one avatar or object");

        List<ProjectAvatar> live = [.. avatars.Where(a => a.ConsentRevokedAt is null)];
        List<ProjectAvatar> withdrawn = [.. avatars.Where(a => a.ConsentRevokedAt is not null)];
        List<ProjectAvatar> resolved = new(names.Count);
        foreach (string? raw in names)
        {
            string name = (raw ?? string.Empty).Trim();
            if (name.Length == 0)
                return (null, "a subject name is empty" + Usable(live));

            List<ProjectAvatar> matches = Match(live, name);
            if (matches.Count == 0) matches = Match(withdrawn, name);
            if (matches.Count == 0)
                return (null, $"no avatar or object named \"{name}\" is in this project" + Usable(live));
            if (matches.Count > 1)
                return (null, $"\"{name}\" could mean more than one avatar or object; use the exact name" + Usable(live));
            resolved.Add(matches[0]);
        }

        return (resolved, null);
    }

    private static List<ProjectAvatar> Match(List<ProjectAvatar> avatars, string name)
    {
        List<ProjectAvatar> exact = [.. avatars.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal))];
        return exact.Count > 0
            ? exact
            : [.. avatars.Where(a => string.Equals(a.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))];
    }

    private static string Usable(List<ProjectAvatar> live)
    {
        if (live.Count == 0) return ". This project has no avatar or object yet.";
        IEnumerable<string> listed = live
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxNamesListed)
            .Select(a => $"\"{a.Name}\" ({(a.SubjectKind == AvatarSubjectKind.Object ? "object" : "person")})");
        return ". Names that can be used: " + string.Join(", ", listed) + (live.Count > MaxNamesListed ? ", and more." : ".");
    }
}
