namespace OpenRouter.Web.Tests;

/// <summary>Reads files from web/samples, the same data the app uses in Development.</summary>
internal static class Samples
{
    private static readonly Lazy<string> Directory = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var samples = Path.Combine(dir.FullName, "samples");
            if (File.Exists(Path.Combine(samples, "kea-dhcp4.conf")))
                return samples;
        }
        throw new DirectoryNotFoundException("Could not find web/samples above the test output folder.");
    });

    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory.Value, name));
}
