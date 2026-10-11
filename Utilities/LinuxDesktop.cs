using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Desktop integration for Linux, where Mono draws WinForms through X11.
    /// </summary>
    public static class LinuxDesktop
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Matches StartupWMClass in a .desktop launcher, so docks group the window with its launcher.
        /// </summary>
        public const string WMClass = "MissionPlanner";

        // Mono drawing through an X server. Android also reports Linux but has no X display
        private static bool IsX11 => Program.MONO && RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                                     !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        [StructLayout(LayoutKind.Sequential)]
        private struct XClassHint
        {
            public IntPtr res_name;
            public IntPtr res_class;
        }

        [DllImport("libX11")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11")]
        private static extern int XSetClassHint(IntPtr display, IntPtr window, ref XClassHint hint);

        [DllImport("libX11")]
        private static extern int XFlush(IntPtr display);

        [DllImport("libX11")]
        private static extern IntPtr XGetDefault(IntPtr display, string program, string option);

        private static float desktopScale = 1;

        // controls already scaled, ApplyThemeTo runs on the same controls more than once
        private static readonly ConditionalWeakTable<object, object> scaled = new ConditionalWeakTable<object, object>();

        // controls whose layout is already scaled, by Mono's autoscale or by ScaleControl
        private static readonly ConditionalWeakTable<object, object> layoutScaled = new ConditionalWeakTable<object, object>();

        /// <summary>
        /// Call before anything uses WinForms or System.Drawing. A scaled desktop gives X11 apps an
        /// Xft.dpi above 96 (192 under GNOME's XWayland at 2x). Mono then reports the higher DPI in
        /// font metrics but still draws text at 96 DPI and does not grow layouts, so the UI is
        /// broken and small. Keep Mono at 96 DPI and remember the scale for ApplyDesktopScale.
        /// </summary>
        public static void PrepareDesktopScale()
        {
            if (!IsX11)
                return;

            try
            {
                var display = XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                    return;

                float dpi = 96;
                try
                {
                    var value = XGetDefault(display, "Xft", "dpi");
                    if (value != IntPtr.Zero)
                        float.TryParse(Marshal.PtrToStringAnsi(value), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out dpi);
                }
                finally
                {
                    XCloseDisplay(display);
                }

                if (dpi <= 96)
                    return;

                // resources in XENVIRONMENT override the desktop's for this process only,
                // and libgdiplus takes its DPI from Xft.dpi
                var file = Path.Combine(Settings.GetDataDirectory(), "xresources");
                File.WriteAllText(file, "Xft.dpi: 96\n");
                Environment.SetEnvironmentVariable("XENVIRONMENT", file);

                desktopScale = dpi / 96f;
                DesktopScale.Factor = desktopScale;
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        /// <summary>
        /// Call after Application.EnableVisualStyles, which decides Mono's theme. Grows the theme's
        /// default font by the desktop scale, so controls inherit it and forms using
        /// AutoScaleMode.Font scale their layout to match, as on a high DPI Windows desktop.
        /// </summary>
        public static void ApplyDesktopScale()
        {
            if (desktopScale == 1)
                return;

            try
            {
                var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var assembly = typeof(Control).Assembly;
                var theme = assembly.GetType("System.Windows.Forms.ThemeEngine")?.GetProperty("Current", flags)?.GetValue(null);
                var field = assembly.GetType("System.Windows.Forms.Theme")?.GetField("default_font", flags);
                if (theme == null || field == null)
                    return;

                var font = SystemFonts.DefaultFont;
                field.SetValue(theme, new Font(font.FontFamily, font.Size * desktopScale, font.Style));

                // map tiles are fixed size bitmaps, draw them larger like a browser does
                GMap.NET.WindowsForms.GMapControl.TileScale = desktopScale;
                log.InfoFormat("Desktop scale {0}", desktopScale);
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        /// <summary>
        /// Scales what the default font does not reach: fonts set in the designer, pixel sizes such as
        /// tool strip images and table rows, and layouts Mono never autoscaled. A container using
        /// AutoScaleMode.Font has scaled itself from the larger default font, and Mono scales the
        /// children it had then. A container left at Inherit, such as a screen created on its own and
        /// added to the main window later, is never scaled, so scale it here. Call this before
        /// AutoScaleMode is turned off. Each control is only scaled once.
        /// </summary>
        public static void ScaleControl(Control control)
        {
            if (desktopScale == 1 || control == null)
                return;

            try
            {
                var parentScaled = false;
                for (var parent = control.Parent; parent != null && !parentScaled; parent = parent.Parent)
                    parentScaled = layoutScaled.TryGetValue(parent, out _);

                ScaleTree(control, parentScaled);
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        private static void ScaleTree(Control control, bool parentScaled)
        {
            if (!layoutScaled.TryGetValue(control, out _))
            {
                if (!parentScaled && control is ContainerControl container &&
                    (container.AutoScaleMode == AutoScaleMode.Inherit || container.AutoScaleMode == AutoScaleMode.None))
                {
                    control.Scale(new SizeF(desktopScale, desktopScale));
                    parentScaled = true;
                }
                else if (control is ContainerControl)
                {
                    // scaled itself with AutoScaleMode.Font or Dpi
                    parentScaled = true;
                }

                if (parentScaled)
                {
                    layoutScaled.Add(control, null);

                    // spacing is in pixels and Mono's Scale only scales bounds
                    control.Margin = ScalePadding(control.Margin);
                    control.Padding = ScalePadding(control.Padding);

                    if (control is TabControl tabs)
                    {
                        tabs.Padding = new Point(DesktopScale.Scale(tabs.Padding.X), DesktopScale.Scale(tabs.Padding.Y));
                        tabs.ItemSize = ScaleSize(tabs.ItemSize);
                    }

                    // Mono scales bounds but not fixed table rows and columns
                    if (control is TableLayoutPanel table)
                    {
                        foreach (ColumnStyle style in table.ColumnStyles)
                            if (style.SizeType == SizeType.Absolute)
                                style.Width *= desktopScale;
                        foreach (RowStyle style in table.RowStyles)
                            if (style.SizeType == SizeType.Absolute)
                                style.Height *= desktopScale;
                    }

                    // grid columns and rows are fixed pixels too
                    if (control is DataGridView grid)
                    {
                        grid.ColumnHeadersHeight = DesktopScale.Scale(grid.ColumnHeadersHeight);
                        grid.RowHeadersWidth = DesktopScale.Scale(grid.RowHeadersWidth);
                        grid.RowTemplate.Height = DesktopScale.Scale(grid.RowTemplate.Height);
                        foreach (DataGridViewColumn column in grid.Columns)
                        {
                            // grow the width first, the minimum may not exceed it
                            column.Width = DesktopScale.Scale(column.Width);
                            column.MinimumWidth = DesktopScale.Scale(column.MinimumWidth);
                            ScaleStyleFont(column.DefaultCellStyle);
                        }

                        // cell and header fonts set in the designer live in the cell styles
                        ScaleStyleFont(grid.DefaultCellStyle);
                        ScaleStyleFont(grid.ColumnHeadersDefaultCellStyle);
                        ScaleStyleFont(grid.RowHeadersDefaultCellStyle);
                        ScaleStyleFont(grid.RowsDefaultCellStyle);
                        ScaleStyleFont(grid.AlternatingRowsDefaultCellStyle);
                    }
                }
            }

            if (!scaled.TryGetValue(control, out _))
            {
                scaled.Add(control, null);

                // an inherited font already follows the scaled default font or the parent's
                var inherited = control.Parent != null ? control.Parent.Font : Control.DefaultFont;
                if (!control.Font.Equals(inherited))
                    control.Font = ScaleFont(control.Font);

                // controls that size themselves to their text
                if (control.AutoSize && (control is Label || control is CheckBox || control is RadioButton))
                    control.Padding = AddTextPadding(control.Padding, control.Font);

                if (control is ToolStrip strip)
                {
                    strip.ImageScalingSize = ScaleSize(strip.ImageScalingSize);

                    foreach (ToolStripItem item in strip.Items)
                        if (!scaled.TryGetValue(item, out _))
                        {
                            scaled.Add(item, null);
                            if (!item.Font.Equals(strip.Font))
                                item.Font = ScaleFont(item.Font);
                            item.Margin = ScalePadding(item.Margin);
                            item.Padding = AddTextPadding(ScalePadding(item.Padding), item.Font);
                        }
                }
            }

            foreach (Control child in control.Controls)
                ScaleTree(child, parentScaled);
        }

        /// <summary>
        /// Mono's TextRenderer pads measured text by a fixed number of pixels, where Windows pads in
        /// proportion to the font, so text sized controls come out too narrow at a larger scale.
        /// Add the padding the larger font is missing, half on each side.
        /// </summary>
        private static Padding AddTextPadding(Padding padding, Font font)
        {
            var built = TextRenderer.MeasureText("I", font).Width -
                        TextRenderer.MeasureText("I", font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var side = (int) Math.Round(built * (desktopScale - 1) / 2);
            return new Padding(padding.Left + side, padding.Top, padding.Right + side, padding.Bottom);
        }

        private static Padding ScalePadding(Padding padding)
        {
            return new Padding(DesktopScale.Scale(padding.Left), DesktopScale.Scale(padding.Top),
                DesktopScale.Scale(padding.Right), DesktopScale.Scale(padding.Bottom));
        }

        private static void ScaleStyleFont(DataGridViewCellStyle style)
        {
            if (style?.Font != null)
                style.Font = ScaleFont(style.Font);
        }

        private static Font ScaleFont(Font font)
        {
            return new Font(font.FontFamily, font.Size * desktopScale, font.Style, font.Unit);
        }

        private static Size ScaleSize(Size size)
        {
            return new Size((int) Math.Round(size.Width * desktopScale), (int) Math.Round(size.Height * desktopScale));
        }

        /// <summary>
        /// Mono does not set WM_CLASS on its windows, so GNOME and other docks cannot match the
        /// window to its launcher and show it as an unknown app. Set it on the top level window.
        /// </summary>
        public static void SetWMClass(Form form)
        {
            if (!IsX11)
                return;

            try
            {
                // form.Handle is Mono's client window, WM_CLASS belongs on the whole (top level) window
                var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hwndType = typeof(Control).Assembly.GetType("System.Windows.Forms.Hwnd");
                var hwnd = hwndType?.GetMethod("ObjectFromHandle", flags)?.Invoke(null, new object[] {form.Handle});
                var window = hwnd == null ? IntPtr.Zero : (IntPtr) hwndType.GetProperty("WholeWindow", flags).GetValue(hwnd);
                if (window == IntPtr.Zero)
                    return;

                var display = XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                    return;

                var name = Marshal.StringToHGlobalAnsi(WMClass);
                try
                {
                    var hint = new XClassHint {res_name = name, res_class = name};
                    XSetClassHint(display, window, ref hint);
                    XFlush(display);
                }
                finally
                {
                    Marshal.FreeHGlobal(name);
                    XCloseDisplay(display);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }
    }
}
