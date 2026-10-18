using System;

namespace GamerTool.Services;

/// <summary>
/// Fit-to-screen decision for the fixed-design window.
/// Pure so the four audit configurations are unit-testable:
/// 1366x768@100, 1920x1080@150, 2560x1440@100, 1280x720@125.
/// </summary>
public static class WindowFit
{
    public const double DesignWidth = 1200.0;

    public const double DesignHeight = 790.0;

    /// <summary>Never scale below this: text becomes unreadable past it.</summary>
    public const double MinScale = 0.65;

    /// <summary>
    /// Uniform scale so the design rect fits the work area, or 1 when it fits.
    /// Inputs are DIPs (work area) + design DIPs; DPI already applied by WPF.
    /// </summary>
    public static double ScaleFor(double workWidth, double workHeight)
    {
        if (workWidth <= 0 || workHeight <= 0)
        {
            return 1.0;
        }

        double scale = Math.Min(workWidth / DesignWidth, workHeight / DesignHeight);
        if (scale >= 1.0)
        {
            return 1.0;
        }

        return Math.Max(MinScale, scale);
    }
}
