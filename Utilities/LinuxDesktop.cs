using System;
using System.Reflection;
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

        /// <summary>
        /// Mono does not set WM_CLASS on its windows, so GNOME and other docks cannot match the
        /// window to its launcher and show it as an unknown app. Set it on the top level window.
        /// </summary>
        public static void SetWMClass(Form form)
        {
            if (!Program.MONO || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
