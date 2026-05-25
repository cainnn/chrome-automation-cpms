using ChromeAutomation.Client;

await using var chrome = new ChromeController();
await chrome.ConnectAsync();
Console.WriteLine("Connected to bridge server");

var tabs = await chrome.GetTabsAsync();
Console.WriteLine($"Open tabs: {tabs?.GetArrayLength() ?? 0}");

await chrome.NavigateAsync("https://www.example.com");
Console.WriteLine("Navigated to example.com");

var info = await chrome.EvaluateAsync("({ title: document.title, url: location.href })");
Console.WriteLine($"Page info: {info}");

var heading = await chrome.QueryAsync("h1");
Console.WriteLine($"H1 text: {heading?.GetProperty("text").GetString()}");

Console.WriteLine("Done");
