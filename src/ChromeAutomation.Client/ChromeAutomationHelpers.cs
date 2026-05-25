using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChromeAutomation.Client;

public static partial class ChromeAutomationHelpers
{
    public static Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default) =>
        Task.Delay(milliseconds, cancellationToken);

    public static Task<JsonElement?> ClickByTextAsync(
        this ChromeController chrome,
        string text,
        bool exact = false,
        int? tabId = null,
        CancellationToken cancellationToken = default) =>
        chrome.CommandAsync("clickByText", new { text, exact, tabId }, cancellationToken);

    public static async Task<JsonElement?> WaitForTextAsync(
        this ChromeController chrome,
        string text,
        int timeoutMs = 30000,
        bool exact = false,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await chrome.GetBodyTextAsync(tabId, cancellationToken);
            if (!string.IsNullOrEmpty(body))
            {
                if (exact ? body.Split('\n').Any(line => line.Trim() == text) : body.Contains(text, StringComparison.Ordinal))
                {
                    return JsonSerializer.SerializeToElement(new { found = true, text });
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"Timeout waiting for text: {text}");
    }

    public static async Task<string?> GetBodyTextAsync(
        this ChromeController chrome,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await chrome.CommandAsync("getBodyText", new { tabId }, cancellationToken);
        if (result?.TryGetProperty("text", out var textProp) == true)
        {
            return textProp.GetString();
        }

        return null;
    }

    public static async Task<string?> ExtractSerialNumberAsync(
        this ChromeController chrome,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        var text = await chrome.GetBodyTextAsync(tabId, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SerialNumberRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"流水号[为：:\s]*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SerialNumberRegex();
}
