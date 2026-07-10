internal sealed class WebcamCaptureSource(int cameraIndex) : ILiveFrameSource, IDisposable
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

    public Task<string> CaptureVideoAsync(
        TimeSpan duration,
        TimeSpan firstFrameTimeout,
        string outputPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (syncRoot)
            {
                EnsureCaptureOpen();
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

                var fps = capture!.Get(VideoCaptureProperties.Fps);
                if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
                {
                    fps = 30;
                }

                using var firstFrame = ReadFrame(firstFrameTimeout, cancellationToken);
                var frameSize = new Size(firstFrame.Width, firstFrame.Height);
                using var writer = VideoFileWriterFactory.Create(outputPath, fps, frameSize);
                writer.Write(firstFrame);

                using var frame = new Mat();
                var deadline = DateTimeOffset.UtcNow + duration;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (capture.Read(frame) && !frame.Empty())
                    {
                        writer.Write(frame);
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                prepared = true;
                return outputPath;
            }
        }, cancellationToken);
    }

    public async IAsyncEnumerable<byte[]> StreamJpegFramesAsync(
        TimeSpan firstFrameTimeout,
        double maxFramesPerSecond,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frameDelay = LiveFrameTiming.GetFrameDelay(maxFramesPerSecond);
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] jpegBytes;
            lock (syncRoot)
            {
                EnsureCaptureOpen();
                using var frame = ReadFrame(firstFrameTimeout, cancellationToken);
                Cv2.ImEncode(".jpg", frame, out jpegBytes);
                prepared = true;
            }

            yield return jpegBytes;

            if (frameDelay > TimeSpan.Zero)
            {
                await Task.Delay(frameDelay, cancellationToken).ConfigureAwait(false);
            }
        }
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

internal sealed class VitureBeastCaptureSource(AppSettings settings) : ILiveFrameSource
{
    public string Name => "Viture Beast";

    public Task PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task<byte[]> CaptureJpegAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        NativeLibraryLoader.AddDllDirectory(Path.Combine(settings.VitureSdkRoot, "x86_64"));

        var handle = CreateCameraProvider();

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

    public async Task<string> CaptureVideoAsync(
        TimeSpan duration,
        TimeSpan firstFrameTimeout,
        string outputPath,
        CancellationToken cancellationToken)
    {
        NativeLibraryLoader.AddDllDirectory(Path.Combine(settings.VitureSdkRoot, "x86_64"));

        var handle = CreateCameraProvider();
        using var frameQueue = new System.Collections.Concurrent.BlockingCollection<byte[]>(boundedCapacity: 3);
        var acceptingFrames = 1;
        VitureNative.XRCameraFrameCallback callback = (framePointer, _) =>
        {
            if (Volatile.Read(ref acceptingFrames) == 0 || framePointer == IntPtr.Zero)
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
            try
            {
                frameQueue.TryAdd(bytes);
            }
            catch (InvalidOperationException)
            {
                // The recording has already finished.
            }
        };

        try
        {
            var startResult = VitureNative.xr_camera_provider_start(handle, callback, IntPtr.Zero);
            if (startResult != VitureNative.VITURE_GLASSES_SUCCESS)
            {
                throw new InvalidOperationException($"xr_camera_provider_start failed with code {startResult}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using var firstFrame = await ReadFirstVitureVideoFrameAsync(
                frameQueue,
                firstFrameTimeout,
                cancellationToken).ConfigureAwait(false);

            const double outputFps = 30;
            var frameSize = new Size(firstFrame.Width, firstFrame.Height);
            using var writer = VideoFileWriterFactory.Create(outputPath, outputFps, frameSize);
            var recordingClock = System.Diagnostics.Stopwatch.StartNew();
            var writtenFrameCount = 0;
            Mat? lastFrame = firstFrame.Clone();
            WriteFrameForElapsed(writer, firstFrame, TimeSpan.Zero, outputFps, ref writtenFrameCount);

            var deadline = DateTimeOffset.UtcNow + duration;
            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!frameQueue.TryTake(out var jpegBytes, millisecondsTimeout: 100, cancellationToken))
                    {
                        continue;
                    }

                    using var frame = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
                    if (frame.Empty())
                    {
                        continue;
                    }

                    using var videoFrame = CreateVideoFrame(frame, frameSize);
                    WriteFrameForElapsed(writer, videoFrame, recordingClock.Elapsed, outputFps, ref writtenFrameCount);
                    lastFrame.Dispose();
                    lastFrame = videoFrame.Clone();
                }

                WriteFrameForElapsed(writer, lastFrame, duration, outputFps, ref writtenFrameCount);
            }
            finally
            {
                lastFrame?.Dispose();
            }

            return outputPath;
        }
        finally
        {
            Volatile.Write(ref acceptingFrames, 0);
            VitureNative.xr_camera_provider_stop(handle);
            VitureNative.xr_camera_provider_destroy(handle);
            GC.KeepAlive(callback);
        }
    }

    public async IAsyncEnumerable<byte[]> StreamJpegFramesAsync(
        TimeSpan firstFrameTimeout,
        double maxFramesPerSecond,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NativeLibraryLoader.AddDllDirectory(Path.Combine(settings.VitureSdkRoot, "x86_64"));

        var handle = CreateCameraProvider();
        using var frameQueue = new System.Collections.Concurrent.BlockingCollection<VitureJpegFrame>(boundedCapacity: 1);
        var acceptingFrames = 1;
        VitureNative.XRCameraFrameCallback callback = (framePointer, _) =>
        {
            if (Volatile.Read(ref acceptingFrames) == 0 || framePointer == IntPtr.Zero)
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
            TryAddLatestFrame(frameQueue, new VitureJpegFrame(bytes, frame.Sequence));
        };

        try
        {
            var startResult = VitureNative.xr_camera_provider_start(handle, callback, IntPtr.Zero);
            if (startResult != VitureNative.VITURE_GLASSES_SUCCESS)
            {
                throw new InvalidOperationException($"xr_camera_provider_start failed with code {startResult}.");
            }

            using var firstFrameTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstFrameTimeoutCts.CancelAfter(firstFrameTimeout);
            var frameDelay = LiveFrameTiming.GetFrameDelay(maxFramesPerSecond);

            while (!cancellationToken.IsCancellationRequested)
            {
                VitureJpegFrame liveFrame;
                try
                {
                    liveFrame = frameQueue.Take(firstFrameTimeoutCts.Token);
                    while (frameQueue.TryTake(out var newerFrame))
                    {
                        liveFrame = newerFrame;
                    }

                    if (!firstFrameTimeoutCts.IsCancellationRequested)
                    {
                        firstFrameTimeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("No Viture camera frame was received before the timeout.");
                }

                yield return liveFrame.Bytes;

                if (frameDelay > TimeSpan.Zero)
                {
                    await Task.Delay(frameDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Volatile.Write(ref acceptingFrames, 0);
            VitureNative.xr_camera_provider_stop(handle);
            VitureNative.xr_camera_provider_destroy(handle);
            GC.KeepAlive(callback);
        }
    }

    private static Mat CreateVideoFrame(Mat frame, Size frameSize)
    {
        if (frame.Width == frameSize.Width && frame.Height == frameSize.Height)
        {
            return frame.Clone();
        }

        var resized = new Mat();
        Cv2.Resize(frame, resized, frameSize);
        return resized;
    }

    private static void WriteFrameForElapsed(
        VideoWriter writer,
        Mat frame,
        TimeSpan elapsed,
        double fps,
        ref int writtenFrameCount)
    {
        var targetFrameCount = Math.Max(1, (int)Math.Round(elapsed.TotalSeconds * fps));
        while (writtenFrameCount < targetFrameCount)
        {
            writer.Write(frame);
            writtenFrameCount++;
        }
    }

    private static void TryAddLatestFrame(
        System.Collections.Concurrent.BlockingCollection<VitureJpegFrame> frameQueue,
        VitureJpegFrame frame)
    {
        try
        {
            while (!frameQueue.TryAdd(frame))
            {
                frameQueue.TryTake(out _);
            }
        }
        catch (InvalidOperationException)
        {
            // The live stream has already finished.
        }
    }

    private IntPtr CreateCameraProvider()
    {
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

        return handle;
    }

    private static async Task<Mat> ReadFirstVitureVideoFrameAsync(
        System.Collections.Concurrent.BlockingCollection<byte[]> frameQueue,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            byte[] jpegBytes;
            try
            {
                jpegBytes = frameQueue.Take(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Yield();
            var frame = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
            if (!frame.Empty())
            {
                return frame;
            }

            frame.Dispose();
        }

        throw new TimeoutException("No Viture camera frame was received before the timeout.");
    }
}

internal static class LiveFrameTiming
{
    public static TimeSpan GetFrameDelay(double maxFramesPerSecond)
    {
        if (maxFramesPerSecond <= 0 || double.IsNaN(maxFramesPerSecond) || double.IsInfinity(maxFramesPerSecond))
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(1 / maxFramesPerSecond);
    }
}

internal readonly record struct VitureJpegFrame(byte[] Bytes, uint Sequence);

internal static class VideoFileWriterFactory
{
    public static VideoWriter Create(string outputPath, double fps, Size frameSize)
    {
        var writer = new VideoWriter(
            outputPath,
            VideoWriter.FourCC('M', 'J', 'P', 'G'),
            fps,
            frameSize);

        if (!writer.IsOpened())
        {
            writer.Dispose();
            throw new InvalidOperationException($"Unable to create video file: {outputPath}");
        }

        return writer;
    }
}
