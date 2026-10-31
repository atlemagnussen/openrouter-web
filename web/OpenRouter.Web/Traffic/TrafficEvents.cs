using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenRouter.Web.Traffic;

/// <summary>
/// Pushes traffic updates to open Traffic pages over Server-Sent Events: <c>ready</c> on connecting,
/// <c>live</c> every few seconds and <c>minute</c> when a minute has been written to the database. Keeps the last few minutes of live
/// points so a page that opens has something to draw straight away.
/// </summary>
public sealed class TrafficEvents
{
    /// <summary>How much live history to keep and draw.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly List<Channel<SseItem<string>>> _subscribers = [];
    private readonly LinkedList<LivePoint> _recent = new();

    public IReadOnlyList<LivePoint> RecentLive
    {
        get
        {
            lock (_lock)
                return _recent.ToList();
        }
    }

    public void PublishLive(LivePoint point)
    {
        lock (_lock)
        {
            _recent.AddLast(point);
            while (_recent.First is { } first && point.Time - first.Value.Time > (long)LiveWindow.TotalSeconds)
                _recent.RemoveFirst();
        }
        Broadcast(new SseItem<string>(JsonSerializer.Serialize(point, JsonSerializerOptions.Web), "live"));
    }

    /// <param name="minute">Start of the minute just written, Unix seconds.</param>
    public void PublishMinute(long minute) =>
        Broadcast(new SseItem<string>(JsonSerializer.Serialize(new { minute }, JsonSerializerOptions.Web), "minute"));

    /// <summary>Events from now on, until <paramref name="cancellationToken"/> fires (the page closes or the app stops).</summary>
    public async IAsyncEnumerable<SseItem<string>> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A page that can't keep up loses the oldest events rather than holding memory.
        var channel = Channel.CreateBounded<SseItem<string>>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
            _subscribers.Add(channel);
        try
        {
            // Sent at once, so the browser gets the response (and knows it's connected) without waiting for the first update.
            yield return new SseItem<string>("{}", "ready");
            while (await WaitAsync(channel.Reader, cancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                    yield return item;
            }
        }
        finally
        {
            lock (_lock)
                _subscribers.Remove(channel);
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
                return _subscribers.Count;
        }
    }

    private void Broadcast(SseItem<string> item)
    {
        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(item);
        }
    }

    // Closing the stream is the normal way out, so don't surface it as an exception.
    private static async Task<bool> WaitAsync(ChannelReader<SseItem<string>> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
