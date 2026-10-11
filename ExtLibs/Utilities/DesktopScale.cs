using System;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// The desktop scale relative to 96 DPI, for code that sizes or draws in pixels. It is 1 unless
    /// Mission Planner scales itself for a high DPI desktop, see LinuxDesktop.
    /// </summary>
    public static class DesktopScale
    {
        public static float Factor { get; set; } = 1;

        /// <summary>
        /// Converts a size or position in 96 DPI pixels to pixels on this desktop.
        /// </summary>
        public static int Scale(int value)
        {
            return (int) Math.Round(value * Factor);
        }

        /// <summary>
        /// Converts pixels on this desktop to 96 DPI pixels, for sizes and positions that are saved,
        /// so a saved layout stays the same when the desktop scale changes.
        /// </summary>
        public static int Unscale(int value)
        {
            return (int) Math.Round(value / Factor);
        }
    }
}
