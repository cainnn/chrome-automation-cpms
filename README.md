# Chrome Automation Bridge

通过 Chrome 扩展 + WebSocket 桥接服务器，让 **C# 程序** 控制 Chrome 浏览器。

## 架构

```
┌─────────────┐     WebSocket      ┌──────────────┐     WebSocket     ┌─────────────┐
│  C# 控制程序 │ ◄────────────────► │ Bridge 服务器 │ ◄───────────────► │ Chrome 扩展 │
│  (.NET 8)   │   ws://127.0.0.1   │  (localhost)  │                   │ (浏览器内)  │
└─────────────┘       :9333        └──────────────┘                   └─────────────┘
```

扩展在浏览器内执行实际操作（导航、点击、输入、截图等），C# 程序通过 WebSocket 发送 JSON 命令。

## 环境要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download) 或更高版本
- Google Chrome 浏览器

## 快速开始

### 1. 启动桥接服务器

```powershell
cd src
dotnet run --project ChromeAutomation.Bridge
```

服务器默认监听 `ws://127.0.0.1:9333`。

### 2. 加载 Chrome 扩展

1. 打开 Chrome，访问 `chrome://extensions/`
2. 开启右上角「开发者模式」
3. 点击「加载已解压的扩展程序」
4. 选择项目中的 `extension` 文件夹
5. 点击扩展图标，确认状态为「已连接到桥接服务器」

### 3. 运行 C# 示例

```powershell
cd src
dotnet run --project ChromeAutomation.Example
```

## 在你的项目中使用

引用客户端 SDK：

```xml
<ProjectReference Include="path\to\ChromeAutomation.Client\ChromeAutomation.Client.csproj" />
```

代码示例：

```csharp
await using var chrome = new ChromeController("ws://127.0.0.1:9333/");
await chrome.ConnectAsync();

await chrome.NavigateAsync("https://www.baidu.com");
await chrome.TypeAsync("#kw", "Chrome 自动化");
await chrome.ClickAsync("#su");

var result = await chrome.QueryAsync("#content_left");
Console.WriteLine(result);
```

## 支持的命令

| action | 说明 | 参数 |
|--------|------|------|
| `getTabs` | 获取所有标签页 | — |
| `createTab` | 新建标签页 | `url`, `active` |
| `closeTab` | 关闭标签页 | `tabId` |
| `activateTab` | 激活标签页 | `tabId` |
| `navigate` | 导航到 URL | `url`, `tabId`, `waitUntil`, `timeout` |
| `click` | 点击元素 | `selector`, `tabId` |
| `type` | 输入文本 | `selector`, `text`, `tabId` |
| `query` | 查询单个元素 | `selector`, `tabId` |
| `queryAll` | 查询多个元素 | `selector`, `tabId` |
| `waitFor` | 等待元素出现 | `selector`, `timeout`, `tabId` |
| `scroll` | 滚动页面/元素 | `selector` 或 `x`/`y`, `tabId` |
| `getPageInfo` | 获取页面信息 | `tabId` |
| `evaluate` | 执行 JavaScript | `code`, `tabId` |
| `screenshot` | 截图当前标签页 | `tabId`, `format` |

通用调用方式：

```csharp
var data = await chrome.CommandAsync("waitFor", new { selector = "#app", timeout = 5000 });
```

## 协议格式

**请求（C# → 桥接 → 扩展）：**

```json
{
  "id": "uuid",
  "action": "navigate",
  "params": { "url": "https://example.com", "waitUntil": "load" }
}
```

**响应：**

```json
{
  "id": "uuid",
  "success": true,
  "data": { "id": 123, "url": "https://example.com", "title": "Example" },
  "error": null
}
```

## 项目结构

```
├── extension/                    # Chrome 扩展 (Manifest V3)
│   ├── manifest.json
│   ├── background.js
│   └── popup/
└── src/
    ├── ChromeAutomation.sln
    ├── ChromeAutomation.Bridge/  # WebSocket 桥接服务器
    ├── ChromeAutomation.Client/  # C# 客户端 SDK
    └── ChromeAutomation.Example/ # 示例程序
```

## 注意事项

- 桥接服务器和扩展必须同时运行
- 扩展需要「标签页」和「脚本」权限才能操控页面
- `evaluate` 会在页面上下文中执行任意 JS，请仅在可信环境使用
- 部分网站（如 `chrome://` 页面）无法注入脚本
- 修改 WebSocket 地址后，在扩展弹窗中保存并重连

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `BRIDGE_PORT` | `9333` | 桥接服务器端口 |
