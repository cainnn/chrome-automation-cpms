# CPMS 下载故障排查

本文档合并了自动化运行中实际出现的错误日志与对应处理（`last-run.log` / `last-run2.log`）。

## 已知失败模式

| 日志关键词 | 含义 | 处理 |
|-----------|------|------|
| `下载策略: click-only` | 只点了「下载」，未拿到可 fetch 的 URL | 使用扩展 ≥1.1.0；确保走 `cpmsResolveDownloadUrl` → blob-bypass |
| `cpmsDownloadBySerial: Request timeout (120s)` | 扩展未刷新或标签页不对 | 在 `chrome://extensions/` 重载扩展；确认在「我的下载」列表页 |
| `捕获 URL: .../operationLog/add` | 误抓操作日志接口 | 已在 `isLikelyDownloadUrl` 中排除 |
| `acceptDanger` / `全自动下载（blob-bypass + acceptDanger` | 旧版 C# 并行点击 + acceptDanger | 升级到当前 main；`acceptPendingDownloads` 已为 no-op |
| `Action failed in all frames: clickByText` | 回退点击失败 | 勿依赖 clickByText；使用 `cpmsDownloadBySerial` |
| `A task was canceled` | 用户中断或总超时 | 增大 `CompleteDownloadStepAsync` 超时或检查网络 |

## 架构说明（为何不用 acceptDanger）

1. **MV3 service worker 不是 visible context**，`chrome.downloads.acceptDanger()` 调用会静默失败。
2. **`acceptDanger` 语义是再弹确认框**，不能代替用户点「保留」。
3. **正确路径**：`cpmsResolveDownloadUrl` 解析 URL → `downloadWithSessionCookies`（fetch + `blob:` URL）保存，不触发 Chrome「不安全下载」条。

## 推荐下载测试

```powershell
cd D:\浏览器自动化项目\src
$env:CPMS_UNATTENDED = "1"
$env:CPMS_SKIP_EXPORT = "1"
# 可选: $env:CPMS_SERIAL = "你的流水号"
.\run-cpms-export.ps1
```

成功日志应包含：`下载策略: blob-bypass` 或 `blob-bypass-sniff`，且 `Downloads` 目录出现 `cpms-export-<流水号>.zip`。
