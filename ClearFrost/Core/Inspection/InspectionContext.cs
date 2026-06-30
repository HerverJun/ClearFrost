// ============================================================================
// 文件名: InspectionContext.cs
// 描述:   单次检测追溯上下文
// ============================================================================

using System;

namespace ClearFrost.Core.Inspection
{
    /// <summary>
    /// 贯穿单次检测的追溯上下文。
    /// </summary>
    public sealed class InspectionContext
    {
        public string InspectionId { get; init; } = string.Empty;
        public DateTimeOffset TriggerTime { get; init; } = DateTimeOffset.Now;
        public string TriggerSource { get; init; } = string.Empty;
        public int? TriggerSeq { get; init; }
        public bool PlcTriggerAccepted { get; set; }
        public int? ResultSeq { get; set; }
        public InspectionStage CurrentStage { get; set; } = InspectionStage.Unknown;
        public TraceStatus TraceStatus { get; set; } = TraceStatus.Unknown;
        public long CaptureMs { get; set; }
        public long InferenceMs { get; set; }
        public long RoiMs { get; set; }
        public long PlcWriteMs { get; set; }
        public long RenderToUiMs { get; set; }
        public long SaveImageMs { get; set; }
        public long SaveRecordMs { get; set; }
        public long TotalMs { get; set; }
        public int FallbackAttemptCount { get; set; }
        public string FallbackSkippedReason { get; set; } = string.Empty;
        public long ImageQueuePending { get; set; }
        public long RecordQueuePending { get; set; }
        public long HandshakeStartMs { get; set; }
        public long PlcResultWriteMs { get; set; }
        public long HandshakeCompleteMs { get; set; }
        public string? ProductBarcode { get; set; }
        public bool? BarcodeReadSucceeded { get; set; }
        public string? BarcodeError { get; set; }
        public string? ImagePath { get; set; }
        public string? RenderedImagePath { get; set; }
        public string? ErrorStage { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }

        public void SetError(InspectionStage stage, string code, string message)
        {
            ErrorStage = stage.ToString();
            ErrorCode = code;
            ErrorMessage = message;
        }

        public void MarkFailed(InspectionStage stage, string code, string message)
        {
            CurrentStage = InspectionStage.Failed;
            SetError(stage, code, message);
        }
    }
}
