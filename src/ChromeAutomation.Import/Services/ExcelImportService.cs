using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Data.Common;
using System.Data;
using System.Reflection;

namespace PersonalPMS.ProjectReport.Services
{
    public class ExcelImportService
    {
        private const int DefaultImportBatchSize = 800;
        private const int DefaultSqlCommandTimeoutSeconds = 600;

        private readonly PersonalPMSModel.PersonalPMSModel _context;
        private readonly ILogger<ExcelImportService> _logger;
        private readonly int _importBatchSize;
        private readonly int _sqlCommandTimeoutSeconds;

        public ExcelImportService(
            PersonalPMSModel.PersonalPMSModel context,
            ILogger<ExcelImportService> logger,
            IConfiguration configuration)
        {
            _context = context;
            _logger = logger;

            _importBatchSize = configuration.GetValue<int?>("AppSettings:ImportBatchSize") ?? DefaultImportBatchSize;
            _sqlCommandTimeoutSeconds = configuration.GetValue<int?>("AppSettings:SqlCommandTimeoutSeconds") ?? DefaultSqlCommandTimeoutSeconds;

            // 边界保护，避免配置异常导致导入逻辑不可用
            if (_importBatchSize <= 0) _importBatchSize = DefaultImportBatchSize;
            if (_sqlCommandTimeoutSeconds <= 0) _sqlCommandTimeoutSeconds = DefaultSqlCommandTimeoutSeconds;
        }

        public async Task<ImportResult> ImportExcelToDatabase(ExcelData excelData)
        {
            var result = new ImportResult();
            try
            {
                _logger.LogInformation("开始 SqlBulkCopy 批量导入...");
                if (excelData.RowCount < 3) return result;

                // 1. 自动检测标题行（第0行或第1行）
                var headerRow = excelData.Rows[0];
                var firstValue = headerRow[0]?.ToString()?.Trim();
                // 如果第0行第一个值看起来像数据（项目编码），则尝试第1行
                if (!string.IsNullOrEmpty(firstValue) && firstValue.Length > 5 && !firstValue.Contains("项目"))
                {
                    headerRow = excelData.Rows[1];
                }
                var columnMap = BuildColumnMap(headerRow);

                // 2. 构建列名到类型的映射
                var columnTypeMap = BuildColumnTypeMap(columnMap);

                // 3. 转换Excel数据为实体对象
                var entities = new List<PersonalPMSModel.PMS项目明细报表>();

                for (int rowIndex = 2; rowIndex < excelData.RowCount; rowIndex++)
                {
                    var row = excelData.Rows[rowIndex];
                    var projectCode = row[0]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(projectCode))
                    {
                        result.SkippedRows++;
                        continue;
                    }

                    var entity = ConvertRowToEntity(row, columnMap, columnTypeMap, rowIndex);
                    if (entity != null)
                    {
                        entities.Add(entity);
                    }
                }

                // 4. 获取Excel中实际存在的字段列表（用于比较）
                var excelFields = new HashSet<string>(columnMap.Values, StringComparer.OrdinalIgnoreCase);
                
                // 5. 使用EF Extensions进行批量操作
                if (entities.Any())
                {
                    await PerformBulkUpsert(entities, excelFields, result);
                }

                result.TotalRows = excelData.RowCount - 2;
                _logger.LogInformation($"导入完成: 总行数={result.TotalRows}, 新增={result.InsertedRows}, 更新={result.UpdatedRows}, 跳过={result.SkippedRows}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入Excel数据时发生错误");
                result.HasError = true;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        // Excel列名到数据库字段名的完整映射字典
        private static readonly Dictionary<string, string> ExcelToDbColumnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 带横线的字段映射
            { "工程管理经理-主", "工程管理经理主" },
            { "[工程管理经理-主]", "工程管理经理主" },
            { "工程管理经理-主所属二级组织", "工程管理经理主所属二级组织" },
            { "[工程管理经理-主所属二级组织]", "工程管理经理主所属二级组织" },
            { "工程管理经理-主所属组织", "工程管理经理主所属组织" },
            { "[工程管理经理-主所属组织]", "工程管理经理主所属组织" },
            { "工程管理经理-辅", "工程管理经理辅" },
            { "[工程管理经理-辅]", "工程管理经理辅" },
            { "工程实施经理-主", "工程实施经理主" },
            { "[工程实施经理-主]", "工程实施经理主" },
            { "工程实施经理主所属组织", "工程实施经理主所属组织" },
            { "[工程实施经理主所属组织]", "工程实施经理主所属组织" },
            { "工程实施经理-辅", "工程实施经理辅" },
            { "[工程实施经理-辅]", "工程实施经理辅" },
            
            // 带斜线的字段映射
            { "项目取消/终止/失效时间", "项目取消终止失效时间" },
            { "项目取消/终止/失效原因", "项目取消终止失效原因" },
            
            // 带括号的字段映射
            { "是否研发相关（PMS）", "是否研发相关PMS" },
            { "是否研发相关(PMS)", "是否研发相关PMS" },
            { "是否研发相关（RMS）", "是否研发相关RMS" },
            { "是否研发相关(RMS)", "是否研发相关RMS" },
            
            // 项目阶段映射
            { "项目阶段", "项目阶段新" },
            
            // 带"（填报）"的字段映射
            { "需求评审完成（填报）时间", "需求评审完成填报时间" },
            { "需求评审完成(填报)时间", "需求评审完成填报时间" },
            { "建设方案评审完成（填报）时间", "建设方案评审完成填报时间" },
            { "建设方案评审完成(填报)时间", "建设方案评审完成填报时间" },
            { "立项可研委托完成（填报）时间", "立项可研委托完成填报时间" },
            { "立项可研委托完成(填报)时间", "立项可研委托完成填报时间" },
            { "立项可研编制完成（填报）时间", "立项可研编制完成填报时间" },
            { "立项可研编制完成(填报)时间", "立项可研编制完成填报时间" },
            { "立项可研会审完成（填报）时间", "立项可研会审完成填报时间" },
            { "立项可研会审完成(填报)时间", "立项可研会审完成填报时间" },
            { "上会决策完成（填报）时间", "上会决策完成填报时间" },
            { "上会决策完成(填报)时间", "上会决策完成填报时间" },
            { "第一次立项决策完成（填报）时间", "第一次立项决策完成填报时间" },
            { "第一次立项决策完成(填报)时间", "第一次立项决策完成填报时间" },
            { "立项决策完成（填报）时间", "立项决策完成填报时间" },
            { "立项决策完成(填报)时间", "立项决策完成填报时间" },
            { "项目可研委托完成（填报）时间", "项目可研委托完成填报时间" },
            { "项目可研委托完成(填报)时间", "项目可研委托完成填报时间" },
            { "项目可研编制完成（填报）时间", "项目可研编制完成填报时间" },
            { "项目可研编制完成(填报)时间", "项目可研编制完成填报时间" },
            { "项目可研会审完成（填报）时间", "项目可研会审完成填报时间" },
            { "项目可研会审完成(填报)时间", "项目可研会审完成填报时间" },
            { "质监申报开始（填报）时间", "质监申报开始填报时间" },
            { "质监申报开始(填报)时间", "质监申报开始填报时间" },
            { "质监申报完成（填报）时间", "质监申报完成填报时间" },
            { "质监申报完成(填报)时间", "质监申报完成填报时间" },
            { "开工报告完成（填报）时间", "开工报告完成填报时间" },
            { "开工报告完成(填报)时间", "开工报告完成填报时间" },
            { "完工报告完成（填报）时间", "完工报告完成填报时间" },
            { "完工报告完成(填报)时间", "完工报告完成填报时间" },
            { "割接上线交维（填报）时间", "割接上线交维填报时间" },
            { "割接上线交维(填报)时间", "割接上线交维填报时间" },
            { "试运行完成（填报）时间", "试运行完成填报时间" },
            { "试运行完成(填报)时间", "试运行完成填报时间" },
            { "竣工备案申请（填报）时间", "竣工备案完成填报时间" },
            { "竣工备案申请(填报)时间", "竣工备案完成填报时间" },
            
            // 带括号的金额字段映射
            { "立项变更批复金额(只统计变更)", "立项变更批复金额只统计变更" },
            { "立项变更批复金额（只统计变更）", "立项变更批复金额只统计变更" },
            { "设计批复造价-设计费", "设计批复造价设计费" },
            { "设计批复造价-施工费", "设计批复造价施工费" },
            { "设计批复造价-监理费", "设计批复造价监理费" },
            { "设计批复造价-安全生产费", "设计批复造价安全生产费" },
            { "设计批复造价-设备费", "设计批复造价设备费" },
            { "设计批复造价-其他费", "设计批复造价其他费" },
            { "设计批复造价-甲供材料费", "设计批复造价甲供材料费" },
            { "设计批复造价-可研费", "设计批复造价可研费" },
            { "设计批复造价-总金额", "设计批复造价总金额" },
            { "设计变更批复总金额(只统计变更)", "设计变更批复总金额只统计变更" },
            { "设计变更批复总金额（只统计变更）", "设计变更批复总金额只统计变更" },
            { "现场签证申请总金额（仅统计审批完成）", "现场签证申请总金额仅统计审批完成" },
            { "现场签证申请总金额(仅统计审批完成)", "现场签证申请总金额仅统计审批完成" },
            { "现场签证报告总金额（仅统计审批完成）", "现场签证报告总金额仅统计审批完成" },
            { "现场签证报告总金额(仅统计审批完成)", "现场签证报告总金额仅统计审批完成" },
            { "设计费订单金额(PMS)", "设计费订单金额PMS" },
            { "设计费订单金额（PMS）", "设计费订单金额PMS" },
            { "施工费订单金额(PMS)", "施工费订单金额PMS" },
            { "施工费订单金额（PMS）", "施工费订单金额PMS" },
            { "监理费订单金额(PMS)", "监理费订单金额PMS" },
            { "监理费订单金额（PMS）", "监理费订单金额PMS" },
            { "当年资本开支计划（一季度）", "当年资本开支计划一季度" },
            { "当年资本开支计划(一季度)", "当年资本开支计划一季度" },
            { "当年资本开支计划（二季度）", "当年资本开支计划二季度" },
            { "当年资本开支计划(二季度)", "当年资本开支计划二季度" },
            { "当年资本开支计划（三季度）", "当年资本开支计划三季度" },
            { "当年资本开支计划(三季度)", "当年资本开支计划三季度" },
            { "当年资本开支计划（四季度）", "当年资本开支计划四季度" },
            { "当年资本开支计划(四季度)", "当年资本开支计划四季度" },
            
            // 带括号的进度计划字段映射
            { "总体进度计划-立项批复计划完成时间", "总体进度计划立项批复计划完成时间" },
            { "总体进度计划-项目采购计划完成时间", "总体进度计划项目采购计划完成时间" },
            { "总体进度计划-设计批复计划完成时间", "总体进度计划设计批复计划完成时间" },
            { "总体进度计划-开工计划完成时间", "总体进度计划开工计划完成时间" },
            { "总体进度计划-割接上线计划完成时间", "总体进度计划割接上线计划完成时间" },
            { "总体进度计划-竣工验收计划完成时间", "总体进度计划竣工验收计划完成时间" },
            { "总体进度计划-项目关闭计划完成时间", "总体进度计划项目关闭计划完成时间" },
            
            // 带括号的其他字段映射
            { "设计批复完成时间（最后一次）", "设计批复完成时间最后一次" },
            { "设计批复完成时间(最后一次)", "设计批复完成时间最后一次" },
            { "监理规划（细则）审批完成时间", "监理规划细则审批完成时间" },
            { "监理规划(细则)审批完成时间", "监理规划细则审批完成时间" },
            { "监理规划（总结）审批完成时间", "监理规划总结审批完成时间" },
            { "监理规划(总结)审批完成时间", "监理规划总结审批完成时间" },
            { "开工率(项目下的任务开工率)", "开工率项目下的任务开工率" },
            { "开工率（项目下的任务开工率）", "开工率项目下的任务开工率" },
            { "完工率(项目下的任务完工率)", "完工率项目下的任务完工率" },
            { "完工率（项目下的任务完工率）", "完工率项目下的任务完工率" },
            { "割接完成率(项目下的任务割接完成率)", "割接完成率项目下的任务割接完成率" },
            { "割接完成率（项目下的任务割接完成率）", "割接完成率项目下的任务割接完成率" }
        };

        // 标准化Excel列名，将Excel列名映射到数据库字段名
        private string NormalizeColumnName(string excelColumnName)
        {
            if (string.IsNullOrEmpty(excelColumnName))
                return excelColumnName;
            
            var originalName = excelColumnName.Trim();
            
            // 1. 首先检查完整匹配（包括方括号）
            if (ExcelToDbColumnMap.TryGetValue(originalName, out var mappedName))
                return mappedName;
            
            // 2. 移除方括号后再次检查
            var withoutBrackets = originalName.TrimStart('[').TrimEnd(']');
            if (withoutBrackets != originalName && ExcelToDbColumnMap.TryGetValue(withoutBrackets, out mappedName))
                return mappedName;
            
            // 3. 通用规则处理
            var normalized = withoutBrackets;
            
            // 移除横线（将"-主"转换为"主"，"-辅"转换为"辅"）
            normalized = normalized.Replace("-主", "主");
            normalized = normalized.Replace("-辅", "辅");
            
            // 移除斜线（将"/"转换为空）
            normalized = normalized.Replace("/", "");
            
            // 移除全角括号中的内容（中文括号）
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"（[^）]*）", "");
            // 移除半角括号中的内容（英文括号）
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\([^)]*\)", "");
            
            // 移除横线（在移除括号后）
            normalized = normalized.Replace("-", "");
            
            return normalized;
        }

        // 构建列名映射（Excel列索引 -> 标准化后的列名）
        private Dictionary<int, string> BuildColumnMap(object[] headerRow)
        {
            var map = new Dictionary<int, string>();
            for (int i = 0; i < headerRow.Length; i++)
            {
                var colName = headerRow[i]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(colName))
                {
                    map[i] = NormalizeColumnName(colName);
                }
            }
            return map;
        }

        // 构建列名到类型的映射
        private Dictionary<string, Type> BuildColumnTypeMap(Dictionary<int, string> columnMap)
        {
            var typeMap = new Dictionary<string, Type>();
            var props = typeof(PersonalPMSModel.PMS项目明细报表).GetProperties();
            foreach (var kv in columnMap)
            {
                var colName = kv.Value; // 这里已经是标准化后的列名
                var prop = props.FirstOrDefault(p => p.Name == colName);
                if (prop != null)
                {
                    typeMap[colName] = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                }
                else
                {
                    _logger.LogWarning($"未找到对应的属性: '{colName}'");
                }
            }
            return typeMap;
        }

        // 数据库中不存在的字段列表（这些字段在EF Core中被Ignore了）
        private static readonly HashSet<string> IgnoredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "总体进度计划开工计划完成时间",
            "总体进度计划割接上线计划完成时间",
            "总体进度计划竣工验收计划完成时间",
            "工程实施经理主所属组织"
        };

        // 将Excel行转换为实体对象
        private PersonalPMSModel.PMS项目明细报表? ConvertRowToEntity(object[] row, Dictionary<int, string> columnMap, Dictionary<string, Type> columnTypeMap, int rowIndex)
        {
            try
            {
                var entity = new PersonalPMSModel.PMS项目明细报表();
                bool hasValidData = false;

                foreach (var kv in columnMap)
                {
                    int colIndex = kv.Key;
                    string colName = kv.Value;
                    if (colIndex >= row.Length) continue;

                    // 跳过数据库中不存在的字段
                    if (IgnoredFields.Contains(colName))
                    {
                        continue;
                    }

                    var cellValue = row[colIndex]?.ToString();
                    var convertedValue = ConvertToPropertyType(cellValue, columnTypeMap.GetValueOrDefault(colName), colName, rowIndex);
                    
                    if (convertedValue != null)
                    {
                        var property = typeof(PersonalPMSModel.PMS项目明细报表).GetProperty(colName);
                        if (property != null && property.CanWrite)
                        {
                            property.SetValue(entity, convertedValue);
                            hasValidData = true;
                        }
                    }
                }

                return hasValidData ? entity : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"转换第{rowIndex + 1}行数据时发生错误: {ex.Message}");
                return null;
            }
        }

        // 类型转换辅助方法
        private object? ConvertToPropertyType(string? value, Type? targetType, string columnName, int rowIndex)
        {
            if (targetType == null) return value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            try
            {
                var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (underlyingType == typeof(string)) return value;
                if (underlyingType == typeof(int)) return int.Parse(value);
                if (underlyingType == typeof(long)) return long.Parse(value);
                if (underlyingType == typeof(decimal)) return decimal.Parse(value);
                if (underlyingType == typeof(double)) return double.Parse(value);
                if (underlyingType == typeof(float)) return float.Parse(value);
                if (underlyingType == typeof(DateTime))
                {
                    // 尝试多种日期格式
                    if (DateTime.TryParse(value, out var dateTime))
                    {
                        return dateTime;
                    }
                    // 尝试常见的日期格式
                    string[] dateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d", "yyyy-MM-d", "yyyy-M-dd" };
                    if (DateTime.TryParseExact(value, dateFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dateTime))
                    {
                        return dateTime;
                    }
                    _logger.LogWarning($"第{rowIndex + 1}行，列[{columnName}]，值[{value}] 无法解析为日期");
                    return null;
                }
                if (underlyingType == typeof(bool))
                {
                    if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    return bool.Parse(value);
                }
                return Convert.ChangeType(value, underlyingType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"第{rowIndex + 1}行，列[{columnName}]，值[{value}] 转换为类型[{targetType}]失败: {ex.Message}");
                return null;
            }
        }

        // 全量导入：TRUNCATE + SqlBulkCopy 直写
        private async Task PerformBulkUpsert(List<PersonalPMSModel.PMS项目明细报表> entities, HashSet<string> excelFields, ImportResult result)
        {
            try
            {
                result.TotalRows = entities.Count;
                _logger.LogInformation($"开始全量导入，共 {entities.Count} 条记录");

                var propertyMap = typeof(PersonalPMSModel.PMS项目明细报表)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

                var nonUpdatableFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "序号",
                    "项目编码"
                };

                var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                try
                {
                    if (connection is not SqlConnection sqlConnection)
                    {
                        throw new InvalidOperationException("当前数据库连接不是 SQL Server，无法使用批量优化导入。");
                    }

                    var dbColumns = await GetDbColumnsAsync(connection);

                    // 1. TRUNCATE 清空表（比 DELETE 快，不记日志）
                    using var truncateCmd = connection.CreateCommand();
                    truncateCmd.CommandTimeout = _sqlCommandTimeoutSeconds;
                    truncateCmd.CommandText = "TRUNCATE TABLE [dbo].[PMS项目明细报表]";
                    await truncateCmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("已清空旧数据 (TRUNCATE)");

                    // 2. 准备列映射
                    var insertColumns = excelFields
                        .Where(col => dbColumns.Contains(col))
                        .Where(col => !IgnoredFields.Contains(col))
                        .Where(col => !nonUpdatableFields.Contains(col))
                        .Where(col => propertyMap.ContainsKey(col))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (dbColumns.Contains("项目名称") && propertyMap.ContainsKey("项目名称") && !insertColumns.Contains("项目名称", StringComparer.OrdinalIgnoreCase))
                    {
                        insertColumns.Add("项目名称");
                    }

                    if (!insertColumns.Any())
                    {
                        _logger.LogWarning("未找到可插入字段。");
                        return;
                    }

                    // 3. 构建 DataTable（含项目编码 + 最后更新时间列）
                    var now = DateTime.Now;
                    var allColumns = new List<string> { "项目编码" };
                    foreach (var col in insertColumns)
                    {
                        if (!allColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                            allColumns.Add(col);
                    }
                    if (dbColumns.Contains("最后更新时间") && !allColumns.Contains("最后更新时间", StringComparer.OrdinalIgnoreCase))
                    {
                        allColumns.Add("最后更新时间");
                    }

                    var table = new DataTable();
                    foreach (var col in allColumns)
                    {
                        var prop = propertyMap.GetValueOrDefault(col);
                        var colType = prop != null
                            ? (Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType)
                            : typeof(string);
                        table.Columns.Add(col, colType);
                    }

                    foreach (var entity in entities)
                    {
                        var row = table.NewRow();
                        foreach (var col in allColumns)
                        {
                            if (col == "最后更新时间")
                            {
                                row[col] = (object)now ?? DBNull.Value;
                                continue;
                            }
                            var prop = propertyMap.GetValueOrDefault(col);
                            if (prop != null)
                            {
                                var val = prop.GetValue(entity);
                                row[col] = val ?? DBNull.Value;
                            }
                        }
                        table.Rows.Add(row);
                    }

                    // 4. SqlBulkCopy 直写正式表
                    using var bulkCopy = new SqlBulkCopy(sqlConnection, SqlBulkCopyOptions.TableLock, null)
                    {
                        DestinationTableName = "[dbo].[PMS项目明细报表]",
                        BulkCopyTimeout = 0,
                        BatchSize = 5000
                    };
                    foreach (DataColumn col in table.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                    }
                    await bulkCopy.WriteToServerAsync(table);

                    result.InsertedRows = entities.Count;
                    result.UpdatedRows = 0;
                    result.ModifiedRows = 0;
                    result.SkippedRows = 0;

                    _logger.LogInformation($"全量导入完成: 插入 {entities.Count} 条 (SqlBulkCopy)");
                }
                finally
                {
                    if (connection.State == System.Data.ConnectionState.Open)
                    {
                        await connection.CloseAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"全量导入失败: {ex.Message}");
                result.HasError = true;
                result.ErrorMessage = ex.Message;
            }
        }

        private static string BuildCompositeKey(string? projectCode, string? projectName)
        {
            return $"{projectCode?.Trim() ?? ""}||{projectName?.Trim() ?? ""}";
        }

        private async Task<HashSet<string>> GetDbColumnsAsync(DbConnection connection)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'PMS项目明细报表'";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
            return columns;
        }

        private async Task EnsureChangeLogTableAsync(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
IF OBJECT_ID(N'[dbo].[PMS项目明细报表导入变更日志]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PMS项目明细报表导入变更日志]
    (
        [日志ID] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [导入批次号] UNIQUEIDENTIFIER NOT NULL,
        [导入时间] DATETIME2(0) NOT NULL,
        [变更类型] NVARCHAR(20) NOT NULL,
        [项目编码] VARCHAR(20) NOT NULL,
        [项目名称] NVARCHAR(500) NULL,
        [变更字段] NVARCHAR(MAX) NULL,
        [旧值JSON] NVARCHAR(MAX) NULL,
        [新值JSON] NVARCHAR(MAX) NULL
    );

    CREATE INDEX [IX_PMS项目明细报表导入变更日志_导入批次号]
    ON [dbo].[PMS项目明细报表导入变更日志]([导入批次号]);

    CREATE INDEX [IX_PMS项目明细报表导入变更日志_项目编码]
    ON [dbo].[PMS项目明细报表导入变更日志]([项目编码]);
END";
            await command.ExecuteNonQueryAsync();

            using var upgradeCommand = connection.CreateCommand();
            upgradeCommand.CommandText = @"
IF COL_LENGTH(N'dbo.PMS项目明细报表导入变更日志', N'旧值JSON') IS NULL
    ALTER TABLE [dbo].[PMS项目明细报表导入变更日志] ADD [旧值JSON] NVARCHAR(MAX) NULL;

IF COL_LENGTH(N'dbo.PMS项目明细报表导入变更日志', N'新值JSON') IS NULL
    ALTER TABLE [dbo].[PMS项目明细报表导入变更日志] ADD [新值JSON] NVARCHAR(MAX) NULL;

EXEC(N'
CREATE OR ALTER VIEW [dbo].[vw_PMS项目明细报表导入变更日志]
AS
SELECT
    [日志ID],
    [导入批次号],
    [导入时间],
    [变更类型],
    [项目编码],
    [项目名称],
    [变更字段],
    [旧值JSON],
    [新值JSON]
FROM [dbo].[PMS项目明细报表导入变更日志];
');

EXEC(N'
CREATE OR ALTER PROCEDURE [dbo].[sp_查询PMS导入变更日志]
    @导入批次号 UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        [日志ID],
        [导入批次号],
        [导入时间],
        [变更类型],
        [项目编码],
        [项目名称],
        [变更字段],
        [旧值JSON],
        [新值JSON]
    FROM [dbo].[vw_PMS项目明细报表导入变更日志]
    WHERE (@导入批次号 IS NULL OR [导入批次号] = @导入批次号)
    ORDER BY [日志ID] DESC;
END
');

EXEC(N'
CREATE OR ALTER PROCEDURE [dbo].[sp_分页查询PMS导入变更日志]
    @导入批次号 UNIQUEIDENTIFIER = NULL,
    @项目编码 VARCHAR(20) = NULL,
    @变更类型 NVARCHAR(20) = NULL,
    @开始时间 DATETIME2(0) = NULL,
    @结束时间 DATETIME2(0) = NULL,
    @页码 INT = 1,
    @每页条数 INT = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF @页码 IS NULL OR @页码 < 1 SET @页码 = 1;
    IF @每页条数 IS NULL OR @每页条数 < 1 SET @每页条数 = 50;
    IF @每页条数 > 1000 SET @每页条数 = 1000;

    ;WITH Filtered AS
    (
        SELECT
            [日志ID],
            [导入批次号],
            [导入时间],
            [变更类型],
            [项目编码],
            [项目名称],
            [变更字段],
            [旧值JSON],
            [新值JSON]
        FROM [dbo].[vw_PMS项目明细报表导入变更日志]
        WHERE (@导入批次号 IS NULL OR [导入批次号] = @导入批次号)
          AND (@项目编码 IS NULL OR [项目编码] = @项目编码)
          AND (@变更类型 IS NULL OR [变更类型] = @变更类型)
          AND (@开始时间 IS NULL OR [导入时间] >= @开始时间)
          AND (@结束时间 IS NULL OR [导入时间] < DATEADD(SECOND, 1, @结束时间))
    )
    SELECT COUNT(1) AS [总记录数] FROM Filtered;

    ;WITH Filtered AS
    (
        SELECT
            [日志ID],
            [导入批次号],
            [导入时间],
            [变更类型],
            [项目编码],
            [项目名称],
            [变更字段],
            [旧值JSON],
            [新值JSON]
        FROM [dbo].[vw_PMS项目明细报表导入变更日志]
        WHERE (@导入批次号 IS NULL OR [导入批次号] = @导入批次号)
          AND (@项目编码 IS NULL OR [项目编码] = @项目编码)
          AND (@变更类型 IS NULL OR [变更类型] = @变更类型)
          AND (@开始时间 IS NULL OR [导入时间] >= @开始时间)
          AND (@结束时间 IS NULL OR [导入时间] < DATEADD(SECOND, 1, @结束时间))
    )
    SELECT
        [日志ID],
        [导入批次号],
        [导入时间],
        [变更类型],
        [项目编码],
        [项目名称],
        [变更字段],
        [旧值JSON],
        [新值JSON]
    FROM Filtered
    ORDER BY [日志ID] DESC
    OFFSET (@页码 - 1) * @每页条数 ROWS
    FETCH NEXT @每页条数 ROWS ONLY;
END
');";
            await upgradeCommand.ExecuteNonQueryAsync();
        }

        private async Task CreateStageTableAsync(DbConnection connection, List<string> stageColumns)
        {
            var columnsSql = string.Join(", ", stageColumns.Select(QuoteSqlName));
            using var command = connection.CreateCommand();
            command.CommandTimeout = _sqlCommandTimeoutSeconds;
            command.CommandText = $@"
IF OBJECT_ID('tempdb..#ImportStage') IS NOT NULL
    DROP TABLE #ImportStage;

SELECT TOP 0 {columnsSql}
INTO #ImportStage
FROM [dbo].[PMS项目明细报表];

ALTER TABLE #ImportStage ADD [导入行号] INT IDENTITY(1,1) NOT NULL;
CREATE CLUSTERED INDEX [IX_ImportStage_导入行号] ON #ImportStage([导入行号]);
CREATE NONCLUSTERED INDEX [IX_ImportStage_项目编码] ON #ImportStage([项目编码]);";
            await command.ExecuteNonQueryAsync();
        }

        private DataTable BuildStageDataTable(
            List<PersonalPMSModel.PMS项目明细报表> entities,
            List<string> stageColumns,
            Dictionary<string, PropertyInfo> propertyMap)
        {
            var table = new DataTable();
            foreach (var col in stageColumns)
            {
                if (propertyMap.TryGetValue(col, out var prop))
                {
                    var columnType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    table.Columns.Add(col, columnType);
                }
                else
                {
                    table.Columns.Add(col, typeof(string));
                }
            }

            foreach (var entity in entities)
            {
                var projectCode = entity.项目编码?.Trim();
                if (string.IsNullOrWhiteSpace(projectCode))
                {
                    continue;
                }

                var row = table.NewRow();
                foreach (var col in stageColumns)
                {
                    if (col.Equals("项目编码", StringComparison.OrdinalIgnoreCase))
                    {
                        row[col] = projectCode;
                    }
                    else if (propertyMap.TryGetValue(col, out var prop))
                    {
                        row[col] = prop.GetValue(entity) ?? DBNull.Value;
                    }
                    else
                    {
                        row[col] = DBNull.Value;
                    }
                }

                table.Rows.Add(row);
            }

            return table;
        }

        private async Task BulkCopyToStageAsync(SqlConnection connection, DataTable source, string destinationTableName)
        {
            using var bulkCopy = new SqlBulkCopy(connection)
            {
                DestinationTableName = destinationTableName,
                BulkCopyTimeout = 0
            };

            foreach (DataColumn col in source.Columns)
            {
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(source);
        }

        private async Task<int> ExecuteSetBasedUpdateAsync(
            DbConnection connection,
            List<string> updatableColumns,
            DateTime now,
            Guid batchId,
            int startRow,
            int endRow,
            List<string>? timeFieldsForCompare = null)
        {
            if (updatableColumns.Count == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            var setClauses = updatableColumns.Select(col => $"T.{QuoteSqlName(col)} = S.{QuoteSqlName(col)}").ToList();
            setClauses.Add("T.[最后更新时间] = @modifyTime");

            // 比较策略：如果指定了时间字段列表，只比较这些字段从空变有值
            // 否则比较所有可更新字段
            List<string> diffClauses;
            if (timeFieldsForCompare != null && timeFieldsForCompare.Count > 0)
            {
                // 时间字段从空变有值：(T.字段 IS NULL AND S.字段 IS NOT NULL) OR ...
                diffClauses = timeFieldsForCompare.Select(col =>
                    $"(T.{QuoteSqlName(col)} IS NULL AND S.{QuoteSqlName(col)} IS NOT NULL)").ToList();
            }
            else
            {
                diffClauses = updatableColumns.Select(col => BuildDiffPredicate("T", "S", col)).ToList();
            }
            var changedFieldsExpr = BuildChangedFieldsExpression(updatableColumns, "deleted", "inserted");
            var oldJsonExpr = BuildJsonExpression(updatableColumns, "deleted", includeOnlyChanged: true, "deleted", "inserted");
            var newJsonExpr = BuildJsonExpression(updatableColumns, "inserted", includeOnlyChanged: true, "deleted", "inserted");

            var modifyTimeParam = command.CreateParameter();
            modifyTimeParam.ParameterName = "@modifyTime";
            modifyTimeParam.Value = now;
            command.Parameters.Add(modifyTimeParam);

            var batchParam = command.CreateParameter();
            batchParam.ParameterName = "@batchId";
            batchParam.Value = batchId;
            command.Parameters.Add(batchParam);
            var startParam = command.CreateParameter();
            startParam.ParameterName = "@startRow";
            startParam.Value = startRow;
            command.Parameters.Add(startParam);
            var endParam = command.CreateParameter();
            endParam.ParameterName = "@endRow";
            endParam.Value = endRow;
            command.Parameters.Add(endParam);
            command.CommandTimeout = _sqlCommandTimeoutSeconds;

            command.CommandText = $@"
UPDATE T
SET {string.Join(", ", setClauses)}
OUTPUT
    @batchId,
    SYSDATETIME(),
    N'UPDATE',
    inserted.[项目编码],
    inserted.[项目名称],
    {changedFieldsExpr},
    {oldJsonExpr},
    {newJsonExpr}
INTO [dbo].[PMS项目明细报表导入变更日志]([导入批次号], [导入时间], [变更类型], [项目编码], [项目名称], [变更字段], [旧值JSON], [新值JSON])
FROM [dbo].[PMS项目明细报表] T
INNER JOIN #ImportStage S ON T.[项目编码] = S.[项目编码]
WHERE S.[导入行号] BETWEEN @startRow AND @endRow
  AND ({string.Join(" OR ", diffClauses)});

SELECT @@ROWCOUNT;";

            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private async Task<int> ExecuteSetBasedInsertAsync(
            DbConnection connection,
            List<string> stageColumns,
            HashSet<string> dbColumns,
            DateTime now,
            Guid batchId,
            int startRow,
            int endRow)
        {
            using var command = connection.CreateCommand();
            var insertColumns = new List<string>(stageColumns);
            var selectColumns = stageColumns.Select(col => $"S.{QuoteSqlName(col)}").ToList();

            if (dbColumns.Contains("最后更新时间"))
            {
                insertColumns.Add("最后更新时间");
                selectColumns.Add("@modifyTime");
                var modifyParam = command.CreateParameter();
                modifyParam.ParameterName = "@modifyTime";
                modifyParam.Value = now;
                command.Parameters.Add(modifyParam);
            }

            var batchParam = command.CreateParameter();
            batchParam.ParameterName = "@batchId";
            batchParam.Value = batchId;
            command.Parameters.Add(batchParam);
            var startParam = command.CreateParameter();
            startParam.ParameterName = "@startRow";
            startParam.Value = startRow;
            command.Parameters.Add(startParam);
            var endParam = command.CreateParameter();
            endParam.ParameterName = "@endRow";
            endParam.Value = endRow;
            command.Parameters.Add(endParam);
            command.CommandTimeout = _sqlCommandTimeoutSeconds;
            var newJsonExpr = BuildJsonExpression(stageColumns, "inserted", includeOnlyChanged: false, "inserted", "inserted");

            var rowsParam = command.CreateParameter();
            rowsParam.ParameterName = "@rows";
            rowsParam.DbType = System.Data.DbType.Int32;
            rowsParam.Direction = System.Data.ParameterDirection.Output;
            command.Parameters.Add(rowsParam);

            command.CommandText = $@"
INSERT INTO [dbo].[PMS项目明细报表] ({string.Join(", ", insertColumns.Select(QuoteSqlName))})
OUTPUT
    @batchId,
    SYSDATETIME(),
    N'INSERT',
    inserted.[项目编码],
    inserted.[项目名称],
    N'新增记录',
    NULL,
    {newJsonExpr}
INTO [dbo].[PMS项目明细报表导入变更日志]([导入批次号], [导入时间], [变更类型], [项目编码], [项目名称], [变更字段], [旧值JSON], [新值JSON])
SELECT {string.Join(", ", selectColumns)}
FROM #ImportStage S
WHERE S.[导入行号] BETWEEN @startRow AND @endRow;

SET @rows = @@ROWCOUNT;";

            await command.ExecuteNonQueryAsync();
            return Convert.ToInt32(rowsParam.Value);
        }

        private async Task<int> ExecuteScalarIntAsync(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = _sqlCommandTimeoutSeconds;
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static string QuoteSqlName(string name)
        {
            return $"[{name.Replace("]", "]]")}]";
        }

        private static string BuildDiffPredicate(string leftAlias, string rightAlias, string column)
        {
            var col = QuoteSqlName(column);
            return $"(({leftAlias}.{col} IS NULL AND {rightAlias}.{col} IS NOT NULL) OR ({leftAlias}.{col} IS NOT NULL AND {rightAlias}.{col} IS NULL) OR ({leftAlias}.{col} <> {rightAlias}.{col}))";
        }

        private static string BuildChangedFieldsExpression(List<string> columns, string oldAlias, string newAlias)
        {
            var parts = columns.Select(col =>
            {
                var quoted = QuoteSqlName(col);
                var diff = $"(({oldAlias}.{quoted} IS NULL AND {newAlias}.{quoted} IS NOT NULL) OR ({oldAlias}.{quoted} IS NOT NULL AND {newAlias}.{quoted} IS NULL) OR ({oldAlias}.{quoted} <> {newAlias}.{quoted}))";
                return $"CASE WHEN {diff} THEN N',{col}' ELSE N'' END";
            });

            return $"STUFF(({string.Join(" + ", parts)}), 1, 1, N'')";
        }

        private static string BuildJsonExpression(
            List<string> columns,
            string valueAlias,
            bool includeOnlyChanged,
            string oldAlias,
            string newAlias)
        {
            var parts = columns.Select(col =>
            {
                var quoted = QuoteSqlName(col);
                var valueExpr = $"CASE WHEN {valueAlias}.{quoted} IS NULL THEN N'null' ELSE N'\"' + STRING_ESCAPE(CONVERT(NVARCHAR(MAX), {valueAlias}.{quoted}), 'json') + N'\"' END";
                if (!includeOnlyChanged)
                {
                    return $"N',\"{col}\":' + {valueExpr}";
                }

                var diff = BuildDiffPredicate(oldAlias, newAlias, col);
                return $"CASE WHEN {diff} THEN N',\"{col}\":' + {valueExpr} ELSE N'' END";
            });

            return $"N'{{' + COALESCE(STUFF(({string.Join(" + ", parts)}), 1, 1, N''), N'') + N'}}'";
        }
    }

    public class ImportResult
    {
        public int TotalRows { get; set; }
        public int InsertedRows { get; set; }
        public int UpdatedRows { get; set; }
        public int ModifiedRows { get; set; }
        public int SkippedRows { get; set; }
        public bool HasError { get; set; }
        public string ErrorMessage { get; set; } = "";
    }
} 