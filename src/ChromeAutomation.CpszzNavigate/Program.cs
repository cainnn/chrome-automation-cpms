using System.Net.WebSockets;
using ChromeAutomation.Bridge;
using ChromeAutomation.CpszzNavigate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

Console.WriteLine("=== CPSZZ 门户 → 核心ERP → 导出支出明细 → 导入数据库 ===");
Console.WriteLine("请确保：1) Chrome 扩展已连接  2) 浏览器已登录内网");
Console.WriteLine("环境变量: ERP_SKIP_IMPORT=1 仅导出；ERP_PORTAL_URL / ERP_TREE_EXPAND / ERP_CUX_TEXT 可覆盖默认值");
Console.WriteLine();

await StartBridgeIfNeededAsync();

try
{
    await ErpWorkflow.RunAsync(log: Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine("完成。Chrome 保持打开。");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"错误: {ex.Message}");
    Environment.Exit(1);
}

static async Task StartBridgeIfNeededAsync()
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 9333;
    var testUrl = $"ws://127.0.0.1:{port}/";

    try
    {
        using var testWs = new ClientWebSocket();
        await testWs.ConnectAsync(new Uri(testUrl), CancellationToken.None);
        await testWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        Console.WriteLine($"[Bridge] 外部桥接已在端口 {port} 运行");
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
    Console.WriteLine($"[Bridge] 内嵌桥接已启动: ws://127.0.0.1:{port}");
    await Task.Delay(500);
}
