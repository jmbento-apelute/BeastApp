internal enum LiveVideoEffect
{
    Normal,
    Grayscale,
    NightVision,
    Thermal,
    Edges,
    LowLightBoost,
    EdgeOverlay,
    MotionHighlight
}

internal static class LiveVideoEffects
{
    private static readonly object MotionGate = new();
    private static Mat? previousMotionFrame;

    private static readonly LiveVideoEffect[] EffectSequence =
    [
        LiveVideoEffect.Normal,
        LiveVideoEffect.Grayscale,
        LiveVideoEffect.LowLightBoost,
        LiveVideoEffect.NightVision,
        LiveVideoEffect.Thermal,
        LiveVideoEffect.Edges,
        LiveVideoEffect.EdgeOverlay,
        LiveVideoEffect.MotionHighlight
    ];

    public static LiveVideoEffect Next(LiveVideoEffect current)
    {
        var index = Array.IndexOf(EffectSequence, current);
        return EffectSequence[(index + 1) % EffectSequence.Length];
    }

    public static string GetDisplayName(LiveVideoEffect effect)
    {
        return effect switch
        {
            LiveVideoEffect.Normal => "Normal",
            LiveVideoEffect.Grayscale => "Blanco y negro",
            LiveVideoEffect.NightVision => "Vision nocturna",
            LiveVideoEffect.Thermal => "Termico",
            LiveVideoEffect.Edges => "Bordes",
            LiveVideoEffect.LowLightBoost => "Baja luz",
            LiveVideoEffect.EdgeOverlay => "Bordes superpuestos",
            LiveVideoEffect.MotionHighlight => "Movimiento",
            _ => effect.ToString()
        };
    }

    public static void ResetTemporalState()
    {
        lock (MotionGate)
        {
            previousMotionFrame?.Dispose();
            previousMotionFrame = null;
        }
    }

    public static byte[] ApplyToJpeg(byte[] jpegBytes, LiveVideoEffect effect)
    {
        if (effect == LiveVideoEffect.Normal)
        {
            return jpegBytes;
        }

        using var input = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (input.Empty())
        {
            return jpegBytes;
        }

        using var output = ApplyToMat(input, effect);
        return output.Empty() ? jpegBytes : EncodeJpeg(output);
    }

    private static Mat ApplyToMat(Mat input, LiveVideoEffect effect)
    {
        return effect switch
        {
            LiveVideoEffect.Grayscale => ApplyGrayscale(input),
            LiveVideoEffect.NightVision => ApplyNightVision(input),
            LiveVideoEffect.Thermal => ApplyThermal(input),
            LiveVideoEffect.Edges => ApplyEdges(input),
            LiveVideoEffect.LowLightBoost => ApplyLowLightBoost(input),
            LiveVideoEffect.EdgeOverlay => ApplyEdgeOverlay(input),
            LiveVideoEffect.MotionHighlight => ApplyMotionHighlight(input),
            _ => input.Clone()
        };
    }

    private static Mat ApplyGrayscale(Mat input)
    {
        var gray = new Mat();
        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat ApplyLowLightBoost(Mat input)
    {
        using var hsv = new Mat();
        using var boostedHsv = new Mat();
        var output = new Mat();

        Cv2.CvtColor(input, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        try
        {
            Cv2.EqualizeHist(channels[2], channels[2]);
            Cv2.ConvertScaleAbs(channels[2], channels[2], alpha: 1.15, beta: 8);
            Cv2.Merge(channels, boostedHsv);
            Cv2.CvtColor(boostedHsv, output, ColorConversionCodes.HSV2BGR);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }

        return output;
    }

    private static Mat ApplyNightVision(Mat input)
    {
        using var gray = new Mat();
        using var boosted = new Mat();
        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, boosted);
        Cv2.ConvertScaleAbs(boosted, boosted, alpha: 1.25, beta: 12);

        using var zero = Mat.Zeros(boosted.Size(), MatType.CV_8UC1);
        var output = new Mat();
        Cv2.Merge([zero, boosted, zero], output);
        return output;
    }

    private static Mat ApplyThermal(Mat input)
    {
        using var gray = new Mat();
        using var boosted = new Mat();
        var output = new Mat();
        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, boosted);
        Cv2.ApplyColorMap(boosted, output, ColormapTypes.Jet);
        return output;
    }

    private static Mat ApplyEdges(Mat input)
    {
        using var gray = new Mat();
        using var blurred = new Mat();
        using var edges = new Mat();
        var output = new Mat();

        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.2);
        Cv2.Canny(blurred, edges, 60, 140);
        Cv2.CvtColor(edges, output, ColorConversionCodes.GRAY2BGR);
        return output;
    }

    private static Mat ApplyEdgeOverlay(Mat input)
    {
        using var gray = new Mat();
        using var blurred = new Mat();
        using var edges = new Mat();
        using var overlay = new Mat(input.Size(), input.Type(), Scalar.All(0));
        var output = new Mat();

        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.2);
        Cv2.Canny(blurred, edges, 60, 140);
        overlay.SetTo(new Scalar(0, 255, 0), edges);
        Cv2.AddWeighted(input, 0.85, overlay, 0.75, 0, output);
        return output;
    }

    private static Mat ApplyMotionHighlight(Mat input)
    {
        using var gray = new Mat();
        using var current = new Mat();
        Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, current, new Size(7, 7), 1.5);

        lock (MotionGate)
        {
            if (previousMotionFrame is null
                || previousMotionFrame.Width != current.Width
                || previousMotionFrame.Height != current.Height)
            {
                previousMotionFrame?.Dispose();
                previousMotionFrame = current.Clone();
                return input.Clone();
            }

            using var diff = new Mat();
            using var mask = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var overlay = new Mat(input.Size(), input.Type(), Scalar.All(0));
            var output = new Mat();

            Cv2.Absdiff(previousMotionFrame, current, diff);
            Cv2.Threshold(diff, mask, 20, 255, ThresholdTypes.Binary);
            Cv2.Dilate(mask, mask, kernel, iterations: 2);
            overlay.SetTo(new Scalar(0, 0, 255), mask);
            Cv2.AddWeighted(input, 0.80, overlay, 0.80, 0, output);

            previousMotionFrame.Dispose();
            previousMotionFrame = current.Clone();
            return output;
        }
    }

    private static byte[] EncodeJpeg(Mat frame)
    {
        Cv2.ImEncode(".jpg", frame, out var jpegBytes);
        return jpegBytes;
    }
}
