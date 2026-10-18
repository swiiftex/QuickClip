using System.Globalization;

namespace QuickClip.Media;

/// <summary>
/// Turns high frame rate video into a lower rate with motion blur made from the frames in between, so 60 fps from a
/// 120 fps recording looks smoother than dropping every other frame. Each output frame is what a camera shutter open
/// for the whole frame would have seen: the source frames across that time, mixed in linear light (mixing the
/// brightness values as stored would dim bright things in motion), with the frames at its two edges counting half.
/// </summary>
internal static class MotionBlur
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Whether there are enough extra frames to blend: at least about one and a half source frames per output frame.</summary>
    public static bool Helps(double sourceFps, double targetFps) => sourceFps > 0 && targetFps > 0 && sourceFps / targetFps >= 1.5;

    /// <summary>Source frames per output frame (2 for 120 into 60).</summary>
    public static int Span(double sourceFps, double targetFps) => Math.Max(1, (int)Math.Round(sourceFps / targetFps));

    /// <summary>
    /// The filters, for BT.709 video (as recordings are); the output is 4:2:0 at <paramref name="targetFps"/>.
    /// <paramref name="fullRange"/>: the video uses 0-255 rather than TV levels.
    /// </summary>
    public static string Filter(double sourceFps, double targetFps, bool fullRange = false)
    {
        int span = Span(sourceFps, targetFps);
        // The shutter's whole time, sampled at every source frame: the ones at its edges are shared with the next
        // output frame, so they count half (trapezoid weights: 1 2 1 for 120 into 60).
        string weights = string.Join(" ", Enumerable.Range(0, span + 1).Select(i => i == 0 || i == span ? 1 : 2));
        // tmix mixes a frame with the ones before it; move each result back to the middle of the time it covers.
        double shift = span / 2.0 / sourceFps;
        string range = fullRange ? "full" : "limited";
        return string.Join(",",
            $"zscale=min=709:tin=709:pin=709:rin={range}:m=gbr:t=linear:p=709:r=full:npl=100",
            "format=gbrpf32le",
            $"tmix=frames={span + 1}:weights='{weights}'",
            $"setpts=PTS-{F(shift)}/TB",
            $"fps={F(targetFps)}",
            $"zscale=min=gbr:tin=linear:pin=709:rin=full:m=709:t=709:p=709:r={range}:npl=100",
            "format=yuv420p");
    }
}
