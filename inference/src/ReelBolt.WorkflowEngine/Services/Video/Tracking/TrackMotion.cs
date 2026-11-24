namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// The motion word of a track ("Static", "Slow" or "Moving"), from the travel of its centroid. One rule for every
/// tracker, so a plate found by colour and an object followed by its features read the same.
/// </summary>
internal static class TrackMotion
{
    /// <summary>
    /// Net centroid speed, bucketed. Thresholds in normalized frame units/second.
    ///
    /// <para>
    /// Measured over half-second strides of the (smoothed) track, not frame to frame. Summing
    /// per-frame steps measures grid quantization, not motion: one grid pixel is ~0.5% of frame,
    /// so a plate that never moves but whose fitted corner flickers by one pixel at 12 fps already
    /// "travels" 0.06 frames/second — twice the Moving bar. That is how a phone held almost still
    /// was offered to the planner as "Moving". Over a stride, back-and-forth jitter cancels while
    /// genuine travel accumulates.
    /// </para>
    /// </summary>
    /// <param name="count">Samples in the track.</param>
    /// <param name="fps">Samples per second.</param>
    /// <param name="centroid">The centroid of sample <c>i</c>.</param>
    /// <param name="width">What one frame width is in the centroid's units: 1 when it is already normalized, else the frame width in pixels.</param>
    /// <param name="height">The same for the frame height.</param>
    public static string Classify(int count, double fps, Func<int, (double X, double Y)> centroid, double width = 1, double height = 1)
    {
        if (count < 2)
            return "Static";

        int stride = Math.Clamp((int)Math.Round(fps / 2), 1, count - 1);
        double total = 0;
        int steps = 0;
        for (int i = stride; i < count; i += stride)
        {
            (double ax, double ay) = centroid(i - stride);
            (double bx, double by) = centroid(i);
            total += Math.Sqrt(Math.Pow((bx - ax) / width, 2) + Math.Pow((by - ay) / height, 2));
            steps++;
        }

        double perSecond = steps == 0 ? 0 : total / (steps * stride) * fps;
        return perSecond switch
        {
            < 0.005 => "Static",
            < 0.03 => "Slow",
            _ => "Moving"
        };
    }
}
