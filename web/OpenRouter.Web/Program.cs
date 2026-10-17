using OpenRouter.Web.Configuration;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;

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

app.Run();
