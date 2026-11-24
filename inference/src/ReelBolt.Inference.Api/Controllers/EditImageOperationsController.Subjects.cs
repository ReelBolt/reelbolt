using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using static ReelBolt.Inference.Api.Controllers.EditImageDocumentSupport;

namespace ReelBolt.Inference.Api.Controllers;

// Avatars and objects in the picture editor (docs/design/avatar-objects.md): the `subjectIds` half of the operations
// route. `generate` with subjects makes a NEW picture on the instruction-edit model (the subjects' pictures are its
// image array); `instruct` with subjects keeps the layer as the picture being edited and adds the subjects' pictures
// as references. Every other operation refuses the field.
public partial class EditImageOperationsController
{
    /// <summary>The code of <c>subjectIds</c> sent with an operation that cannot show a subject.</summary>
    internal const string SubjectsNotSupportedCode = "subjects_not_supported";

    /// <summary>The code of a call that would send avatar pictures out of the host with no permission to.</summary>
    internal const string ConsentRequiredCode = "CONSENT_REQUIRED";

    /// <summary>
    /// How many of the subjects' pictures one call sends at most (docs/design/avatar-objects.md: up to 3 per subject,
    /// 6 in all). They come on top of the <see cref="MaxReferencePictures"/> loose reference pictures of an edit.
    /// </summary>
    public const int MaxSubjectPictures = ProjectSubjectLimits.MaxPicturesInAll;

    private readonly IProjectSubjectReader? _subjectReader;
    private readonly ISubjectPictureScaler? _subjectScaler;
    private readonly IMediaEgressCallPermission _egressPermission;
    private readonly IOrganizationEntitlements? _entitlements;

    /// <summary>
    /// The grantor (<c>MediaEgressPermission.By*</c>) the assistant's picture tool established, under the one rule, for the
    /// ONE call it runs through this controller in process (<see cref="ReelBolt.Inference.Api.Services.Assistant.IImageDocumentCommands"/>);
    /// null on every HTTP request. Never model-bound: only the in-process adapter sets it.
    /// </summary>
    internal string? InProcessMediaEgressGrant { get; set; }

    /// <summary>One subject picture as it is sent, with the size the meter prices a reference picture by.</summary>
    private sealed record SubjectPictureSent(string DataUri, long? Pixels, byte[] Bytes, string ContentType);

    /// <summary>
    /// Everything a call with <c>subjectIds</c> sends and records: the subjects, their pictures in array order (each
    /// subject's pictures together, subjects in the order they were named), how many pictures were left out, and the
    /// aspect ratio a new picture is asked for.
    /// </summary>
    private sealed record SubjectWork(
        ProjectSubjectRead Read,
        IReadOnlyList<SubjectPictureSent> Pictures,
        IReadOnlyList<(ProjectSubject Subject, int Count)> Groups,
        int Skipped,
        string? GenerateAspect)
    {
        /// <summary>A person's pictures are sent: the vendor is asked not to keep the inputs.</summary>
        public bool HasPerson => Read.HasPerson;

        public PictureSubjectFacts Facts => PictureSubjectFacts.From(Read);

        /// <summary>The instruction's subject sentences, with <paramref name="firstPosition"/> the 1-based array position of the first subject picture.</summary>
        public IReadOnlyList<SubjectPictureSlot> Slots(int firstPosition)
        {
            List<SubjectPictureSlot> slots = new(Groups.Count);
            int position = firstPosition;
            foreach ((ProjectSubject subject, int count) in Groups)
            {
                slots.Add(new SubjectPictureSlot(subject.Kind, subject.Name, subject.Description, position, count));
                position += count;
            }

            return slots;
        }
    }

    /// <summary>The operation a call is routed as: <c>generate</c> with subjects runs on the instruction-edit model.</summary>
    private static Op RoutedAs(Op op, bool withSubjects) => withSubjects && op == Op.Generate ? Op.Instruct : op;

    private static bool TakesSubjects(Op op) => op is Op.Generate or Op.Instruct;

    /// <summary>
    /// The catalogued instruction-edit endpoint <paramref name="model"/> names on a fal.ai row (empty = the catalogue's
    /// default for an edit) when it takes an image ARRAY, the only shape several pictures of a subject can be sent in;
    /// otherwise null. Any other provider kind is null: its client reads one picture.
    /// </summary>
    private static FalImageEndpoint? SubjectEndpointFor(InferenceProviderKind kind, string model)
    {
        if (kind != InferenceProviderKind.FalAi) return null;
        FalImageEndpoint? endpoint = model.Length == 0
            ? FalAiImageClient.Catalog.DefaultFor(FalImageJob.Instruct)
            : FalAiImageClient.Catalog.Find(model);
        return endpoint is { Job: FalImageJob.Instruct, ImageUrlsField: not null } ? endpoint : null;
    }

    /// <summary>
    /// Whether a run on <paramref name="kind"/> with <paramref name="model"/> (empty = the default edit model of a fal.ai
    /// row) can show avatars and objects: the same test a run applies, shared with the assistant's picture tool so its
    /// proposal and the run agree.
    /// </summary>
    internal static bool ModelTakesSubjects(InferenceProviderKind kind, string? model) =>
        SubjectEndpointFor(kind, model?.Trim() ?? string.Empty) is not null;

    /// <summary>The catalogued instruction-edit models that take an image array, default first: the ones a subject can be shown with.</summary>
    internal static IReadOnlyList<string> SubjectCapableModels() =>
        [.. FalAiImageClient.Catalog.All
            .Where(e => e is { Job: FalImageJob.Instruct, ImageUrlsField: not null })
            .OrderByDescending(e => e.IsDefault)
            .Select(e => e.Id)];

    /// <summary>
    /// The <c>409 OPERATION_UNSUPPORTED</c> of a model that cannot show a subject: the tool's usual sentence shape, and
    /// <c>models</c>, the picture-edit models that can (what the editor offers the person to ask for).
    /// </summary>
    private ObjectResult SubjectsUnsupported(string detail) => Conflict(new
    {
        code = OperationUnsupportedCode,
        message = "The AI service set up for this tool cannot show avatars or objects. " + detail,
        models = SubjectCapableModels()
    });

    /// <summary>
    /// The subject half of a run, after the request and the provider were checked and before anything is presigned,
    /// held or sent. In the design's order: the avatars the ids name (<c>404</c>, <c>400</c>), the plan feature of each
    /// kind (<c>402</c>), the permission to send their media (<c>409 CONSENT_REQUIRED</c>), the one rule
    /// (<c>409 AVATAR_NOT_USABLE</c>), the picture consent version (<c>409 AVATAR_CONSENT_NO_PICTURES</c>), their
    /// pictures re-hashed against the attested digests, and last the model (<c>409 OPERATION_UNSUPPORTED</c>).
    /// </summary>
    private async Task<(SubjectWork? Work, ActionResult? Refusal)> PrepareSubjectsAsync(
        Op op, EditImageOperationRequest request, Project project, InferenceProviderKind kind, string model, CancellationToken ct)
    {
        if (_subjectReader is null)
        {
            // No avatar feature is wired on this host: as far as the caller can tell, no avatar exists.
            return (null, NotFound(new { error = ProjectSubjectReader.NotFoundCode, code = ProjectSubjectReader.NotFoundCode, message = "An avatar or object that was chosen is not in this project." }));
        }

        ProjectSubjectLookup found = await _subjectReader.FindAsync(project.Id, request.SubjectIds, ct);
        if (found.Refusal is { } notFound) return (null, notFound.ToResult());

        // ---- the plan feature of each kind named ----
        if (_entitlements is not null)
        {
            OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
            foreach (AvatarSubjectKind subjectKind in found.Kinds)
            {
                PlanGateViolation? violation = subjectKind == AvatarSubjectKind.Object
                    ? PlanGate.CheckAvatarObjects(set)
                    : PlanGate.CheckAvatars(set);
                if (violation is not null)
                    return (null, new ObjectResult(violation.ToBody()) { StatusCode = violation.StatusCode });
            }
        }

        // ---- the permission to send their pictures out of the host ----
        // The project's recorded consent, or this one call's explicit word from a caller who may give it under the one rule
        // (IMediaEgressCallPermission: a signed-in person on this route, an API key with media.egress; or, for the
        // assistant's picture tool running this controller in process, the grant that tool established under the same
        // rule). The word of any other caller counts for nothing, and nothing is recorded either way.
        bool allowed = project.SubjectMediaConsentAt is not null
            || (request.AllowMediaEgress == true
                && (ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.IsGrantor(InProcessMediaEgressGrant)
                    || await _egressPermission.MayAllowAsync(
                        MediaEgressCall.FromRequest(MediaEgressSurface.PictureOperation, _currentUser?.UserId ?? Guid.Empty, project.Id, HttpContext), ct)));
        if (!allowed)
        {
            return (null, Conflict(new
            {
                code = ConsentRequiredCode,
                consent = "subject_media",
                message = "Before an avatar or object can be shown in a picture, this project has to allow its pictures to be " +
                          "sent to the AI picture service. You can allow it once in the project's Avatars tab."
            }));
        }

        // ---- the model, decided now (it sets how many pictures fit) and refused last ----
        FalImageEndpoint? endpoint = SubjectEndpointFor(kind, model);
        int looseReferences = op == Op.Instruct ? Math.Min(request.ReferenceProjectFileIds?.Count ?? 0, MaxReferencePictures) : 0;
        int room = MaxSubjectPictures;
        if (endpoint?.MaxInputImages is { } most)
        {
            // An edit sends the layer first and the loose reference pictures after it; a new picture sends only subjects.
            room = Math.Min(room, most - (op == Op.Instruct ? 1 + looseReferences : 0));
        }

        ProjectSubjectPictureBudget budget = new(ProjectSubjectLimits.MaxPicturesPerSubject, Math.Max(room, 0));

        // ---- the one rule, the picture consent version, and the pictures themselves ----
        ProjectSubjectRead read = await _subjectReader.ReadAsync(found, ProjectSubjectPurpose.Picture, budget, ct);
        if (read.Refusal is { } unusable) return (null, unusable.ToResult());

        if (endpoint is null)
        {
            string shown = model.Length == 0 ? "its model" : $"'{model}'";
            return (null, SubjectsUnsupported(
                kind == InferenceProviderKind.FalAi
                    ? $"{shown} takes one picture at a time. Use a picture-edit model that takes several pictures: {string.Join(", ", SubjectCapableModels())}."
                    : $"Showing an avatar needs a fal.ai picture-edit model that takes several pictures: {string.Join(", ", SubjectCapableModels())}."));
        }

        if (read.Subjects.Any(s => s.Pictures.Count == 0))
        {
            return (null, SubjectsUnsupported(
                $"'{endpoint.Id}' takes at most {endpoint.MaxInputImages} pictures in one request, which leaves no room for a picture of every avatar chosen. " +
                "Choose fewer avatars, or use fewer reference pictures."));
        }

        // ---- a picture that is still an output's bytes ----
        // An object may show a platform-made file (a frame, an export, a render). Its bytes leave the host only with a
        // current verdict, the same gate as the layer and the loose reference pictures; an upload passes at once.
        if (_publishModeration is not null)
        {
            Guid[] fileIds = [.. read.Subjects.SelectMany(s => s.Pictures).Select(p => p.ProjectFileId).Distinct()];
            List<string> keys = await _db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == project.Id && fileIds.Contains(f.Id))
                .Select(f => f.StorageKey)
                .ToListAsync(ct);
            foreach (string key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                if (await _publishModeration.GateStoredCopyAsync(project.Id, key, ct) is { } refused)
                    return (null, refused.ToRefusal(this));
            }
        }

        // ---- the copies that are sent ----
        List<SubjectPictureSent> sent = [];
        List<(ProjectSubject, int)> groups = [];
        foreach (ProjectSubject subject in read.Subjects)
        {
            foreach (ProjectSubjectPicture picture in subject.Pictures)
            {
                ScaledSubjectPicture? scaled = _subjectScaler is not null
                    ? await _subjectScaler.ScaleAsync(picture, ct)
                    : picture.Bytes.LongLength <= FfmpegSubjectPictureScaler.MaxUnscaledBytes
                        ? new ScaledSubjectPicture(picture.ContentType, picture.Bytes, picture.Width, picture.Height)
                        : null;
                if (scaled is null)
                {
                    return (null, StatusCode(VendorFailureStatus, new
                    {
                        code = OperationFailedCode,
                        message = $"A picture of \"{subject.Name}\" could not be prepared to send. Nothing was sent and nothing was charged; try again."
                    }));
                }

                long? pixels = scaled.Width is > 0 && scaled.Height is > 0 ? (long)scaled.Width.Value * scaled.Height.Value : null;
                sent.Add(new SubjectPictureSent(scaled.ToDataUri(), pixels, scaled.Bytes, scaled.ContentType));
            }

            groups.Add((subject, subject.Pictures.Count));
        }

        int skipped = read.Subjects.Sum(s => s.PicturesAvailable) - sent.Count;
        return (new SubjectWork(read, sent, groups, skipped, op == Op.Generate ? SubjectAspect(endpoint, request.Params) : null), null);
    }

    /// <summary>
    /// The aspect ratio a NEW picture with subjects is asked for. An instruction-edit model takes no pixel size, so the
    /// canvas the caller asked for (<c>params.width</c> x <c>params.height</c>) becomes the nearest ratio the endpoint
    /// documents; an explicit <c>params.aspect</c> is passed as it is (the client refuses one the endpoint does not
    /// document). Null when the endpoint documents no aspect ratio or no size was asked for: the model then chooses,
    /// and the result is still scaled to cover the canvas.
    /// </summary>
    internal static string? SubjectAspect(FalImageEndpoint endpoint, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if ((ParamString(parameters, "aspect") ?? ParamString(parameters, "aspectRatio")) is { } asked) return asked;
        if (ParamInt(parameters, "width") is not (int width and > 0) || ParamInt(parameters, "height") is not (int height and > 0)) return null;
        if (endpoint.AspectRatioField is null || endpoint.FieldNamed(endpoint.AspectRatioField)?.Values is not { } values) return null;

        double wanted = Math.Log((double)width / height);
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (string value in values)
        {
            string[] parts = value.Split(':');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w <= 0
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) || h <= 0)
                continue; // "auto" and the like are not ratios

            double distance = Math.Abs(Math.Log(w / h) - wanted);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = value;
            }
        }

        return best;
    }

    /// <summary>
    /// The layer provenance of a result made with subjects: the operation's own facts plus <c>instruction</c>, the
    /// version of the fixed words the prompt was sent in. Who the layer shows is its CONTENT
    /// (<see cref="PictureSubjectFacts"/>), not its provenance. A layer made without subjects keeps the shape it
    /// always had.
    /// </summary>
    private static string SubjectProvenanceJson(
        string operationName, string providerKind, InferenceProviderCapability servedAs, string model, string? prompt,
        string? vendorRequestId, Guid? parentLayerId) =>
        JsonSerializer.Serialize(new
        {
            operation = operationName,
            providerKind,
            capability = servedAs.ToString(),
            model,
            prompt,
            vendorRequestId,
            parentLayerId,
            instruction = SubjectPictureInstruction.Version
        }, JsonWithNulls);

    /// <summary>
    /// What a tool can do with avatars and objects right now (the capabilities route): whether the picture-edit model
    /// it would use takes several pictures, the limits, and the models of that service that do.
    /// </summary>
    private async Task<EditImageSubjectCapabilitiesDto> SubjectCapabilitiesAsync(CancellationToken ct)
    {
        ResolvedImageProvider? provider;
        try
        {
            provider = await _providerResolver.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, ct);
        }
        catch (ModelUnpricedException)
        {
            provider = null; // an unpriced platform model cannot run, so it shows nothing
        }

        return provider is null
            ? new EditImageSubjectCapabilitiesDto(false, ProjectSubjectLimits.MaxSubjects, ProjectSubjectLimits.MaxPeople, [])
            : SubjectCapabilities(provider);
    }

    /// <summary>The same answer for a picture-edit service that is already resolved.</summary>
    private EditImageSubjectCapabilitiesDto SubjectCapabilities(ResolvedImageProvider provider)
    {
        string model = ChooseModel(null, Op.Instruct, provider.Kind, provider.ModelName);
        FalImageEndpoint? endpoint = SubjectEndpointFor(provider.Kind, model);
        string described = model.Length == 0 && provider.Kind == InferenceProviderKind.FalAi
            ? FalAiImageClient.Catalog.DefaultFor(FalImageJob.Instruct).Id
            : model;
        return new EditImageSubjectCapabilitiesDto(
            endpoint is not null && _subjectReader is not null,
            ProjectSubjectLimits.MaxSubjects,
            ProjectSubjectLimits.MaxPeople,
            provider.Kind == InferenceProviderKind.FalAi ? SubjectCapableModels() : [],
            new EditImageOperationProviderDto(provider.Kind.ToString(), described, provider.Capability.ToString()));
    }
}
