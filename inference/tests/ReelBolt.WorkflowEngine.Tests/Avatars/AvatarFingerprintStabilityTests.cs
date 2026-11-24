#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// <see cref="AvatarFingerprint"/> is hashed into the ledger request key, the step-cache key and every recorded clip of
/// a person avatar. The values below were recorded from the code as it stood BEFORE object avatars were added
/// (docs/design/avatar-objects.md): a person's fingerprint must never move, or every clip made so far would stop being
/// reusable and a retried take would be bought twice.
/// </summary>
public class AvatarFingerprintStabilityTests
{
    private static readonly Guid AvatarId = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static ResolvedAvatarReference Reference(int ordinal, AvatarReferenceKind kind, AvatarReferenceRole role, char digest) =>
        new(Guid.Parse($"00000000-0000-0000-0000-00000000000{ordinal + 1}"), Guid.NewGuid(), kind, role, ordinal,
            new string(digest, 64), $"projects/p/userFiles/{ordinal}", "application/octet-stream");

    [Fact]
    public void A_person_avatars_fingerprint_is_the_value_recorded_before_objects_existed()
    {
        var avatar = new ResolvedAvatar(
            AvatarId,
            "Anna",
            true,
            null,
            [
                Reference(2, AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, 'c'),
                Reference(0, AvatarReferenceKind.Image, AvatarReferenceRole.Face, 'a'),
                Reference(1, AvatarReferenceKind.Image, AvatarReferenceRole.HalfBody, 'b'),
            ],
            null,
            AvatarDisclosure.Visible,
            new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

        AvatarFingerprint.Compute(avatar).Should().Be("768e127b150530bb07e689a9c5bfe52b9032667e4066acdd8a5f5ed65c11d620");
    }

    [Fact]
    public void An_objects_fingerprint_is_its_own_and_never_a_persons()
    {
        var person = new ResolvedAvatar(
            AvatarId, "Machine", true, null,
            [Reference(0, AvatarReferenceKind.Image, AvatarReferenceRole.Front, 'a')],
            null, AvatarDisclosure.MetadataOnly, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        ResolvedAvatar machine = person with { SubjectKind = AvatarSubjectKind.Object };

        string personPrint = AvatarFingerprint.Compute(person);
        string objectPrint = AvatarFingerprint.Compute(machine);

        objectPrint.Should().NotBe(personPrint, "the kind joins the hashed fields for an object");
        objectPrint.Should().MatchRegex("^[0-9a-f]{64}$");
        AvatarFingerprint.Compute(machine with { Name = "Renamed", Description = "Chrome", CoversPictures = true })
            .Should().Be(objectPrint, "a name, a description and the picture flag are not part of the identity");
        AvatarFingerprint.Compute(person with { SubjectKind = AvatarSubjectKind.Person, Description = "x", CoversPictures = true })
            .Should().Be(personPrint, "the new fields never move a person's fingerprint");
    }

    [Fact]
    public void Being_a_variant_never_moves_a_fingerprint()
    {
        // docs/design/avatar-objects.md "Avatar variants": a variant is identified by its own id, attestation and
        // references like any avatar; its label, its original and the description built from them are not hashed.
        var avatar = new ResolvedAvatar(
            AvatarId, "Anna", true, null,
            [Reference(0, AvatarReferenceKind.Image, AvatarReferenceRole.Face, 'a')],
            null, AvatarDisclosure.Visible, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        string print = AvatarFingerprint.Compute(avatar);

        AvatarFingerprint.Compute(avatar with
        {
            VariantLabel = "Suit",
            VariantOfAvatarId = Guid.NewGuid(),
            Description = ResolvedAvatar.DescribeVariant("Presenter", "in a dark suit")
        }).Should().Be(print);
        new ResolvedAvatar(AvatarId, "Anna", true, null, [], null, AvatarDisclosure.MetadataOnly).VariantLabel.Should().BeNull("an avatar is not a variant unless it says so");
    }

    [Fact]
    public void A_person_avatar_without_an_attestation_time_or_references_keeps_its_recorded_fingerprint()
    {
        var avatar = new ResolvedAvatar(AvatarId, "Anna", true, null, [], null, AvatarDisclosure.MetadataOnly);

        AvatarFingerprint.Compute(avatar).Should().Be("4a476518d4dfe83ac8c6690d787552c7ee73c6aee566d8a408dfadb7b591eb19");
    }
}
