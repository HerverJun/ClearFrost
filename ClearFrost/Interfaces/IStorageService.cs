// ============================================================================
// 文件名: IStorageService.cs
// 描述:   存储服务接口
//
// 功能:
//   - 图像保存和管理
//   - 日志文件记录
//   - 历史数据自动清理
// ============================================================================

using System;
using System.Drawing;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// 存储服务接口
    /// </summary>
    public interface IStorageService : IDisposable
    {
        #region 属性

        /// <summary>
        /// 图像存储根路径
        /// </summary>
        string ImageBasePath { get; }

        /// <summary>
        /// 日志存储根路径
        /// </summary>
        string LogBasePath { get; }

        /// <summary>
        /// 系统数据路径
        /// </summary>
        string SystemPath { get; }

        /// <summary>
        /// 已解析的存储根路径（进行驱动器/盘符校验后的实际路径）
        /// </summary>
        string BaseStoragePath { get; }

        #endregion

        #region 图像保存

        /// <summary>
        /// 保存检测图像
        /// </summary>
        /// <param name="bitmap">图像</param>
        /// <param name="isQualified">是否合格</param>
        void SaveDetectionImage(Bitmap bitmap, bool isQualified);

        /// <summary>
        /// 保存检测图像 (异步)
        /// </summary>
        void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified);

        #endregion

        #region 日志记录

        /// <summary>
        /// 写入检测日志
        /// </summary>
        void WriteDetectionLog(string content, bool isQualified);

        /// <summary>
        /// 写入启动日志
        /// </summary>
        void WriteStartupLog(string action, string? serialNumber = null);

        /// <summary>
        /// 写入错误日志
        /// </summary>
        void WriteErrorLog(string message);

        #endregion

        #region 数据清理

        /// <summary>
        /// 清理旧数据
        /// </summary>
        /// <param name="retainDays">保留天数</param>
        void CleanOldData(int retainDays);

        /// <summary>
        /// 获取存储磁盘剩余空间（GB）
        /// </summary>
        double GetDiskFreeSpaceGb();

        /// <summary>
        /// 执行紧急清理，按优先级删除最旧的数据直到磁盘空间恢复或无可删数据
        /// </summary>
        /// <returns>清理后的剩余空间（GB）</returns>
        double PerformEmergencyCleanup();

        /// <summary>
        /// 确保目录存在
        /// </summary>
        void EnsureDirectoriesExist();

        /// <summary>
        /// 刷新运行时存储根目录。
        /// </summary>
        void UpdateStoragePath(string storagePath);

        #endregion
    }
}
