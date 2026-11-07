using System.Diagnostics;

namespace OpenRouter.Web.Processes;

/// <summary>Runs a program directly (no shell) and returns its standard output.</summary>
public static class CommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {program}.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} timed out.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} failed: {(await error).Trim()}");
        return await output;
    }
}
