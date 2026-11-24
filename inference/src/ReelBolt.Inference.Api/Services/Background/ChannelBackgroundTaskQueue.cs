using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// In-memory channel-based implementation of the background task queue. Every task is tracked from
/// the moment it is queued until the worker calls <see cref="Complete"/>, which is what lets a
/// sweeper re-offer persisted pending work without ever queuing (or running) the same task twice.
/// </summary>
public class ChannelBackgroundTaskQueue<T> : IBackgroundTaskQueue<T> where T : notnull
{
    public const int DefaultCapacity = 100;

    private readonly Channel<T> _channel;
    private readonly ConcurrentDictionary<T, byte> _tracked = new();
    private readonly int _capacity;

    public ChannelBackgroundTaskQueue() : this(DefaultCapacity)
    {
    }

    public ChannelBackgroundTaskQueue(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        // FullMode only matters to an awaiting writer, and there is none: TryWrite answers false
        // the moment the channel is full.
        BoundedChannelOptions options = new(_capacity) { FullMode = BoundedChannelFullMode.Wait };
        _channel = Channel.CreateBounded<T>(options);
    }

    public bool TryQueue(T task)
    {
        if (!_tracked.TryAdd(task, 0))
            return false;

        if (_channel.Writer.TryWrite(task))
            return true;

        _tracked.TryRemove(task, out _);
        return false;
    }

    public async ValueTask<T> DequeueAsync(CancellationToken ct)
    {
        return await _channel.Reader.ReadAsync(ct);
    }

    public void Complete(T task) => _tracked.TryRemove(task, out _);

    public bool IsTracked(T task) => _tracked.ContainsKey(task);

    public int FreeCapacity => Math.Max(0, _capacity - _channel.Reader.Count);

    public int TrackedCount => _tracked.Count;
}
