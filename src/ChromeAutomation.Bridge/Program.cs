using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChromeAutomation.Bridge;

var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var parsedPort)
    ? parsedPort
    : 9333;

var bridge = new BridgeHost();
var builder = WebApplication.CreateBuilder(args);
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

Console.WriteLine($"[Bridge] WebSocket server listening on ws://127.0.0.1:{port}");
Console.WriteLine("[Bridge] Waiting for Chrome extension and controller clients...");

app.Run($"http://127.0.0.1:{port}");
