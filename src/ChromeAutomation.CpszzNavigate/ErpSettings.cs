namespace ChromeAutomation.CpszzNavigate;

/// <summary>ERP 工作流可配置项（环境变量优先）。</summary>
public sealed class ErpSettings
{
    public string PortalUrl { get; init; } =
        Environment.GetEnvironmentVariable("ERP_PORTAL_URL")
        ?? "http://cpszz.hq.cmcc/oldHome";

    public string TreeExpandText { get; init; } =
        Environment.GetEnvironmentVariable("ERP_TREE_EXPAND")
        ?? "展开 303310PA_广西全省_项目查询岗";

    public string CuxLinkText { get; init; } =
        Environment.GetEnvironmentVariable("ERP_CUX_TEXT")
        ?? "CUX:查询项目支出";

    public string[] ExportButtonHints { get; init; } =
        (Environment.GetEnvironmentVariable("ERP_EXPORT_BUTTONS")
         ?? "导出,Export,输出,Output,电子表格,Spreadsheet,下载,另存为,Save")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool SkipImport { get; init; } =
        string.Equals(Environment.GetEnvironmentVariable("ERP_SKIP_IMPORT"), "1", StringComparison.OrdinalIgnoreCase);

    public bool StopAtForms { get; init; } =
        string.Equals(Environment.GetEnvironmentVariable("ERP_STOP_AT_FORMS"), "1", StringComparison.OrdinalIgnoreCase);

    public static ErpSettings FromEnvironment() => new();
}
