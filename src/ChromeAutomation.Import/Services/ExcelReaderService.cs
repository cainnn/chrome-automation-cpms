using Aspose.Cells;
using Microsoft.Extensions.Logging;

namespace PersonalPMS.ProjectReport.Services
{
    public class ExcelData
    {
        public List<object[]> Rows { get; set; } = new List<object[]>();
        public int RowCount { get; set; }
        public int ColumnCount { get; set; }
    }

    public class ExcelReaderService
    {
        private readonly ILogger<ExcelReaderService> _logger;

        public ExcelReaderService(ILogger<ExcelReaderService> logger)
        {
            _logger = logger;
        }

        public void SetAsposeLicense(string licensePath)
        {
            try
            {
                if (File.Exists(licensePath))
                {
                    License license = new License();
                    license.SetLicense(licensePath);
                    _logger.LogInformation("Aspose.Cells license set successfully from: {LicensePath}", licensePath);
                }
                else
                {
                    _logger.LogWarning("License file not found at: {LicensePath}", licensePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Aspose.Cells license");
            }
        }

        public ExcelData ReadExcelFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogError("Excel file not found at: {FilePath}", filePath);
                    return new ExcelData { Rows = new List<object[]>(), RowCount = 0, ColumnCount = 0 };
                }

                _logger.LogInformation("Reading Excel file: {FilePath}", filePath);

                // 加载工作簿
                Workbook workbook = new Workbook(filePath);
                Worksheet worksheet = workbook.Worksheets[0]; // 获取第一个工作表

                // 获取数据范围
                int maxRow = worksheet.Cells.MaxDataRow;
                int maxCol = worksheet.Cells.MaxDataColumn;

                _logger.LogInformation("Excel file loaded. Rows: {MaxRow}, Columns: {MaxCol}", maxRow + 1, maxCol + 1);

                // 读取所有数据
                var rows = new List<object[]>();
                for (int row = 0; row <= maxRow; row++)
                {
                    var rowData = new object[maxCol + 1];
                    for (int col = 0; col <= maxCol; col++)
                    {
                        rowData[col] = worksheet.Cells[row, col].StringValue;
                    }
                    rows.Add(rowData);
                }

                _logger.LogInformation("Excel file read successfully");
                
                return new ExcelData 
                { 
                    Rows = rows, 
                    RowCount = maxRow + 1, 
                    ColumnCount = maxCol + 1 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading Excel file: {FilePath}", filePath);
                Console.WriteLine($"读取Excel文件时发生错误: {ex.Message}");
                return new ExcelData { Rows = new List<object[]>(), RowCount = 0, ColumnCount = 0 };
            }
        }

        private static string TruncateString(string? input, int maxLength)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Length <= maxLength ? input : input.Substring(0, maxLength - 3) + "...";
        }
    }
} 