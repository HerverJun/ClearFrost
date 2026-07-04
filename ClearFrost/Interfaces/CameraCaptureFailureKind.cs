namespace ClearFrost.Interfaces
{
    public enum CameraCaptureFailureKind
    {
        None,
        NotReady,
        TriggerFailed,
        GetFrameFailed,
        EmptyFrame,
        InvalidFrame,
        ShortFrame,
        UnsupportedPixelFormat,
        ConversionFailed
    }

    public interface ICameraCaptureDiagnostics
    {
        CameraCaptureFailureKind LastCaptureFailureKind { get; }
    }
}
