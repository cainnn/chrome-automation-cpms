using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChromeAutomation.Client;

public sealed class ChromeController : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ClientWebSocket _webSocket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _url;

    public ChromeController(string url = "ws://127.0.0.1:9333/")
    {
        _url = url.EndsWith('/') ? url : url + "/";
    }

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            return;
        }

        await _webSocket.ConnectAsync(new Uri(_url), cancellationToken);
    }

    public async Task<JsonElement?> CommandAsync(
        string action,
        object? parameters = null,
        CancellationToken cancellationToken = default,
        int? timeoutMs = null)
    {
        var requestId = Guid.NewGuid().ToString();
        var effectiveTimeoutMs = timeoutMs ?? GetDefaultTimeoutMs(action);

        JsonObject paramObj = parameters is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(parameters, JsonOptions)!.AsObject();
        paramObj["timeoutMs"] = effectiveTimeoutMs;

        var payload = JsonSerializer.Serialize(new
        {
            id = requestId,
            action,
            @params = paramObj,
        }, JsonOptions);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await SendAsync(payload, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMs + 10000));

            while (true)
            {
                var message = await ReceiveAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (!root.TryGetProperty("id", out var idProp) || idProp.GetString() != requestId)
                {
                    continue;
                }

                if (root.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
                {
                    if (root.TryGetProperty("data", out var dataProp))
                    {
                        return dataProp.Clone();
                    }

                    return null;
                }

                var error = root.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString()
                    : "Unknown error";
                throw new InvalidOperationException(error);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task<JsonElement?> GetTabsAsync(CancellationToken cancellationToken = default) =>
        CommandAsync("getTabs", cancellationToken: cancellationToken);

    public Task<JsonElement?> NavigateAsync(
        string url,
        string? waitUntil = "load",
        int? tabId = null,
        string? recreateUrl = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("navigate", new { url, waitUntil, tabId, recreateUrl }, cancellationToken);

    public Task<JsonElement?> ClickAsync(
        string selector,
        int? tabId = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("click", new { selector, tabId }, cancellationToken);

    public Task<JsonElement?> TypeAsync(
        string selector,
        string text,
        int? tabId = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("type", new { selector, text, tabId }, cancellationToken);

    public Task<JsonElement?> QueryAsync(
        string selector,
        int? tabId = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("query", new { selector, tabId }, cancellationToken);

    public Task<JsonElement?> EvaluateAsync(
        string code,
        int? tabId = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("evaluate", new { code, tabId }, cancellationToken);

    public Task<JsonElement?> ScreenshotAsync(
        int? tabId = null,
        string format = "png",
        CancellationToken cancellationToken = default) =>
        CommandAsync("screenshot", new { tabId, format }, cancellationToken);

    public Task<JsonElement?> EnableAutoAcceptDownloadsAsync(
        bool enabled = true,
        int durationMs = 20 * 60 * 1000,
        CancellationToken cancellationToken = default) =>
        CommandAsync("enableAutoAcceptDownloads", new { enabled, durationMs }, cancellationToken);

    public Task<JsonElement?> DisableAutoAcceptDownloadsAsync(
        CancellationToken cancellationToken = default) =>
        CommandAsync("disableAutoAcceptDownloads", new { }, cancellationToken);

    public Task<JsonElement?> AcceptPendingDownloadsAsync(
        CancellationToken cancellationToken = default) =>
        CommandAsync("acceptPendingDownloads", new { }, cancellationToken);

    public Task<JsonElement?> WaitForDownloadAsync(
        string? filenameContains = null,
        int timeoutMs = 120000,
        DateTime? since = null,
        CancellationToken cancellationToken = default) =>
        CommandAsync("waitForDownload", new
        {
            filenameContains = filenameContains ?? "",
            timeout = timeoutMs,
            sinceMs = new DateTimeOffset(since ?? DateTime.UtcNow).ToUnixTimeMilliseconds(),
        }, cancellationToken, timeoutMs: timeoutMs + 5000);

    private static int GetDefaultTimeoutMs(string action)
    {
        if (action.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith("cpmsDownload", StringComparison.OrdinalIgnoreCase) ||
            action is "startDownload" or "waitForDownload" or "acceptPendingDownloads")
        {
            return 300000;
        }

        return 125000;
    }

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to bridge server.");
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Bridge connection closed.");
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sendLock.Dispose();

        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
            catch
            {
                // Ignore close errors.
            }
        }

        _webSocket.Dispose();
    }
}
