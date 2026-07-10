internal sealed class LiveStreamingServer(AppSettings settings) : IDisposable
{
    private readonly SemaphoreSlim streamClientGate = new(1, 1);
    private System.Net.HttpListener? listener;
    private CancellationTokenSource? serverCts;
    private Task? serverTask;
    private ILiveFrameSource? source;

    public bool IsRunning => listener?.IsListening == true;

    public string Url => settings.LiveStreamUrl;

    public void Start(ILiveFrameSource frameSource)
    {
        if (IsRunning)
        {
            return;
        }

        source = frameSource;
        serverCts = new CancellationTokenSource();
        listener = new System.Net.HttpListener();
        listener.Prefixes.Add(settings.LiveStreamUrl);
        listener.Start();
        serverTask = Task.Run(() => AcceptLoopAsync(serverCts.Token));
    }

    public async Task StopAsync()
    {
        if (!IsRunning && serverTask is null)
        {
            return;
        }

        serverCts?.Cancel();

        try
        {
            listener?.Stop();
        }
        catch
        {
        }

        if (serverTask is not null)
        {
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        listener?.Close();
        listener = null;
        serverCts?.Dispose();
        serverCts = null;
        serverTask = null;
        source = null;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        streamClientGate.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is { IsListening: true } activeListener)
        {
            System.Net.HttpListenerContext context;
            try
            {
                context = await activeListener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (System.Net.HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(System.Net.HttpListenerContext context, CancellationToken serverCancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteIndexAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/stream.mjpeg", StringComparison.OrdinalIgnoreCase))
            {
                await WriteMjpegStreamAsync(context, serverCancellationToken).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/snapshot.jpg", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSnapshotAsync(context, serverCancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            if (settings.VoiceCommandDebug)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Live stream request failed: {ex.Message}");
                Console.ResetColor();
            }

            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = 500;
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private Task WriteIndexAsync(System.Net.HttpListenerContext context)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Beast Live</title>
              <style>
                html, body { margin: 0; height: 100%; background: #101010; color: #f4f4f4; font-family: system-ui, sans-serif; }
                main { min-height: 100%; display: grid; grid-template-rows: auto 1fr; }
                header { padding: 12px 16px; border-bottom: 1px solid #2b2b2b; display: flex; justify-content: space-between; gap: 16px; }
                img { width: 100%; height: 100%; object-fit: contain; background: #000; }
                small { color: #bdbdbd; }
              </style>
            </head>
            <body>
              <main>
                <header>
                  <strong>Beast Live</strong>
                  <small>{{System.Net.WebUtility.HtmlEncode(source?.Name ?? "Unknown source")}}</small>
                </header>
                <img src="/stream.mjpeg" alt="Live camera stream">
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private async Task WriteSnapshotAsync(
        System.Net.HttpListenerContext context,
        CancellationToken serverCancellationToken)
    {
        if (source is null)
        {
            context.Response.StatusCode = 503;
            return;
        }

        await foreach (var jpegBytes in source.StreamJpegFramesAsync(
            settings.CaptureTimeout,
            settings.LiveStreamFramesPerSecond,
            serverCancellationToken).ConfigureAwait(false))
        {
            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength64 = jpegBytes.Length;
            await context.Response.OutputStream.WriteAsync(jpegBytes, serverCancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task WriteMjpegStreamAsync(
        System.Net.HttpListenerContext context,
        CancellationToken serverCancellationToken)
    {
        if (source is null)
        {
            context.Response.StatusCode = 503;
            return;
        }

        if (!await streamClientGate.WaitAsync(0, serverCancellationToken).ConfigureAwait(false))
        {
            context.Response.StatusCode = 409;
            await WriteTextAsync(context, "A live stream client is already connected.").ConfigureAwait(false);
            return;
        }

        try
        {
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";

            await foreach (var jpegBytes in source.StreamJpegFramesAsync(
                settings.CaptureTimeout,
                settings.LiveStreamFramesPerSecond,
                serverCancellationToken).ConfigureAwait(false))
            {
                var header = Encoding.ASCII.GetBytes(
                    $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegBytes.Length}\r\n\r\n");
                await context.Response.OutputStream.WriteAsync(header, serverCancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.WriteAsync(jpegBytes, serverCancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), serverCancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(serverCancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            streamClientGate.Release();
        }
    }

    private static Task WriteTextAsync(System.Net.HttpListenerContext context, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }
}
