#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The two WORKSPACE statements of the avatar library (docs/design/avatar-objects.md "The workspace avatar library
/// (global avatars)"): pinned byte for byte, because rows store these bytes under these versions and a statement is
/// never edited in place. Also the picture rule: an attestation under the workspace consent text covers pictures.
/// </summary>
public class AvatarLibraryStatementTests
{
    private static string Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void The_workspace_consent_statements_are_the_design_text_byte_for_byte()
    {
        AvatarWorkspaceConsentStatement.Version.Should().Be("avatar-consent-workspace-2026-11");
        AvatarWorkspaceConsentStatement.English.Should().Be(
            "I confirm that I am the person shown and heard in these files, or that I have that person's written permission; that the person is 18 or older; that they have given informed consent to their face, body and voice being used to generate AI videos and pictures in any project of this workspace, and to these files being sent for that purpose to the AI providers those projects use; that I will keep a record of that consent; and that I will revoke or delete this avatar if they withdraw it.");
        AvatarWorkspaceConsentStatement.Italian.Should().Be(
            "Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in qualsiasi progetto di questo spazio di lavoro, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da quei progetti; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la persona ritira il consenso.");
        Hex(AvatarWorkspaceConsentStatement.English).Should().Be("76061b19b5b6de3812cfd6f17089c5b92710b38d43456d2f18f279f82bb4e31b");
        Hex(AvatarWorkspaceConsentStatement.Italian).Should().Be("3ee5c3ca4a9bca7b92bc7ec90f0f62df763b8ff324a3dce8cea0ee8a050a90c1");
        Encoding.UTF8.GetByteCount(AvatarWorkspaceConsentStatement.English).Should().Be(487);
        Encoding.UTF8.GetByteCount(AvatarWorkspaceConsentStatement.Italian).Should().Be(592);

        AvatarWorkspaceConsentStatement.Locales.Should().Equal("en", "it");
        AvatarWorkspaceConsentStatement.For("en").Should().Be(AvatarWorkspaceConsentStatement.English);
        AvatarWorkspaceConsentStatement.For("it").Should().Be(AvatarWorkspaceConsentStatement.Italian);
        AvatarWorkspaceConsentStatement.For("de").Should().BeNull();
        AvatarWorkspaceConsentStatement.Matches("en", AvatarWorkspaceConsentStatement.English).Should().BeTrue();
        AvatarWorkspaceConsentStatement.Matches("en", AvatarWorkspaceConsentStatement.English + " ").Should().BeFalse("ordinal, untrimmed");
        AvatarWorkspaceConsentStatement.Matches("it", AvatarWorkspaceConsentStatement.English).Should().BeFalse("the statement must be the one of the locale shown");
        AvatarWorkspaceConsentStatement.Matches("en", AvatarConsentStatement.English).Should().BeFalse(
            "a project-scoped consent never becomes workspace-wide by itself");
    }

    [Fact]
    public void The_workspace_object_statements_are_the_design_text_byte_for_byte()
    {
        AvatarWorkspaceObjectStatement.Version.Should().Be("avatar-object-workspace-2026-11");
        AvatarWorkspaceObjectStatement.English.Should().Be(
            "I confirm that these files show an object, a product or a place and no identifiable person, and that I have the right to use them to generate AI videos and pictures in any project of this workspace and to send them for that purpose to the AI providers those projects use.");
        AvatarWorkspaceObjectStatement.Italian.Should().Be(
            "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in qualsiasi progetto di questo spazio di lavoro e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da quei progetti.");
        Hex(AvatarWorkspaceObjectStatement.English).Should().Be("f6936a2c2f94015edcb99fd9ddaf90f20503156c12cfe89d12f0076432f47e31");
        Hex(AvatarWorkspaceObjectStatement.Italian).Should().Be("8eede36ec578a6b3812a55cea21937c1ab62e301ff1b185a1afd104e33d6f221");
        Encoding.UTF8.GetByteCount(AvatarWorkspaceObjectStatement.English).Should().Be(271);
        Encoding.UTF8.GetByteCount(AvatarWorkspaceObjectStatement.Italian).Should().Be(335);

        AvatarWorkspaceObjectStatement.Locales.Should().Equal("en", "it");
        AvatarWorkspaceObjectStatement.For("de").Should().BeNull();
        AvatarWorkspaceObjectStatement.Matches("it", AvatarWorkspaceObjectStatement.Italian).Should().BeTrue();
        AvatarWorkspaceObjectStatement.Matches("en", AvatarWorkspaceObjectStatement.English + "\n").Should().BeFalse("ordinal, untrimmed");
        AvatarWorkspaceObjectStatement.Matches("en", AvatarObjectStatement.English).Should().BeFalse(
            "the project object statement is not the workspace one");
    }

    [Fact]
    public void An_attestation_under_the_workspace_consent_text_covers_pictures()
    {
        AvatarConsentStatement.CoversPictures(AvatarWorkspaceConsentStatement.Version).Should().BeTrue(
            "the workspace text names videos and pictures");
        AvatarConsentStatement.CoversPictures(AvatarConsentStatement.Version).Should().BeTrue();
        AvatarConsentStatement.CoversPictures(AvatarConsentStatement.Version2026_11).Should().BeFalse("the first text covers videos only");
        AvatarConsentStatement.CoversPictures(AvatarWorkspaceObjectStatement.Version).Should().BeFalse("an object statement is no person's consent");
        AvatarConsentStatement.CoversPictures(null).Should().BeFalse();

        new ProjectAvatar { SubjectKind = AvatarSubjectKind.Person, ConsentStatementVersion = AvatarWorkspaceConsentStatement.Version }
            .CoversPictures.Should().BeTrue("a linked import of a person carries the workspace version");
        new ProjectAvatar { SubjectKind = AvatarSubjectKind.Person, ConsentStatementVersion = AvatarConsentStatement.Version2026_11 }
            .CoversPictures.Should().BeFalse();
        new ProjectAvatar { SubjectKind = AvatarSubjectKind.Object, ConsentStatementVersion = AvatarWorkspaceObjectStatement.Version }
            .CoversPictures.Should().BeTrue("an object always may be shown in a picture");
    }

    [Fact]
    public void The_library_key_layout_accepts_only_its_own_objects()
    {
        Guid org = Guid.NewGuid(), item = Guid.NewGuid(), reference = Guid.NewGuid();
        string key = AvatarLibraryStorage.ReferenceKey(org, item, reference, "png");
        key.Should().Be($"organizations/{org:D}/avatar-library/{item:D}/{reference:D}.png");
        AvatarLibraryStorage.ConsentDocumentKey(org, item, "pdf").Should().Be($"organizations/{org:D}/avatar-library/{item:D}/consent.pdf");
        AvatarLibraryStorage.Parse(key).Should().Be((org, item));
        AvatarLibraryStorage.ReferenceKey(org, item, reference, "../x").Should().EndWith(".bin", "an unsafe extension never reaches a key");
        AvatarLibraryStorage.ExtensionOf("Photo.JPEG").Should().Be("jpeg");
        AvatarLibraryStorage.ExtensionOf("no-extension").Should().Be("bin");

        foreach (string foreign in new[]
                 {
                     $"projects/{org:D}/userFiles/a.png",
                     $"organizations/{org:D}/avatar-library/{item:D}/sub/a.png",
                     $"organizations/{org:D}/avatar-library/{item:D}/..",
                     $"organizations/{org:D}/other/{item:D}/a.png",
                     $"organizations/not-a-guid/avatar-library/{item:D}/a.png",
                     $"organizations/{org:D}/avatar-library/{item:D}/",
                     ""
                 })
        {
            AvatarLibraryStorage.Parse(foreign).Should().BeNull(foreign);
        }

        var file = new ProjectFile { SourceRevision = AvatarLibraryStorage.SourceRevision(item) };
        AvatarLibraryStorage.IsCopyOf(file, item).Should().BeTrue();
        AvatarLibraryStorage.IsCopyOf(file, Guid.NewGuid()).Should().BeFalse();
        AvatarLibraryStorage.IsCopyOf(new ProjectFile(), item).Should().BeFalse("a file the project already had is not a copy");
    }
}
