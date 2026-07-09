internal sealed class WebcamCaptureSource(int cameraIndex) : IImageCaptureSource, IDisposable
{
    private readonly object syncRoot = new();
    private VideoCapture? capture;
    private bool prepared;

    public string Name => $"Webcam {cameraIndex}";

    public Task PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (syncRoot)
            {
                if (prepared && capture?.IsOpened() == true)
                {
                    return;
                }

                EnsureCaptureOpen();
                using var frame = ReadFrame(timeout, cancellationToken);
                prepared = !frame.Empty();
            }
        }, cancellationToken);
    }

    public Task<byte[]> CaptureJpegAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (syncRoot)
            {
                EnsureCaptureOpen();
                using var frame = ReadFrame(timeout, cancellationToken);
                Cv2.ImEncode(".jpg", frame, out var jpegBytes);
                prepared = true;
                return jpegBytes;
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            capture?.Release();
            capture?.Dispose();
            capture = null;
            prepared = false;
        }
    }

    private void EnsureCaptureOpen()
    {
        if (capture?.IsOpened() == true)
        {
            return;
        }

        capture?.Dispose();
        capture = new VideoCapture(cameraIndex);
        prepared = false;

        if (!capture.IsOpened())
        {
            capture.Dispose();
            capture = null;
            throw new InvalidOperationException($"Unable to open webcam index {cameraIndex}.");
        }
    }

    private Mat ReadFrame(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (capture is null)
        {
            throw new InvalidOperationException("Webcam capture has not been opened.");
        }

        var frame = new Mat();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.Read(frame) && !frame.Empty())
            {
                return frame;
            }

            Thread.Sleep(20);
        }

        frame.Dispose();
        prepared = false;
        throw new TimeoutException("No webcam frame was received before the timeout.");
    }
}

internal sealed class VitureBeastCaptureSource(AppSettings settings) : IImageCaptureSource
{
    public string Name => "Viture Beast";

    public Task PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task<byte[]> CaptureJpegAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        NativeLibraryLoader.AddDllDirectory(Path.Combine(settings.VitureSdkRoot, "x86_64"));

        var glassesPid = VitureUsbDeviceFinder.FindFirstVitureGlassesPid()
            ?? throw new InvalidOperationException("No connected Viture glasses were found.");

        var cameraVid = VitureNative.xr_camera_provider_get_camera_vid(glassesPid);
        var cameraPid = VitureNative.xr_camera_provider_get_camera_pid(glassesPid);
        if (cameraVid == 0 || cameraPid == 0)
        {
            throw new InvalidOperationException($"The detected Viture device PID 0x{glassesPid:X4} does not expose a supported camera.");
        }

        var handle = VitureNative.xr_camera_provider_create(cameraVid, cameraPid);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"xr_camera_provider_create failed for camera VID 0x{cameraVid:X4}, PID 0x{cameraPid:X4}.");
        }

        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        VitureNative.XRCameraFrameCallback callback = (framePointer, _) =>
        {
            if (framePointer == IntPtr.Zero || completion.Task.IsCompleted)
            {
                return;
            }

            var frame = Marshal.PtrToStructure<VitureNative.XRCameraFrame>(framePointer);
            if (frame.Format != VitureNative.XR_CAMERA_FORMAT_MJPEG || frame.Data == IntPtr.Zero || frame.Size == 0)
            {
                return;
            }

            var bytes = new byte[frame.Size];
            Marshal.Copy(frame.Data, bytes, 0, bytes.Length);
            completion.TrySetResult(bytes);
        };

        try
        {
            var startResult = VitureNative.xr_camera_provider_start(handle, callback, IntPtr.Zero);
            if (startResult != VitureNative.VITURE_GLASSES_SUCCESS)
            {
                throw new InvalidOperationException($"xr_camera_provider_start failed with code {startResult}.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await using var _ = timeoutCts.Token.Register(
                static state => ((TaskCompletionSource<byte[]>)state!).TrySetException(
                    new TimeoutException("No Viture camera frame was received before the timeout.")),
                completion);

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            VitureNative.xr_camera_provider_stop(handle);
            VitureNative.xr_camera_provider_destroy(handle);
            GC.KeepAlive(callback);
        }
    }
}
