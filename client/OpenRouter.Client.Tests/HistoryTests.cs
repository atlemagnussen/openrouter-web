using OpenRouter.Client.Storage;

namespace OpenRouter.Client.Tests;

public sealed class HistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Buckets_give_a_few_hundred_points()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), HistoryReader.BucketFor(TimeSpan.FromMinutes(15)));
        Assert.Equal(TimeSpan.FromSeconds(6), HistoryReader.BucketFor(TimeSpan.FromHours(1)));
        Assert.InRange((TimeSpan.FromDays(7) / HistoryReader.BucketFor(TimeSpan.FromDays(7))), 500, 610);
    }

    [Fact]
    public async Task Timeline_averages_per_bucket_and_leaves_gaps_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netcheck-history-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = await ResultStore.OpenAsync(path, CancellationToken.None))
            {
                var run = await store.StartRunAsync(T0, "laptop", "wlan0", null, null, null, null, CancellationToken.None);
                await store.WriteAsync(run, [
                    new PingSample(T0, "192.0.2.1", "gateway", 4),
                    new PingSample(T0.AddSeconds(5), "192.0.2.1", "gateway", null),
                    new PingSample(T0.AddSeconds(2), "198.51.100.1", "internet", 12),
                    new PingSample(T0.AddSeconds(3), "2001:db8::1", "internet-v6", 14),
                    new PingSample(T0.AddSeconds(30), "192.0.2.1", "gateway", 6),
                    new WifiSample(T0, "wlan0", true, "HomeNet", "02:00:5e:10:00:01", 5180, -50, null, 300, 300, null, null, null, null),
                    new WifiSample(T0.AddSeconds(2), "wlan0", true, "HomeNet", "02:00:5e:10:00:01", 5180, -60, null, 100, 300, null, null, null, null),
                    new WifiSample(T0.AddSeconds(4), "wlan0", false, null, null, null, null, null, null, null, null, null, null, null),
                    new NetEvent(T0.AddSeconds(20), "roam", "a → b"),
                ], CancellationToken.None);
            }

            await using var reader = await HistoryReader.OpenAsync(path, CancellationToken.None);
            var timeline = await reader.GetTimelineAsync(T0, T0.AddSeconds(40), TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(5, timeline.Times.Count);
            Assert.Equal(-55, timeline.SignalDbm[0]);          // disconnected readings don't count
            Assert.Equal(200, timeline.TxMbps[0]);
            Assert.Equal(4, timeline.RouterMs[0]);
            Assert.Equal(50, timeline.RouterLossPercent[0]);
            Assert.Equal(13, timeline.InternetMs[0]);          // IPv4 and IPv6 together
            Assert.Null(timeline.RouterMs[1]);                 // nothing measured: a gap, not zero
            Assert.Equal(6, timeline.RouterMs[3]);

            Assert.Single(await reader.GetEventsAsync(T0, T0.AddMinutes(1), CancellationToken.None));
            var status = await reader.GetStatusAsync(T0.AddSeconds(40), TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.False(status.Wifi!.Connected);
            Assert.Equal(T0.AddSeconds(30), status.LastSample);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }
}
