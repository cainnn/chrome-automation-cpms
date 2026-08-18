using System.Net.WebSockets;
using ChromeAutomation.Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ChromeAutomation.CpszzNavigate;

/// <summary>供 UI 调用的 ERP 工作流运行器。</summary>
public class ErpWorkflowRunner
{
    public event Action<string>? Log;
    public event Action? IsRunningChanged;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; IsRunningChanged?.Invoke(); }
    }

    private void WriteLog(string message) => Log?.Invoke(message);

    public async Task RunFullWorkflowAsync(ErpSettings settings, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Already running");
        IsRunning = true;
        try
        {
            await StartBridgeIfNeededAsync(ct);
            await ErpWorkflow.RunAsync(settings, WriteLog, ct);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async Task StartBridgeIfNeededAsync(CancellationToken ct)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 9333;
        var testUrl = $"ws://127.0.0.1:{port}/";

        try
        {
            using var testWs = new ClientWebSocket();
            await testWs.ConnectAsync(new Uri(testUrl), ct);
            await testWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", ct);
            return;
        }
        catch (WebSocketException) { }

        var bridge = new BridgeHost();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await bridge.HandleConnectionAsync(socket, context.RequestAborted);
        });

        _ = app.RunAsync();
        await Task.Delay(500, ct);
    }
}
