using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ClearFrost.Services
{
    /// <summary>
    /// 数据集自动收集服务
    /// 从历史检测记录中按时间平摊、机型去重的策略抽取样本图片。
    /// </summary>
    public class DatasetCollectionService
    {
        private readonly string _dbPath;
        private readonly string _storagePath;
        private const int BusyTimeoutMs = 5000;

        public DatasetCollectionService(string dbPath, string storagePath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _storagePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        }

        /// <summary>
        /// 自动收集数据集
        /// </summary>
        /// <param name="maxDays">最大回溯天数</param>
        /// <param name="totalCount">目标总张数</param>
        /// <param name="failRatio">不合格图片占比</param>
        /// <param name="progress">进度报告</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<DatasetCollectionResult> CollectAsync(
            int maxDays = 15,
            int totalCount = 100,
            double failRatio = 0.7,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_dbPath))
            {
                return DatasetCollectionResult.Failed("检测记录数据库不存在。");
            }

            int failTarget = (int)Math.Round(totalCount * failRatio);
            int passTarget = totalCount - failTarget;

            progress?.Report("正在读取检测记录...");

            var allRecords = await QueryRecordsAsync(maxDays, cancellationToken);
            List<DetectionRecordLite> validRecords;

            if (allRecords.Count == 0)
            {
                progress?.Report("指定时间范围内未找到检测记录，尝试直接扫描图片目录...");
                validRecords = ScanImageFilesFromDisk(maxDays, cancellationToken);

                if (validRecords.Count == 0)
                {
                    return DatasetCollectionResult.Failed("指定时间范围内未找到任何检测记录或图片文件。");
                }
            }
            else
            {
                // 过滤出磁盘上实际存在的原图，避免渲染框污染训练数据。
                validRecords = allRecords
                    .Select(r => ResolveImagePath(r))
                    .Where(r => !string.IsNullOrWhiteSpace(r.EffectiveImagePath) && File.Exists(r.EffectiveImagePath))
                    .ToList();

                // 若有部分记录路径失效，尝试根据时间戳在标准目录中进行增量匹配并补充
                if (validRecords.Count < allRecords.Count)
                {
                    progress?.Report("检测到部分图片路径失效，尝试根据时间戳在标准目录中进行增量匹配...");
                    var invalidRecords = allRecords
                        .Where(r => string.IsNullOrWhiteSpace(r.EffectiveImagePath) || !File.Exists(r.EffectiveImagePath))
                        .ToList();
                    var resolvedIncremental = TryResolvePathsFromStandardDirectories(invalidRecords);
                    validRecords = validRecords
                        .Concat(resolvedIncremental)
                        .GroupBy(r => r.Id)
                        .Select(g => g.First())
                        .ToList();
                }

                // 若仍无任何有效记录，回退到直接扫描图片文件夹
                if (validRecords.Count == 0)
                {
                    progress?.Report("检测记录中无有效图片路径，尝试直接扫描图片目录...");
                    validRecords = ScanImageFilesFromDisk(maxDays, cancellationToken);
                }
            }

            if (validRecords.Count == 0)
            {
                return DatasetCollectionResult.Failed("检测记录中的图片文件在磁盘上已不存在，可能已被清理。");
            }

            var failCandidates = validRecords.Where(r => !r.IsQualified).ToList();
            var passCandidates = validRecords.Where(r => r.IsQualified).ToList();

            progress?.Report($"有效记录: NG {failCandidates.Count} 张, OK {passCandidates.Count} 张");

            // 动态调整目标（若实际不足则按同比例缩减；若某一类完全缺失，由另一类填补）
            if (failCandidates.Count < failTarget || passCandidates.Count < passTarget)
            {
                int adjustedTotal = Math.Min(totalCount, failCandidates.Count + passCandidates.Count);

                if (failCandidates.Count == 0)
                {
                    failTarget = 0;
                    passTarget = Math.Min(passCandidates.Count, adjustedTotal);
                }
                else if (passCandidates.Count == 0)
                {
                    passTarget = 0;
                    failTarget = Math.Min(failCandidates.Count, adjustedTotal);
                }
                else
                {
                    double availableFail = failCandidates.Count;
                    double availablePass = passCandidates.Count;
                    double maxByFail = availableFail / failRatio;
                    double maxByPass = availablePass / (1 - failRatio);
                    adjustedTotal = (int)Math.Floor(Math.Min(maxByFail, maxByPass));
                    adjustedTotal = Math.Min(adjustedTotal, failCandidates.Count + passCandidates.Count);
                    adjustedTotal = Math.Max(0, adjustedTotal);

                    failTarget = (int)Math.Round(adjustedTotal * failRatio);
                    passTarget = adjustedTotal - failTarget;

                    // 保证不超限
                    failTarget = Math.Min(failTarget, failCandidates.Count);
                    passTarget = Math.Min(passTarget, passCandidates.Count);
                }

                progress?.Report($"图片数量不足，自动调整目标为 NG {failTarget} + OK {passTarget} = {failTarget + passTarget} 张");
            }

            var selectedFails = StratifiedSample(failCandidates, failTarget);
            var selectedPasses = StratifiedSample(passCandidates, passTarget);

            var selected = selectedFails.Concat(selectedPasses).ToList();
            if (selected.Count == 0)
            {
                return DatasetCollectionResult.Failed("没有可复制的有效图片。");
            }

            string outputDir = Path.Combine(
                _storagePath,
                "DatasetCollections",
                $"Dataset_{DateTime.Now:yyyyMMdd_HHmmss}");

            string failDir = Path.Combine(outputDir, "Fail");
            string passDir = Path.Combine(outputDir, "Pass");
            Directory.CreateDirectory(failDir);
            Directory.CreateDirectory(passDir);

            progress?.Report($"开始复制 {selected.Count} 张图片到 {outputDir}...");

            int failCopied = 0;
            int passCopied = 0;
            var copyErrors = new List<string>();

            await Task.Run(() =>
            {
                foreach (var record in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string destDir = record.IsQualified ? passDir : failDir;
                        string fileName = Path.GetFileName(record.EffectiveImagePath!);
                        string destPath = Path.Combine(destDir, fileName);

                        // 处理重名
                        if (File.Exists(destPath))
                        {
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                            string ext = Path.GetExtension(fileName);
                            int suffix = 1;
                            do
                            {
                                destPath = Path.Combine(destDir, $"{nameWithoutExt}_{suffix}{ext}");
                                suffix++;
                            } while (File.Exists(destPath));
                        }

                        File.Copy(record.EffectiveImagePath!, destPath, overwrite: false);

                        if (record.IsQualified)
                            passCopied++;
                        else
                            failCopied++;
                    }
                    catch (Exception ex)
                    {
                        copyErrors.Add($"复制失败 {record.EffectiveImagePath}: {ex.Message}");
                    }
                }
            }, cancellationToken);

            int totalCopied = failCopied + passCopied;
            if (totalCopied == 0 && selected.Count > 0)
            {
                string errDetail = copyErrors.Count > 0 ? string.Join("; ", copyErrors.Take(3)) : "无可用文件";
                try
                {
                    if (Directory.Exists(outputDir))
                    {
                        Directory.Delete(outputDir, true);
                    }
                }
                catch (Exception ex)
                {
                    copyErrors.Add($"清理空数据集目录失败 {outputDir}: {ex.Message}");
                }
                return DatasetCollectionResult.Failed($"数据集图片复制失败: 未成功复制任何图片。详情: {errDetail}");
            }

            string message = $"成功复制 {totalCopied} 张图片（NG {failCopied} / OK {passCopied}）";
            if (copyErrors.Count > 0)
            {
                message += $"，{copyErrors.Count} 张复制失败。";
            }
            if (totalCopied < totalCount)
            {
                message += $" 因可用图片不足，目标从 {totalCount} 张调整为实际 {totalCopied} 张。";
            }

            return new DatasetCollectionResult
            {
                Success = true,
                OutputDirectory = outputDir,
                FailCopied = failCopied,
                PassCopied = passCopied,
                TotalRequested = totalCount,
                Message = message
            };
        }

        /// <summary>
        /// 分层抽样：先按天分桶，再按小时分桶，再按机型去重，尽量均匀分散。
        /// </summary>
        private static List<DetectionRecordLite> StratifiedSample(List<DetectionRecordLite> candidates, int targetCount)
        {
            if (candidates.Count <= targetCount)
            {
                // 候选不足，全部返回（但按时间打乱避免顺序依赖）
                return Shuffle(candidates).ToList();
            }

            var selected = new HashSet<long>(); // 用 Id 去重
            var result = new List<DetectionRecordLite>();

            // 按天分桶
            var dayGroups = candidates
                .GroupBy(r => r.Timestamp.Date)
                .OrderBy(g => g.Key)
                .ToList();

            int actualDays = dayGroups.Count;
            int perDay = actualDays > 0 ? (int)Math.Ceiling((double)targetCount / actualDays) : targetCount;

            // 第一轮：每天按小时分层、机型去重抽取
            foreach (var dayGroup in dayGroups)
            {
                if (result.Count >= targetCount) break;

                int dayQuota = Math.Min(perDay, targetCount - result.Count);

                // 按小时分桶
                var hourGroups = dayGroup
                    .Where(r => !selected.Contains(r.Id))
                    .GroupBy(r => r.Timestamp.Hour)
                    .OrderBy(_ => Guid.NewGuid()) // 随机小时顺序
                    .ToList();

                // 每个小时内按机型分组，每组最多取1张
                foreach (var hourGroup in hourGroups)
                {
                    if (result.Count >= targetCount || dayQuota <= 0) break;

                    var modelGroups = hourGroup
                        .GroupBy(r => $"{r.ModelName}|{r.RecipeId}")
                        .OrderBy(_ => Guid.NewGuid())
                        .ToList();

                    foreach (var modelGroup in modelGroups)
                    {
                        if (result.Count >= targetCount || dayQuota <= 0) break;

                        var pick = modelGroup.FirstOrDefault(r => !selected.Contains(r.Id));
                        if (pick != null)
                        {
                            selected.Add(pick.Id);
                            result.Add(pick);
                            dayQuota--;
                        }
                    }
                }
            }

            // 第二轮：如果第一轮未达到目标，放宽机型限制，继续从剩余候选中按天平均补充
            if (result.Count < targetCount)
            {
                var remaining = candidates
                    .Where(r => !selected.Contains(r.Id))
                    .OrderBy(_ => Guid.NewGuid())
                    .ToList();

                int need = targetCount - result.Count;
                var extra = remaining.Take(need).ToList();
                foreach (var r in extra)
                {
                    selected.Add(r.Id);
                    result.Add(r);
                }
            }

            return result;
        }

        /// <summary>
        /// 查询最近 N 天的检测记录（仅取需要的字段，无数量限制）
        /// </summary>
        private async Task<List<DetectionRecordLite>> QueryRecordsAsync(int maxDays, CancellationToken cancellationToken)
        {
            var records = new List<DetectionRecordLite>();
            DateTime startDate = DateTime.Now.Date.AddDays(-(maxDays - 1));
            DateTime endDate = DateTime.Now.Date.AddDays(1).AddTicks(-1);

            string connectionString = $"Data Source={_dbPath};Cache=Shared;Pooling=True;Default Timeout={BusyTimeoutMs / 1000}";

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // 启用 WAL 模式相关配置与主服务保持一致
            await using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout = 5000;";
                await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string sql = @"
                SELECT Id, Timestamp, IsQualified, ImagePath, RenderedImagePath,
                       ModelName, RecipeId, InspectionId
                FROM DetectionRecords
                WHERE Timestamp >= @StartDate AND Timestamp <= @EndDate
                ORDER BY Timestamp DESC, Id DESC
            ";

            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@StartDate", startDate.ToString("yyyy-MM-dd 00:00:00.000"));
            command.Parameters.AddWithValue("@EndDate", endDate.ToString("yyyy-MM-dd 23:59:59.999"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new DetectionRecordLite
                {
                    Id = reader.GetInt64(0),
                    Timestamp = reader.GetDateTime(1),
                    IsQualified = reader.GetInt32(2) != 0,
                    ImagePath = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RenderedImagePath = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    ModelName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    RecipeId = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    InspectionId = reader.IsDBNull(7) ? "" : reader.GetString(7),
                });
            }

            return records;
        }

        /// <summary>
        /// 决定使用哪条图片路径：仅使用原图，避免渲染框污染训练数据。
        /// </summary>
        private static DetectionRecordLite ResolveImagePath(DetectionRecordLite record)
        {
            if (!string.IsNullOrWhiteSpace(record.ImagePath) &&
                File.Exists(record.ImagePath))
            {
                record.EffectiveImagePath = record.ImagePath;
                return record;
            }

            record.EffectiveImagePath = record.ImagePath;
            return record;
        }

        /// <summary>
        /// 当数据库中 ImagePath 为空时，尝试根据 Timestamp 在标准目录中查找匹配的图片。
        /// </summary>
        private List<DetectionRecordLite> TryResolvePathsFromStandardDirectories(List<DetectionRecordLite> records)
        {
            var resolved = new List<DetectionRecordLite>();
            string imageBase = Path.Combine(_storagePath, "Images");

            foreach (var record in records)
            {
                if (!string.IsNullOrWhiteSpace(record.EffectiveImagePath) && File.Exists(record.EffectiveImagePath))
                {
                    resolved.Add(record);
                    continue;
                }

                string subFolder = record.IsQualified ? "Qualified" : "Unqualified";
                string dateFolder = record.Timestamp.ToString("yyyy年MM月dd日");
                string hourFolder = record.Timestamp.ToString("HH");
                string searchDir = Path.Combine(imageBase, subFolder, dateFolder, hourFolder);

                if (!Directory.Exists(searchDir))
                    continue;

                string[] files = EnumerateImageFiles(searchDir).ToArray();
                string? matched = null;

                // 优先按 InspectionId 匹配
                if (!string.IsNullOrWhiteSpace(record.InspectionId))
                {
                    matched = files.FirstOrDefault(f => Path.GetFileName(f).Contains(record.InspectionId));
                }

                // 若未匹配到，按时间戳前后 5 分钟内最近的文件匹配
                if (matched == null)
                {
                    DateTime recordTime = record.Timestamp;
                    matched = files
                        .Select(f => new { Path = f, Time = ExtractTimeFromFileName(f) })
                        .Select(x => new
                        {
                            x.Path,
                            Timestamp = x.Time.HasValue
                                ? recordTime.Date.Add(x.Time.Value.TimeOfDay)
                                : (DateTime?)null
                        })
                        .Where(x => x.Timestamp.HasValue && Math.Abs((x.Timestamp.Value - recordTime).TotalMinutes) <= 5)
                        .OrderBy(x => Math.Abs((x.Timestamp!.Value - recordTime).TotalSeconds))
                        .Select(x => x.Path)
                        .FirstOrDefault();
                }

                if (matched != null && File.Exists(matched))
                {
                    record.EffectiveImagePath = matched;
                    resolved.Add(record);
                }
            }

            return resolved;
        }

        /// <summary>
        /// 从文件名中提取时间（支持 PASS_HHmmssfff.jpg 或 FAIL_HHmmssfff.jpg）
        /// </summary>
        private static DateTime? ExtractTimeFromFileName(string filePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            // 格式如 PASS_143022123 或 FAIL_SN-xxx_143022123
            int lastUnderscore = fileName.LastIndexOf('_');
            if (lastUnderscore < 0) return null;
            string timePart = fileName.Substring(lastUnderscore + 1);
            if (timePart.Length >= 6 &&
                int.TryParse(timePart.Substring(0, 2), out int hour) &&
                int.TryParse(timePart.Substring(2, 2), out int minute) &&
                int.TryParse(timePart.Substring(4, 2), out int second))
            {
                // 日期从目录结构推断，这里只返回时间部分，调用方会结合目录日期
                return new DateTime(1, 1, 1, hour, minute, second);
            }
            return null;
        }

        /// <summary>
        /// 当数据库方式完全失效时，直接扫描图片目录收集最近 N 天的图片。
        /// </summary>
        private List<DetectionRecordLite> ScanImageFilesFromDisk(int maxDays, CancellationToken cancellationToken)
        {
            var records = new List<DetectionRecordLite>();
            string imageBase = Path.Combine(_storagePath, "Images");
            DateTime cutoff = DateTime.Now.Date.AddDays(-(maxDays - 1));
            long nextId = 1;

            foreach (bool isQualified in new[] { true, false })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string subFolder = isQualified ? "Qualified" : "Unqualified";
                string subPath = Path.Combine(imageBase, subFolder);
                if (!Directory.Exists(subPath))
                    continue;

                foreach (string dateDir in Directory.GetDirectories(subPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string dateName = Path.GetFileName(dateDir) ?? "";
                    if (!DateTime.TryParseExact(dateName, "yyyy年MM月dd日", null, System.Globalization.DateTimeStyles.None, out DateTime dirDate))
                        continue;
                    if (dirDate < cutoff)
                        continue;

                    foreach (string hourDir in Directory.GetDirectories(dateDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string hourName = Path.GetFileName(hourDir) ?? "";
                        if (!int.TryParse(hourName, out int hour))
                            continue;

                        foreach (string file in EnumerateImageFiles(hourDir))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var fileTime = ExtractTimeFromFileName(file);
                            DateTime timestamp = dirDate.AddHours(hour);
                            if (fileTime.HasValue)
                            {
                                timestamp = timestamp.AddHours(fileTime.Value.Hour - timestamp.Hour)
                                                     .AddMinutes(fileTime.Value.Minute)
                                                     .AddSeconds(fileTime.Value.Second);
                            }

                            records.Add(new DetectionRecordLite
                            {
                                Id = nextId++,
                                Timestamp = timestamp,
                                IsQualified = isQualified,
                                EffectiveImagePath = file,
                                ModelName = "",
                                RecipeId = "",
                                InspectionId = Path.GetFileNameWithoutExtension(file) ?? ""
                            });
                        }
                    }
                }
            }

            return records;
        }

        private static IEnumerable<T> Shuffle<T>(IEnumerable<T> source)
        {
            var rng = new Random();
            return source.OrderBy(_ => rng.Next());
        }

        private static IEnumerable<string> EnumerateImageFiles(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return Enumerable.Empty<string>();
            }

            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp" };
            return Directory
                .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 轻量检测记录（仅数据集收集所需字段）
    /// </summary>
    public class DetectionRecordLite
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsQualified { get; set; }
        public string ImagePath { get; set; } = "";
        public string RenderedImagePath { get; set; } = "";
        public string EffectiveImagePath { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string RecipeId { get; set; } = "";
        public string InspectionId { get; set; } = "";
    }

    /// <summary>
    /// 数据集收集结果
    /// </summary>
    public class DatasetCollectionResult
    {
        public bool Success { get; set; }
        public string OutputDirectory { get; set; } = "";
        public int FailCopied { get; set; }
        public int PassCopied { get; set; }
        public int TotalRequested { get; set; }
        public string Message { get; set; } = "";

        public static DatasetCollectionResult Failed(string message)
        {
            return new DatasetCollectionResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
