internal sealed class LiveStreamingServer(AppSettings settings) : IDisposable
{
    private readonly SemaphoreSlim streamClientGate = new(1, 1);
    private readonly object latestFrameGate = new();
    private Func<LiveVideoEffect> getEffect = () => LiveVideoEffect.Normal;
    private Func<byte[], string, Task> handleGestureCapture = (_, _) => Task.CompletedTask;
    private Func<ConsoleKey, Task> handleCommandKey = _ => Task.CompletedTask;
    private System.Net.HttpListener? listener;
    private CancellationTokenSource? serverCts;
    private Task? serverTask;
    private ILiveFrameSource? source;
    private byte[]? latestJpegBytes;
    private string latestFrameSourceName = string.Empty;

    public bool IsRunning => listener?.IsListening == true;

    public string Url => settings.LiveStreamUrl;

    public void Start(
        ILiveFrameSource frameSource,
        Func<LiveVideoEffect> liveEffectProvider,
        Func<byte[], string, Task> gestureCaptureHandler,
        Func<ConsoleKey, Task> commandKeyHandler)
    {
        if (IsRunning)
        {
            return;
        }

        source = frameSource;
        getEffect = liveEffectProvider;
        handleGestureCapture = gestureCaptureHandler;
        handleCommandKey = commandKeyHandler;
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
        getEffect = () => LiveVideoEffect.Normal;
        handleGestureCapture = (_, _) => Task.CompletedTask;
        handleCommandKey = _ => Task.CompletedTask;
        lock (latestFrameGate)
        {
            latestJpegBytes = null;
            latestFrameSourceName = string.Empty;
        }
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

            if (path.Equals("/effect.json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteEffectJsonAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/gesture/capture", StringComparison.OrdinalIgnoreCase))
            {
                await HandleGestureCaptureAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/command/key", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCommandKeyAsync(context).ConfigureAwait(false);
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
                .viewer { position: relative; min-height: 0; background: #000; overflow: hidden; }
                img, canvas { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
                canvas { pointer-events: none; }
                small { color: #bdbdbd; }
                .hud { position: absolute; left: 12px; bottom: 12px; padding: 8px 10px; background: rgb(0 0 0 / 0.62); color: #f4f4f4; border: 1px solid rgb(255 255 255 / 0.16); font-size: 13px; }
                .armed { color: #78ff9a; }
                .idle { color: #f6d365; }
              </style>
              <script src="https://cdn.jsdelivr.net/npm/@mediapipe/hands/hands.js"></script>
            </head>
            <body>
              <main>
                <header>
                  <strong>Beast Live</strong>
                  <small>{{System.Net.WebUtility.HtmlEncode(source?.Name ?? "Unknown source")}} - efecto: <span id="effect">{{System.Net.WebUtility.HtmlEncode(LiveVideoEffects.GetDisplayName(getEffect()))}}</span> - gestos: <span id="gestures" class="idle">OFF</span></small>
                </header>
                <section class="viewer">
                  <img id="live" src="/stream.mjpeg" alt="Live camera stream">
                  <canvas id="overlay"></canvas>
                  <div class="hud">G activa gestos. Pinza = captura. E/L/C/V/S/Q se envian a BeastApp.</div>
                </section>
              </main>
              <script>
                const liveImage = document.getElementById('live');
                const overlay = document.getElementById('overlay');
                const overlayContext = overlay.getContext('2d');
                const gestureStatus = document.getElementById('gestures');
                let gesturesEnabled = false;
                let handsReady = false;
                let handsBusy = false;
                let wasPinched = false;
                let lastClickAt = 0;
                let audioContext;
                let hands;

                async function refreshEffect() {
                  try {
                    const response = await fetch('/effect.json', { cache: 'no-store' });
                    if (!response.ok) return;
                    const data = await response.json();
                    document.getElementById('effect').textContent = data.name;
                  } catch {
                  }
                }
                setInterval(refreshEffect, 500);

                function ensureAudio() {
                  audioContext ??= new (window.AudioContext || window.webkitAudioContext)();
                  if (audioContext.state === 'suspended') {
                    audioContext.resume();
                  }
                }

                function playClickSound() {
                  ensureAudio();
                  const now = audioContext.currentTime;
                  const gain = audioContext.createGain();
                  const osc = audioContext.createOscillator();
                  osc.type = 'square';
                  osc.frequency.setValueAtTime(900, now);
                  osc.frequency.exponentialRampToValueAtTime(1400, now + 0.035);
                  gain.gain.setValueAtTime(0.0001, now);
                  gain.gain.exponentialRampToValueAtTime(0.20, now + 0.006);
                  gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.075);
                  osc.connect(gain);
                  gain.connect(audioContext.destination);
                  osc.start(now);
                  osc.stop(now + 0.08);
                }

                function sendGestureCapture() {
                  fetch('/gesture/capture', {
                    method: 'POST',
                    cache: 'no-store'
                  }).catch(() => {});
                }

                function sendCommandKey(key) {
                  fetch('/command/key', {
                    method: 'POST',
                    cache: 'no-store',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ key })
                  }).catch(() => {});
                }

                function distance(a, b) {
                  const dx = a.x - b.x;
                  const dy = a.y - b.y;
                  return Math.hypot(dx, dy);
                }

                function setGestureStatus(enabled) {
                  gestureStatus.textContent = enabled ? 'ON' : 'OFF';
                  gestureStatus.className = enabled ? 'armed' : 'idle';
                }

                function resizeOverlay() {
                  const width = overlay.clientWidth;
                  const height = overlay.clientHeight;
                  if (overlay.width !== width || overlay.height !== height) {
                    overlay.width = width;
                    overlay.height = height;
                  }
                }

                function drawLandmarks(landmarks, pinched) {
                  resizeOverlay();
                  overlayContext.clearRect(0, 0, overlay.width, overlay.height);
                  overlayContext.fillStyle = pinched ? '#78ff9a' : '#f6d365';
                  overlayContext.strokeStyle = pinched ? '#78ff9a' : '#f6d365';
                  overlayContext.lineWidth = pinched ? 4 : 2;

                  for (const point of landmarks) {
                    overlayContext.beginPath();
                    overlayContext.arc(point.x * overlay.width, point.y * overlay.height, 4, 0, Math.PI * 2);
                    overlayContext.fill();
                  }

                  const thumb = landmarks[4];
                  const index = landmarks[8];
                  overlayContext.beginPath();
                  overlayContext.moveTo(thumb.x * overlay.width, thumb.y * overlay.height);
                  overlayContext.lineTo(index.x * overlay.width, index.y * overlay.height);
                  overlayContext.stroke();
                }

                function handlePinch(landmarks) {
                  const thumbTip = landmarks[4];
                  const indexTip = landmarks[8];
                  const wrist = landmarks[0];
                  const middleBase = landmarks[9];
                  const handScale = Math.max(distance(wrist, middleBase), 0.001);
                  const pinchDistance = distance(thumbTip, indexTip);
                  const pinchOn = pinchDistance < handScale * 0.42;
                  const pinchOff = pinchDistance > handScale * 0.58;
                  const now = performance.now();

                  if (pinchOn && !wasPinched && now - lastClickAt > 650) {
                    lastClickAt = now;
                    wasPinched = true;
                    playClickSound();
                    sendGestureCapture();
                  } else if (pinchOff) {
                    wasPinched = false;
                  }

                  return wasPinched;
                }

                async function initializeHands() {
                  if (handsReady || !window.Hands) {
                    return;
                  }

                  hands = new Hands({
                    locateFile: file => `https://cdn.jsdelivr.net/npm/@mediapipe/hands/${file}`
                  });
                  hands.setOptions({
                    maxNumHands: 1,
                    modelComplexity: 1,
                    minDetectionConfidence: 0.65,
                    minTrackingConfidence: 0.65
                  });
                  hands.onResults(results => {
                    overlayContext.clearRect(0, 0, overlay.width, overlay.height);
                    const landmarks = results.multiHandLandmarks?.[0];
                    if (!gesturesEnabled || !landmarks) {
                      wasPinched = false;
                      return;
                    }

                    const pinched = handlePinch(landmarks);
                    drawLandmarks(landmarks, pinched);
                  });
                  handsReady = true;
                }

                async function analyzeGestureFrame() {
                  if (!gesturesEnabled || !handsReady || handsBusy || !liveImage.complete || liveImage.naturalWidth === 0) {
                    return;
                  }

                  handsBusy = true;
                  try {
                    await hands.send({ image: liveImage });
                  } catch {
                  } finally {
                    handsBusy = false;
                  }
                }

                async function toggleGestures() {
                  gesturesEnabled = !gesturesEnabled;
                  setGestureStatus(gesturesEnabled);
                  ensureAudio();
                  if (gesturesEnabled) {
                    await initializeHands();
                  } else {
                    overlayContext.clearRect(0, 0, overlay.width, overlay.height);
                    wasPinched = false;
                  }
                }

                document.addEventListener('keydown', event => {
                  if (event.repeat) {
                    return;
                  }

                  const key = event.key.toUpperCase();
                  if (key === 'G') {
                    toggleGestures();
                    event.preventDefault();
                    return;
                  }

                  if (['E', 'L', 'C', 'V', 'S', 'Q'].includes(key)) {
                    sendCommandKey(key);
                    event.preventDefault();
                  }
                });
                document.addEventListener('pointerdown', ensureAudio, { once: true });
                window.addEventListener('resize', resizeOverlay);
                setGestureStatus(false);
                resizeOverlay();
                setInterval(analyzeGestureFrame, 90);
              </script>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private Task WriteEffectJsonAsync(System.Net.HttpListenerContext context)
    {
        var effectName = LiveVideoEffects.GetDisplayName(getEffect());
        var json = JsonSerializer.Serialize(new { name = effectName });
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private async Task HandleGestureCaptureAsync(System.Net.HttpListenerContext context)
    {
        var accepted = QueueLatestFrameCapture();
        if (!accepted)
        {
            context.Response.StatusCode = 409;
            await WriteTextAsync(context, "No live frame is available yet.").ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context, new { accepted = true }).ConfigureAwait(false);
    }

    private async Task HandleCommandKeyAsync(System.Net.HttpListenerContext context)
    {
        if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 405;
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        string? keyText = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("key", out var keyElement))
            {
                keyText = keyElement.GetString();
            }
        }

        var key = ParseCommandKey(keyText);
        if (key is null)
        {
            context.Response.StatusCode = 400;
            await WriteTextAsync(context, "Unsupported key.").ConfigureAwait(false);
            return;
        }

        if (key == ConsoleKey.C)
        {
            var accepted = QueueLatestFrameCapture();
            if (!accepted)
            {
                context.Response.StatusCode = 409;
                await WriteTextAsync(context, "No live frame is available yet.").ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context, new { accepted = true, key = key.ToString() }).ConfigureAwait(false);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await handleCommandKey(key.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Browser key command failed: {ex.Message}");
                Console.ResetColor();
            }
        });

        await WriteJsonAsync(context, new { accepted = true, key = key.ToString() }).ConfigureAwait(false);
    }

    private bool QueueLatestFrameCapture()
    {
        byte[]? frameBytes;
        string sourceName;
        lock (latestFrameGate)
        {
            frameBytes = latestJpegBytes?.ToArray();
            sourceName = latestFrameSourceName;
        }

        if (frameBytes is null)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await handleGestureCapture(frameBytes, sourceName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Gesture capture failed: {ex.Message}");
                Console.ResetColor();
            }
        });

        return true;
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
            StoreLatestFrame(jpegBytes);
            var outputBytes = LiveVideoEffects.ApplyToJpeg(jpegBytes, getEffect());
            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength64 = outputBytes.Length;
            await context.Response.OutputStream.WriteAsync(outputBytes, serverCancellationToken).ConfigureAwait(false);
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
                StoreLatestFrame(jpegBytes);
                var outputBytes = LiveVideoEffects.ApplyToJpeg(jpegBytes, getEffect());
                var header = Encoding.ASCII.GetBytes(
                    $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {outputBytes.Length}\r\n\r\n");
                await context.Response.OutputStream.WriteAsync(header, serverCancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.WriteAsync(outputBytes, serverCancellationToken).ConfigureAwait(false);
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

    private static Task WriteJsonAsync(System.Net.HttpListenerContext context, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static ConsoleKey? ParseCommandKey(string? keyText)
    {
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return null;
        }

        return keyText.Trim().ToUpperInvariant() switch
        {
            "C" => ConsoleKey.C,
            "V" => ConsoleKey.V,
            "L" => ConsoleKey.L,
            "E" => ConsoleKey.E,
            "S" => ConsoleKey.S,
            "Q" => ConsoleKey.Q,
            _ => null
        };
    }

    private void StoreLatestFrame(byte[] jpegBytes)
    {
        lock (latestFrameGate)
        {
            latestJpegBytes = jpegBytes.ToArray();
            latestFrameSourceName = source?.Name ?? "Live";
        }
    }
}
