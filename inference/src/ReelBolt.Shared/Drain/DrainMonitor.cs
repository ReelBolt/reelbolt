using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReelBolt.Shared.Drain;

/// <summary>
/// Polls for the drain signal file and publishes a status file. With no signal it only reports
/// <c>active</c>; it changes nothing else about the process. Draining is never reversed.
/// </summary>
public sealed class DrainMonitor : BackgroundService, IDrainState
{
    private readonly DrainOptions _options;
    private readonly IEnumerable<IDrainWorkSource> _sources;
    private readonly IEnumerable<IDrainTarget> _targets;
    private readonly TimeProvider _time;
    private readonly ILogger<DrainMonitor> _logger;
    private DateTimeOffset? _lastStatusWrite;
    private int _draining;
    private int _targetsStarted;

    public DrainMonitor(
        IOptions<DrainOptions> options,
        IEnumerable<IDrainWorkSource> sources,
        IEnumerable<IDrainTarget> targets,
        TimeProvider time,
        ILogger<DrainMonitor> logger)
    {
        _options = options.Value;
        _sources = sources;
        _targets = targets;
        _time = time;
        _logger = logger;
    }

    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One poll: detect the signal, begin drain once, write the status if due. Never throws.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The signal file surviving an in-place restart is intended: a draining container must stay draining.
            if (!IsDraining && File.Exists(_options.SignalPath))
            {
                Interlocked.Exchange(ref _draining, 1);
                _logger.LogWarning("Drain signal {Path} seen; taking no new work", _options.SignalPath);
            }

            if (IsDraining && Interlocked.Exchange(ref _targetsStarted, 1) == 0)
            {
                foreach (IDrainTarget target in _targets)
                {
                    try
                    {
                        await target.BeginDrainAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Drain target {Target} failed to stop", target.GetType().Name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Drain signal check failed");
        }

        WriteStatusIfDue();
    }

    private void WriteStatusIfDue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_lastStatusWrite is { } last && now - last < _options.StatusInterval)
        {
            return;
        }

        _lastStatusWrite = now;
        try
        {
            // Unique name + CreateNew: a planted file or symlink at a guessable path is never followed.
            string tmp = _options.StatusPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                {
                    w.Write(BuildStatusJson(now));
                }

                File.Move(tmp, _options.StatusPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing drain status to {Path} failed; work is unaffected", _options.StatusPath);
        }
    }

    public string BuildStatusJson(DateTimeOffset now)
    {
        var detail = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (IDrainWorkSource source in _sources)
        {
            int count = Math.Max(0, source.ActiveCount);
            detail[source.Name] = count;
            total += count;
        }

        string state = !IsDraining
            ? "active"
            : total == 0 && _targets.All(t => t.IsFullyStopped) ? "drained" : "draining";
        return JsonSerializer.Serialize(new
        {
            state,
            activeWork = total,
            detail,
            updatedAt = now.UtcDateTime.ToString("O"),
        });
    }
}
