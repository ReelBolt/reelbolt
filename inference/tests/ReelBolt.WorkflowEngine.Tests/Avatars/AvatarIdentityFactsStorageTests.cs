#nullable enable
using AwesomeAssertions;
using ReelBolt.Shared.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// QA pin (broll-film-quality-and-avatar-fidelity): identity facts that pass <see cref="AvatarIdentityFacts.Normalize"/>
/// must fit the <c>project_avatars.identity_facts</c> column (<see cref="AvatarIdentityFacts.MaxStoredChars"/>) and read
/// back. Each field is capped at <see cref="AvatarIdentityFacts.MaxFieldChars"/> characters, but the stored JSON escapes
/// every non-ASCII character as <c>\uXXXX</c> (six characters), so four full fields in Cyrillic, Greek or Japanese
/// overflow the column: the save fails (string too long) and <see cref="AvatarIdentityFacts.Parse"/> drops the value.
/// </summary>
public class AvatarIdentityFactsStorageTests
{
    [Theory]
    [InlineData('ж')] // Cyrillic
    [InlineData('λ')] // Greek
    [InlineData('車')] // Japanese / Chinese
    [InlineData('è')] // an accented Latin letter (Italian)
    public void Four_full_fields_in_any_script_fit_the_column_and_read_back(char letter)
    {
        string field = new(letter, AvatarIdentityFacts.MaxFieldChars);
        AvatarIdentityFacts facts = AvatarIdentityFacts.Normalize(field, field, field, field)!;

        string json = facts.ToJson()!;

        json.Length.Should().BeLessThanOrEqualTo(AvatarIdentityFacts.MaxStoredChars,
            "the stored JSON must fit character varying(MaxStoredChars), or saving the avatar fails");
        AvatarIdentityFacts.Parse(json).Should().Be(facts, "facts that were accepted must read back, not vanish");
    }
}
