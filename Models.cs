internal sealed record CapturedImage(IImageCaptureSource Source, byte[] JpegBytes);

internal sealed record CapturedVideo(IImageCaptureSource Source, string FilePath, TimeSpan Duration);

internal sealed record SceneContext(
    string SourceName,
    string Description,
    byte[] JpegBytes,
    DateTimeOffset CapturedAt);

internal interface IImageCaptureSource
{
    string Name { get; }
    Task PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<byte[]> CaptureJpegAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IVideoCaptureSource : IImageCaptureSource
{
    Task<string> CaptureVideoAsync(
        TimeSpan duration,
        TimeSpan firstFrameTimeout,
        string outputPath,
        CancellationToken cancellationToken);
}

internal interface ILiveFrameSource : IVideoCaptureSource
{
    IAsyncEnumerable<byte[]> StreamJpegFramesAsync(
        TimeSpan firstFrameTimeout,
        double maxFramesPerSecond,
        CancellationToken cancellationToken);
}
