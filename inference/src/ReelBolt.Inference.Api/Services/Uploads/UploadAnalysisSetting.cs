using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// The operator's switch for analysis at upload (<c>MediaAnalysis:OnUpload</c>). On by default: a project
/// still has to opt in (<c>projects.analyze_on_upload</c>). Off: no upload ever asks for the analysis,
/// whatever the project chose.
/// </summary>
public sealed class UploadAnalysisOptions
{
    public const string SectionName = "MediaAnalysis:OnUpload";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Answers "should a new video of this project be analysed for workflows when it is uploaded?".
/// The ONE place that reads the project's choice and the global switch; <see cref="UploadDerivativeRequest"/>
/// asks it once per published request, so no publisher of a derivative request reads the setting itself.
/// </summary>
public interface IUploadAnalysisSetting
{
    /// <summary>
    /// True when the global switch is on and the project opted in. Never throws for a failed read:
    /// the answer is then false (the upload is published as it always was). Cancellation propagates.
    /// </summary>
    Task<bool> IsOnAsync(Guid projectId, CancellationToken ct);
}

public sealed class UploadAnalysisSetting : IUploadAnalysisSetting
{
    private readonly InferenceApiDbContext _db;
    private readonly IOptionsMonitor<UploadAnalysisOptions>? _options;
    private readonly ILogger<UploadAnalysisSetting>? _logger;

    // One answer per project for the life of this (scoped) instance: unpacking a zip publishes one request per
    // media file of the same project, and they must not each cost a query.
    private readonly Dictionary<Guid, bool> _answers = [];

    public UploadAnalysisSetting(
        InferenceApiDbContext db,
        IOptionsMonitor<UploadAnalysisOptions>? options = null,
        ILogger<UploadAnalysisSetting>? logger = null)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> IsOnAsync(Guid projectId, CancellationToken ct)
    {
        if (!(_options?.CurrentValue.Enabled ?? true)) return false;
        if (_answers.TryGetValue(projectId, out bool known)) return known;

        try
        {
            // No tenant filter: the id comes from a committed project_files row, never from a caller, and
            // the background paths (sync, unpack, the CSAM sweeper) have no tenant. Only the flag is read.
            bool on = await _db.Projects
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.AnalyzeOnUpload)
                .FirstOrDefaultAsync(ct);
            _answers[projectId] = on;
            return on;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "Could not read the upload-analysis setting of project {ProjectId} ({ExceptionType}); "
                + "the upload is published without it",
                projectId,
                ex.GetType().Name);
            return false;
        }
    }
}
