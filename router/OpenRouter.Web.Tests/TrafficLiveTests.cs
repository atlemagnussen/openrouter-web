using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Tests;

public class TrafficLiveTests
{
    private static readonly DateTimeOffset Minute = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 11)]   // 12:00:01 mostly covers 11:59:56–12:00:01: still 11:59
    [InlineData(6, 12)]   // 12:00:06 covers 12:00:01–12:00:06: 12:00
    [InlineData(56, 12)]
    public void Five_second_readings_belong_to_the_minute_they_mostly_cover(int second, int expectedMinute)
    {
        var minute = TrafficSampler.MinuteOf(Minute.AddSeconds(second), TimeSpan.FromSeconds(5));
        Assert.Equal(Minute.AddMinutes(expectedMinute - 12).ToUnixTimeSeconds(), minute);
    }

    [Fact]
    public void Minute_readings_belong_to_the_minute_before()
    {
        var interval = TimeSpan.FromMinutes(1);
        var at = Minute.AddSeconds(1);
        Assert.Equal(Minute.AddMinutes(-1).ToUnixTimeSeconds(), TrafficSampler.MinuteOf(at, interval));
        // ...so the next reading is always in a new minute and each one is written straight away.
        Assert.NotEqual(TrafficSampler.MinuteOf(at, interval), TrafficSampler.MinuteOf(at + interval, interval));
    }

    [Fact]
    public void Live_point_has_rates_and_the_busiest_devices_first()
    {
        ClientTraffic[] clients =
        [
            new("02:00:00:00:00:01", "small", "LAN", 1_000, 0),
            new("02:00:00:00:00:02", "big", "LAN", 50_000, 5_000),
            new(TrafficAttribution.UnknownKey, null, null, 2_000, 0),
            new("02:00:00:00:00:03", null, "LAN", 3_000, 0),
        ];
        var point = LivePoint.Build(Minute, 5, "eth0", [new LinkTraffic("eth0", 62_500, 6_250), new LinkTraffic("eth1", 1, 1)], clients);

        Assert.Equal(Minute.ToUnixTimeSeconds(), point.Time);
        Assert.Equal((100_000.0, 10_000.0), (point.WanDown, point.WanUp));
        Assert.Equal(["big", "02:00:00:00:00:03", "Unknown addresses", "small"], point.Clients.Select(c => c.Name));
        Assert.Equal(80_000, point.Clients[0].Down);
    }

    [Fact]
    public async Task Subscribers_get_ready_then_published_events()
    {
        var events = new TrafficEvents();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();

        var reading = Task.Run(async () =>
        {
            await foreach (var item in events.SubscribeAsync(cancel.Token))
            {
                received.Add(item.EventType ?? "");
                if (received.Count == 3)
                    break;
            }
        });
        while (events.SubscriberCount == 0)
            await Task.Delay(10);

        events.PublishLive(new LivePoint(1, 0, 0, []));
        events.PublishMinute(60);
        await reading;

        Assert.Equal(["ready", "live", "minute"], received);
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public async Task Cancelling_ends_the_stream_quietly()
    {
        var events = new TrafficEvents();
        using var cancel = new CancellationTokenSource();
        var items = 0;
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in events.SubscribeAsync(cancel.Token))
                items++;
        });
        while (events.SubscriberCount == 0)
            await Task.Delay(10);

        await cancel.CancelAsync();
        await reading; // no OperationCanceledException

        Assert.Equal(1, items);
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public void Recent_live_points_cover_the_live_window()
    {
        var events = new TrafficEvents();
        var window = (long)TrafficEvents.LiveWindow.TotalSeconds;
        for (long t = 0; t <= window + 30; t += 5)
            events.PublishLive(new LivePoint(t, 0, 0, []));

        var recent = events.RecentLive;
        Assert.Equal(30, recent[0].Time);
        Assert.Equal(window + 30, recent[^1].Time);
    }
}
