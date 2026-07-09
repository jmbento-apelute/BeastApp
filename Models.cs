internal sealed record CapturedImage(IImageCaptureSource Source, byte[] JpegBytes);

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
