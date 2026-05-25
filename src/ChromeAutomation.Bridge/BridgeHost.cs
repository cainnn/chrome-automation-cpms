using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChromeAutomation.Bridge;

public sealed class BridgeHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private WebSocket? _extensionSocket;

    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var isExtension = false;

        try
        {
            await foreach (var message in ReceiveMessagesAsync(socket, cancellationToken))
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "register" &&
                    root.TryGetProperty("role", out var roleProp) &&
                    roleProp.GetString() == "extension")
                {
                    if (_extensionSocket is { State: WebSocketState.Open } existing && existing != socket)
                    {
                        await SafeCloseAsync(existing);
                    }

                    _extensionSocket = socket;
                    isExtension = true;
                    Console.WriteLine("[Bridge] Chrome extension connected");
                    await SendAsync(socket, new { type = "registered", success = true }, cancellationToken);
                    continue;
                }

                if (isExtension || ReferenceEquals(socket, _extensionSocket))
                {
                    if (root.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (id is not null && _pending.TryRemove(id, out var pending))
                        {
                            pending.Completion.TrySetResult(message);
                        }
                    }

                    continue;
                }

                if (root.TryGetProperty("id", out _) && root.TryGetProperty("action", out _))
                {
                    await RouteToExtensionAsync(socket, message, root, cancellationToken);
                }
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected.
        }
        finally
        {
            if (ReferenceEquals(socket, _extensionSocket))
            {
                _extensionSocket = null;
                Console.WriteLine("[Bridge] Chrome extension disconnected");
            }
        }
    }

    private async Task RouteToExtensionAsync(
        WebSocket controller,
        string rawMessage,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var id = message.GetProperty("id").GetString();
        if (id is null)
        {
            return;
        }

        if (_extensionSocket is not { State: WebSocketState.Open })
        {
            await SendAsync(controller, new
            {
                id,
                success = false,
                data = (object?)null,
                error = "Chrome extension not connected. Load the extension and ensure bridge is running.",
            }, cancellationToken);
            return;
        }

        var pending = new PendingRequest();
        if (!_pending.TryAdd(id, pending))
        {
            await SendAsync(controller, new
            {
                id,
                success = false,
                data = (object?)null,
                error = "Duplicate request id.",
            }, cancellationToken);
            return;
        }

        try
        {
            await SendRawAsync(_extensionSocket, rawMessage, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutSeconds = GetRequestTimeoutSeconds(message);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var response = await pending.Completion.Task.WaitAsync(timeoutCts.Token);
            await SendRawAsync(controller, response, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            await SendAsync(controller, new
            {
                id,
                success = false,
                data = (object?)null,
                error = "Request timeout (120s)",
            }, cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static int GetRequestTimeoutSeconds(JsonElement message)
    {
        if (message.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("timeoutMs", out var timeoutMsProp) &&
            timeoutMsProp.TryGetInt32(out var timeoutMs))
        {
            return Math.Clamp(timeoutMs / 1000 + 5, 30, 600);
        }

        if (message.TryGetProperty("action", out var actionProp))
        {
            var action = actionProp.GetString() ?? "";
            if (action.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("cpmsDownload", StringComparison.OrdinalIgnoreCase) ||
                action is "startDownload" or "waitForDownload" or "acceptPendingDownloads")
            {
                return 300;
            }
        }

        return 120;
    }

    private static async IAsyncEnumerable<string> ReceiveMessagesAsync(
        WebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }
        }
    }

    private static Task SendAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return SendRawAsync(socket, json, cancellationToken);
    }

    private static Task SendRawAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task SafeCloseAsync(WebSocket socket)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch
            {
                // Ignore close errors.
            }
        }
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
