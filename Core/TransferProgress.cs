using System;

namespace NightLightControl.Core;

public sealed class TransferProgress(IProgress<double>? progress, Func<double> seconds)
{
    private double lastReport = double.NegativeInfinity;
    private bool finished;
    public void Report(long received, long total)
    {
        if (progress == null || finished) return;
        double now = seconds(); bool done = received >= total;
        if (!done && now - lastReport < .2) return;
        lastReport = now; finished = done;
        progress.Report(Math.Clamp(100.0 * received / Math.Max(total, 1), 0, 100));
    }
}
