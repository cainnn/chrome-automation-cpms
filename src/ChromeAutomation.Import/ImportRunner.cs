using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PersonalPMS.ProjectReport.Services;
using PmsContext = PersonalPMSModel.PersonalPMSModel;

namespace ChromeAutomation.Import;

public static class ImportRunner
{
    public static async Task<ImportResult> RunAsync(
        string excelPath,
        string? connectionString = null,
        string? asposeLicensePath = null,
        Action<string>? log = null)
    {
        void Log(string msg)
        {
            if (log != null) log(msg);
            else Console.WriteLine(msg);
        }

        var config = BuildConfiguration();
        var connStr = connectionString
            ?? Environment.GetEnvironmentVariable("NET_IMPORT_CONNECTION")
            ?? config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("未配置数据库连接字符串");

        var licensePath = asposeLicensePath
            ?? Environment.GetEnvironmentVariable("ASPOSE_LICENSE_PATH")
            ?? config["AppSettings:AsposeLicensePath"]
            ?? @"D:\AsposeLicense\Aspose.Total.lic";

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        var options = new DbContextOptionsBuilder<PmsContext>()
            .UseSqlServer(connStr)
            .Options;

        await using var context = new PmsContext(options);
        var excelReader = new ExcelReaderService(loggerFactory.CreateLogger<ExcelReaderService>());
        var importService = new ExcelImportService(context, loggerFactory.CreateLogger<ExcelImportService>(), config);

        if (!File.Exists(excelPath))
            throw new FileNotFoundException($"Excel 文件不存在: {excelPath}");

        Log($"[7/7] 读取 Excel: {Path.GetFileName(excelPath)}");
        excelReader.SetAsposeLicense(licensePath);
        var excelData = excelReader.ReadExcelFile(excelPath);
        Log($"[7/7] Excel {excelData.RowCount} 行 × {excelData.ColumnCount} 列，开始 SqlBulkCopy 批量导入...");

        var result = await importService.ImportExcelToDatabase(excelData);

        if (result.HasError)
        {
            Log($"[7/7] 导入失败: {result.ErrorMessage}");
        }
        else
        {
            Log($"[7/7] 批量导入完成: 处理 {result.TotalRows} 行，写入 {result.InsertedRows} 条，跳过 {result.SkippedRows} 条");
        }

        return result;
    }

    private static IConfiguration BuildConfiguration()
    {
        var basePath = AppContext.BaseDirectory;
        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
    }
}
