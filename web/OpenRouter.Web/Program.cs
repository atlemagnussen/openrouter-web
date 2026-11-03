using OpenRouter.Web.Configuration;
using OpenRouter.Web.Dns;
using OpenRouter.Web.Firewall;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Systemd;
using OpenRouter.Web.Traffic;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection(RouterOptions.Section))
    .PostConfigure(options => options.ResolvePaths(builder.Environment.ContentRootPath))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var sampleNow = builder.Configuration.GetValue<DateTimeOffset?>($"{RouterOptions.Section}:{nameof(RouterOptions.SampleNow)}");
builder.Services.AddSingleton(sampleNow is { } now ? new FixedTimeProvider(now) : TimeProvider.System);
builder.Services.AddSingleton<KeaControlClient>();
builder.Services.AddSingleton<KeaService>();
builder.Services.AddSingleton<RadvdService>();
builder.Services.AddSingleton<IpCommand>();
builder.Services.AddSingleton<KernelReader>();
builder.Services.AddSingleton<LinkStore>();
builder.Services.AddHostedService<LinkMonitor>();
builder.Services.AddSingleton<SystemdService>();
builder.Services.AddSingleton<FirewallService>();
builder.Services.AddSingleton<ProbeService>();
builder.Services.AddSingleton<NftService>();
builder.Services.AddSingleton<TrafficStore>();
builder.Services.AddSingleton<TrafficStatus>();
builder.Services.AddSingleton<TrafficEvents>();
builder.Services.AddHostedService<TrafficSampler>();
builder.Services.AddSingleton<BindService>();
builder.Services.AddSingleton<DnsStore>();
builder.Services.AddHostedService<DnsSampler>();

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Server-Sent Events for the Traffic and DNS pages: "ready" on connecting, "live" traffic every few seconds,
// "minute" when a minute of traffic is stored, "dns" when a minute of DNS statistics is.
app.MapGet("/events", (TrafficEvents events, IHostApplicationLifetime lifetime, HttpContext context) =>
{
    // End the stream when the page closes or the app shuts down, so shutdown doesn't wait for open pages.
    var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
    context.Response.RegisterForDispose(stop);
    return TypedResults.ServerSentEvents(events.SubscribeAsync(stop.Token));
});

app.Run();
