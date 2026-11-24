namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>What happened when a generated clip's content-provenance manifest (C2PA) was asked for.</summary>
public enum ProvenanceOutcome
{
    /// <summary>No signer is installed in this deployment: the clip carries container metadata only.</summary>
    NotAvailable,

    /// <summary>A manifest was embedded and signed.</summary>
    Signed,

    /// <summary>A signer is installed but failed; the clip is still delivered with its container metadata.</summary>
    Failed
}

/// <summary>
/// The seam for decision AV-9 option B (docs/design/avatars.md "AI Act disclosure"): a C2PA manifest stating that a
/// clip is AI-generated, embedded and signed after normalization. <b>Not implemented, and deliberately not faked.</b>
/// <para>
/// The gap, as of avatars phase 2: the candidate is the C2PA reference implementation (<c>c2pa-rs</c> and its
/// <c>c2patool</c> CLI, MIT/Apache-2.0, runs offline). It is not vetted for this repository, is not in the engine
/// image, and signing needs an X.509 signing certificate and private key from a CA on the C2PA trust list, whose
/// custody (KMS, rotation) is an open decision. Until both are settled the only signer is
/// <see cref="NoProvenanceSigner"/>, every avatar clip records <c>c2pa: "not_available"</c> in its manifest, and the
/// machine-readable marking is the container metadata the normalizer writes (<see cref="GeneratedClipDisclosure"/>).
/// A real signer replaces the registration; nothing else changes.
/// </para>
/// </summary>
public interface IGeneratedClipProvenanceSigner
{
    /// <summary>
    /// Embeds and signs a manifest in the MP4 at <paramref name="localMp4Path"/> in place. Never throws except for
    /// cancellation; a failure is <see cref="ProvenanceOutcome.Failed"/> and the clip is delivered unsigned.
    /// </summary>
    Task<ProvenanceOutcome> SignAsync(string localMp4Path, GeneratedClipProvenanceClaim claim, CancellationToken ct);
}

/// <summary>
/// What the manifest would assert: AI-generated (the C2PA <c>trainedAlgorithmicMedia</c> source type), the
/// generator, and that a consented likeness was used. Never the subject's name, an id or a storage key.
/// </summary>
public sealed record GeneratedClipProvenanceClaim(string Generator, string Model, bool ShowsConsentedLikeness);

/// <summary>The only signer today: signs nothing and says so (see <see cref="IGeneratedClipProvenanceSigner"/>).</summary>
public sealed class NoProvenanceSigner : IGeneratedClipProvenanceSigner
{
    public Task<ProvenanceOutcome> SignAsync(string localMp4Path, GeneratedClipProvenanceClaim claim, CancellationToken ct) =>
        Task.FromResult(ProvenanceOutcome.NotAvailable);
}
