using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpenRouter.Web.Pages;

/// <summary>
/// Base for pages that read router state. A source that can't be read is reported on the page
/// instead of failing the request, so one broken file doesn't hide everything else.
/// </summary>
public abstract class RouterPageModel(ILogger logger) : PageModel
{
    public List<string> Errors { get; } = [];

    protected async Task<T?> TryLoadAsync<T>(string source, Func<Task<T>> load) where T : class
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or JsonException or InvalidOperationException or SocketException or Win32Exception)
        {
            logger.LogWarning(ex, "Could not read {Source}", source);
            Errors.Add($"Could not read {source}: {ex.Message}");
            return null;
        }
    }
}
