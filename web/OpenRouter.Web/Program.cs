using OpenRouter.Web.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection(RouterOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

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
