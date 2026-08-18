namespace ChromeAutomation.CpmsExport.Ui;

public class AppSettings
{
    public string ReportUrl { get; set; } = "http://cpms.hq.cmcc/pms/#/mdat/wideTable/BigTableDefind";
    public string DownloadListUrl { get; set; } = "http://cpms.hq.cmcc/pms/#/mops/tools/attachmentDownload/list";
    public string ConnectionString { get; set; } = "Server=localhost,1435;Database=PersonalPMS;User Id=sa;Password=11111a;TrustServerCertificate=True;";
    public string AsposeLicensePath { get; set; } = @"D:\AsposeLicense\Aspose.Total.lic";
    public bool ScheduleEnabled { get; set; }
    public int ScheduleHour { get; set; } = 8;
    public int ScheduleMinute { get; set; } = 0;
    public bool SkipExport { get; set; }
    public bool ForceNewTab { get; set; }

    public string ErpPortalUrl { get; set; } = "http://cpszz.hq.cmcc/oldHome";
    public string ErpTreeExpand { get; set; } = "展开 303310PA_广西全省_项目查询岗";
    public string ErpCuxText { get; set; } = "CUX:查询项目支出";
    public bool ErpSkipImport { get; set; }
}
