//extern alias MPLib;

using MissionPlanner;
using MissionPlanner.Utilities;
using Newtonsoft.Json;
using SkiaSharp;
using SkiaSharp.Views.Forms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Acr.UserDialogs.Infrastructure;
using Microsoft.Scripting.Utils;
using MissionPlanner.Comms;
using MissionPlanner.GCSViews;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;
using Xamarin.Essentials;
using Application = System.Windows.Forms.Application;
using Device = Xamarin.Forms.Device;
using Extensions = MissionPlanner.Utilities.Extensions;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using MissionPlanner.Controls;
using System.Globalization;
using log4net;
using System.Text.RegularExpressions;
using Size = System.Drawing.Size;

namespace Xamarin.GCSViews
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class WinForms : ContentPage
    {
        readonly string TAG = "MP";

        static WinForms Instance;

        public WinForms()
        {
            InitializeComponent();

            size = Device.Info.ScaledScreenSize;
            Console.WriteLine("ScaledScreenSize " + size);
            size = Device.Info.PixelScreenSize;
            Console.WriteLine("PixelScreenSize " + size);

            Xamarin.Forms.Platform.WinForms.Forms.UIThread = Thread.CurrentThread.ManagedThreadId;

            var scale = size.Width / size.Height; // 1.77 1.6  1.33

            if (scale < 1)
            {
                size = new Forms.Size(960, 960 * scale);
            }
            else
            {
                size = new Forms.Size(540 * scale, 540);
                if (size.Width < 960)
                    size = new Forms.Size(960, 960 / scale);
            }

            if (Device.RuntimePlatform == Device.macOS || Device.RuntimePlatform == Device.UWP)
            {
                size = Device.Info.PixelScreenSize;
                // scale if higher than full hd
                if (size.Width > 1920)
                {
                    size.Width /= 2;
                    size.Height /= 2;

                }
            }

            Console.WriteLine("Final Size " + size);

            Instance = this;
            try
            {
                if (Test.Speech != null)
                    MainV2.speechEngine = Test.Speech;
                else
                    MainV2.speechEngine = new Speech();
            } catch{}

            RestoreFiles();

            FileDialog.CustomDirectory = Settings.GetUserDataDirectory();

            // init seril port type
            SerialPort.DefaultType = (self, s, i) =>
            {
                return Task.Run(async () =>
                {
                    Log.Info(TAG, "SerialPort.DefaultType in " + s + " " + i);

                    // no valid portname to start
                    if (String.IsNullOrEmpty(s))
                    {
                        Log.Info(TAG, "SerialPort.DefaultType passthrough s = null");
                        return self._baseport;
                    }
                    else
                    {
                        if (s.StartsWith("BT_") || s.StartsWith("BLE_"))
                        {
                            var bt = await Test.BlueToothDevice.GetDeviceInfoList();

                            var di = bt.Where(a => a.board == s);

                            if (di.Count() > 0)
                            {
                                Log.Info(TAG, "SerialPort.DefaultType found device " + di.First().board + " search " + s);
                                return await Test.BlueToothDevice.GetBT(di.First());
                            }
                        }

                        if (s.StartsWith("GPS"))
                        {
                            var com = new CommsInjection();
                            Task.Run(async () => {
                                while (true)
                                {
                                    var (lat, lng, alt) = await Test.GPS.GetPosition();
                                    var latdms = (int)lat + (lat - (int)lat) * .6f;
                                    var lngdms = (int)lng + (lng - (int)lng) * .6f;

                                    var line = string.Format(CultureInfo.InvariantCulture,
                                        "$GP{0},{1:HHmmss.ff},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}", "GGA",
                                        DateTime.Now.ToUniversalTime(),
                                        Math.Abs(latdms * 100).ToString("0000.00", CultureInfo.InvariantCulture), lat < 0 ? "S" : "N",
                                        Math.Abs(lngdms * 100).ToString("00000.00", CultureInfo.InvariantCulture), lng < 0 ? "W" : "E",
                                        1, 10,
                                        1, alt.ToString("0.00", CultureInfo.InvariantCulture), "M", 0, "M", "0.0", "0");

                                    var checksum = GetChecksum(line);
                                    com.AppendBuffer(ASCIIEncoding.ASCII.GetBytes(line + "*" + checksum + "\r\n"));

                                    await Task.Delay(200);
                                }
                            });                           

                            return com;
                        }

                        {
                            var dil = await Test.UsbDevices.GetDeviceInfoList();

                            var di = dil.Where(a => a.board == s);

                            if (di.Count() > 0)
                            {
                                Log.Info(TAG,
                                    "SerialPort.DefaultType found device " + di.First().board + " search " + s);
                                return await Test.UsbDevices.GetUSB(di.First());
                            }
                        }

                        if (Device.RuntimePlatform == Device.macOS || s != null && File.Exists(s))
                        {
                            Log.Info(TAG, "SerialPort.DefaultType in " + s + " " + i + " for " + Device.RuntimePlatform);
                            if (s != null && i > 0)
                                return new MonoSerialPort(s, i);
                            if(s!= null)
                                return new MonoSerialPort(s);
                            return new MonoSerialPort();
                        }
                    }

                    Log.Info(TAG, "SerialPort.DefaultType passthrough no board match");
                    return self._baseport;
                }).Result;
            };

            // report back device list
            SerialPort.GetCustomPorts = () =>
            {
                var list1 = Task.Run(async () =>
                {
                    var list = await Test.BlueToothDevice.GetDeviceInfoList();
                    return list.Select(a => a.board).ToList();
                }).Result;

                var list2 = Task.Run(async () =>
                {
                    var list = await Test.UsbDevices.GetDeviceInfoList();
                    return list.Select(a => a.board).ToList();
                }).Result;

                list1.AddRange(list2);
                if (Device.RuntimePlatform == Device.Android)
                    list1.Add("GPS");

                return list1;
            };

            if (Device.RuntimePlatform == Device.macOS || Device.RuntimePlatform == Device.Android)
            {
                // support for fw upload
                MissionPlanner.GCSViews.ConfigurationView.ConfigFirmwareManifest.ExtraDeviceInfo += () =>
                {
                    return Task.Run(async () => { return await Test.UsbDevices.GetDeviceInfoList(); }).Result;
                };

                MissionPlanner.GCSViews.ConfigurationView.ConfigFirmware.ExtraDeviceInfo += () =>
                {
                    return Task.Run(async () => { return await Test.UsbDevices.GetDeviceInfoList(); }).Result;
                };
            }
        }

        // Calculates the checksum for a sentence
        private string GetChecksum(string sentence)
        {
            // Loop through all chars to get a checksum
            var Checksum = 0;
            foreach (var Character in sentence)
                switch (Character)
                {
                    case '$':
                        // Ignore the dollar sign
                        break;

                    case '*':
                        // Stop processing before the asterisk
                        continue;
                    default:
                        // Is this the first value for the checksum?
                        if (Checksum == 0)
                            Checksum = Convert.ToByte(Character);
                        else
                            Checksum = Checksum ^ Convert.ToByte(Character);
                        break;
                }
            // Return the checksum formatted as a two-character hexadecimal
            return Checksum.ToString("X2");
        }
        public static void SetHUDbg(byte[] buffer)
        {
            try
            {
                MissionPlanner.GCSViews.FlightData.myhud.bgimage = Bitmap.FromStream(new MemoryStream(buffer));
            }
            catch (Exception ex)
            {
                Log.Error("MP", ex.ToString());
            }
        }

        private void RestoreFiles()
        {
            try
            {
                // nofly dir
                Directory.CreateDirectory(Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "NoFly");

                // restore assets
                Directory.CreateDirectory(Settings.GetUserDataDirectory());

                File.WriteAllText(Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "airports.csv",
                    files.airports);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "BurntKermit.mpsystheme",
                    files.BurntKermit);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "HighContrast.mpsystheme",
                    files.HighContrast);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "ParameterMetaData.xml",
                    files.ParameterMetaDataBackup);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "camerasBuiltin.xml",
                    files.camerasBuiltin);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "checklistDefault.xml",
                    files.checklistDefault);

                File.WriteAllText(
                    Settings.GetUserDataDirectory() + Path.DirectorySeparatorChar + "mavcmd.xml", 
                    files.mavcmd);

                /*
                try {
                        var pluginsdir = Settings.GetRunningDirectory() + "plugins";
                        Directory.CreateDirectory(pluginsdir);

                        string[] files = new[]
                        {
                            "example10_canlogfile", "example11_trace", "example3_fencedist", "example4_herelink",
                            "example5_latencytracker", "example6_mapicondesc", "example7_canrtcm", "example8_modechange",
                            "example9_hudonoff", "generator"
                        };

                        foreach (var file in files)
                        {
                            try
                            {
                                var id = (int) typeof(MissionPlanner.files)
                                    .GetProperty(file)
                                    .GetValue(null);

                                var filename = pluginsdir + Path.DirectorySeparatorChar + file + ".cs";

                                if (File.Exists(filename))
                                {
                                    File.Delete(filename);
                                }


                                File.WriteAllText(filename, MissionPlanner.files.ResourceManager.GetString(file));

                            }
                            catch
                            {

                            }
                        }
                    } catch { }
                    */
                    try {
                        var graphsdir = Settings.GetRunningDirectory() + "graphs";
                        Directory.CreateDirectory(graphsdir);

                        string[] files1 = new[]
                        {
                            "ekf3Graphs", "ekfGraphs", "mavgraphs", "mavgraphs2", "mavgraphsMP"
                        };

                        foreach (var file in files1)
                        {
                            try
                            {
                                var id = typeof(MissionPlanner.files)
                                    .GetProperty(file)
                                    .GetValue(null);

                                File.WriteAllText(
                                    graphsdir + Path.DirectorySeparatorChar + file + ".xml",
                                    files.ResourceManager.GetString(file));
                            }
                            catch
                            {

                            }
                        }
                    } catch { }
            }
            catch (Exception ex)
            {
                DisplayAlert(Strings.ERROR, "Failed to stage files " + ex.ToString(), "OK");
            }
        }

        public static string BundledPath
        {
            get { return SITL.BundledPath; }
            set { SITL.BundledPath = value; }
        }

        public static bool Android
        {
            get { return MainV2.Android; }
            set { MainV2.Android = value; }
        }
        public static bool IOS
        {
            get { return MainV2.IOS; }
            set { MainV2.IOS = value; }
        }
        public static bool OSX
        {
            get { return MainV2.OSX; }
            set { MainV2.OSX = value; }
        }

        public static Action InitDevice
        {
            get => _initDevice;
            set => _initDevice = value;
        }

        public static void Exit()
        {
            Application.Exit();
        }

        public static void Resize(int width, int height)
        {
            Instance.size = new Forms.Size(width, height);
            Screen.PrimaryScreen.Bounds = new Rectangle(0, 0, width, height);
            Screen.PrimaryScreen.WorkingArea = new Rectangle(0, 0, width, height);
            var pos = new XplatUIMine.tagWINDOWPOS() {cx = width, cy = height + XplatUIMine.GetInstance().CaptionHeight, flags = 0x2, x = 0, y = 0};
            int size = Marshal.SizeOf(typeof(XplatUIMine.tagWINDOWPOS));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(pos, ptr, true);
            XplatUIMine.GetInstance().SendMessage(IntPtr.Zero, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, ptr);
            Marshal.FreeHGlobal(ptr);
            //.SetWindowPos(IntPtr.Zero, 0, 0, width, height);
        }
        

        protected override void OnAppearing()
        {
            if (!start)
            {
                StartThreads();

                XplatUIMine.GetInstance().Keyboard = new Keyboard(Entry);
                start = true;
            }

            SkCanvasView.InvalidateSurface();

            Activate();

            base.OnAppearing();
        }
        public class Keyboard: KeyboardXplat
        {
            private readonly Forms.Entry _inputView;

            private Entry view;
            private IntPtr _focusWindow;

            public Keyboard(Entry inputView)
            {
                _inputView = inputView;
            }

            public void FocusIn(IntPtr focusWindow)
            {
                FocusOut(_focusWindow);
                caretptr = IntPtr.Zero;
                _focusWindow = focusWindow;

                var ctl = Control.FromHandle(_focusWindow);
                var nw = NativeWindow.FromHandle(_focusWindow);

                Console.WriteLine("FocusIn name {0} type {1} nw {2}", ctl?.Name,ctl?.GetType(), nw?.Handle);
            }

            private void View_TextChanged(object sender, TextChangedEventArgs e)
            {
                var current = Control.FromHandle(_focusWindow).Text;

                Console.WriteLine("TextChanged {0} {1} {2}", current, e.OldTextValue, e.NewTextValue);
            }

            public void FocusOut(IntPtr focusWindow)
            {
                _inputView.TextChanged -= View_TextChanged;
            }

            private IntPtr caretptr;
            public void SetCaretPos(CaretStruct caret, IntPtr handle, int x, int y)
            {
                var ctl = Control.FromHandle(_focusWindow);
                var nw = NativeWindow.FromHandle(_focusWindow);

                var caretl = caret;

                //if (_focusWindow == handle && caret.Hwnd == _focusWindow)                                
                //Device.BeginInvokeOnMainThread(() =>
                _inputView.Dispatcher.BeginInvokeOnMainThread(()=>
                    {
                        if(caretptr == handle)
                            return;

                        var focusctl = Control.FromHandle(_focusWindow);
                        if (focusctl == null)
                            return;
                        var p = focusctl.PointToClient(Form.MousePosition);

                        var handlectl = Control.FromHandle(handle);
                        var p2 = handlectl.PointToClient(Form.MousePosition);

                        if(focusctl is ComboBox)
                        {
                            var cb = (ComboBox)focusctl;
                            if(cb.DropDownStyle == ComboBoxStyle.DropDownList)
                            {
                                return;
                            }
                        }

                        if (handlectl.ClientRectangle.Contains(p))
                        {
                            // unbind
                            _inputView.Unfocused -= _inputView_Unfocused;                            
                            _inputView.TextChanged -= View_TextChanged;
                            _inputView.Completed -= _inputView_Completed;
                            // set                  

                            _inputView.Text = focusctl.Text;
                            // rebind
                            _inputView.Completed += _inputView_Completed;
                            _inputView.TextChanged += View_TextChanged;
                            _inputView.Unfocused += _inputView_Unfocused;
                            //show
                            _inputView.IsVisible = true;
                            _inputView.Focus();

                            caretptr = handle;
                        }                      
                    });
            }

            private void _inputView_Completed(object sender, EventArgs e)
            {
                Console.WriteLine("_inputView_Completed");
                var focusctl = Control.FromHandle(_focusWindow);
                var text = (sender as Entry)?.Text;
                focusctl.BeginInvokeIfRequired(()=>{ 
                    focusctl.Text = text;
                });
                _inputView.Dispatcher.BeginInvokeOnMainThread(() =>
                    {
                _inputView.IsVisible = false; });
            }

            private void _inputView_Unfocused(object sender, FocusEventArgs e)
            {
                Console.WriteLine("_inputView_Unfocused");
                if(Device.RuntimePlatform == Device.macOS)
                {
                    // osx only accepts the enter key - which in testing doesnt work
                    _inputView_Completed(sender, new EventArgs());
                }
                caretptr = IntPtr.Zero;                
                _inputView.Dispatcher.BeginInvokeOnMainThread(() =>
                    {
                _inputView.IsVisible = false; });
            }
        }

        protected override bool OnBackButtonPressed()
        {
            if (Application.OpenForms.Count > 1)
            {
                Application.OpenForms[Application.OpenForms.Count - 1].Close();
                XplatUIMine.PaintPending = true;
                return true;
            }
            else if (Application.OpenForms.Count == 1)
            {
                MainPage.Instance.DisplayAlert("", "Exit?", "Yes", "No").ContinueWith((result) =>
                {
                    if (result.Result)
                    {
                        // ensure we run on the right thread
                        Application.Idle += (sender, args) =>
                        {
                            Application.Exit();
                            
                        };
                        XplatUIMine.PaintPending = true;
                    }
                });

                return true;
            }

            return false;
        }

        protected override void OnDisappearing()
        {
            Deactivate();
            base.OnDisappearing();
        }

        private void StartThreads()
        {
            Screen.PrimaryScreen.Bounds = new Rectangle(0, 0, (int) size.Width, (int) size.Height);
            Screen.PrimaryScreen.WorkingArea = new Rectangle(0, 0, (int) size.Width, (int) size.Height);

            winforms = new Thread(() =>
            {
                var init = true;

                Application.Idle += (sender, args) =>
                {
                    if (MainV2.instance != null && MainV2.instance.IsHandleCreated)
                    {
                        if (init)
                        {
                            Device.BeginInvokeOnMainThread(() => { InitDevice?.Invoke(); });
                            init = false;
                        }
                    }

                    Thread.Yield();
                };

                MissionPlanner.Program.Main(new string[0]);
                
                System.Diagnostics.Process.GetCurrentProcess().CloseMainWindow();
            });
            winforms.Start();

            Forms.Device.StartTimer(TimeSpan.FromMilliseconds(1000/30), () =>
            {
                Monitor.Enter(XplatUIMine.paintlock);
                if (XplatUIMine.PaintPending)
                {
                    if (Instance.SkCanvasView != null)
                    {
                        Instance.scale = new Forms.Size((Instance.SkCanvasView.CanvasSize.Width / Instance.size.Width),
                            (Instance.SkCanvasView.CanvasSize.Height / Instance.size.Height));

                        Screen.PrimaryScreen.WorkingArea =
                            new Rectangle(0, 0, (int) Instance.size.Width, (int) Instance.size.Height);
                        Screen.PrimaryScreen.Bounds =
                            new Rectangle(0, 0, (int) Instance.size.Width, (int) Instance.size.Height);

                        Device.BeginInvokeOnMainThread(() => { Instance.SkCanvasView.InvalidateSurface(); });
                        XplatUIMine.PaintPending = false;
                    }
                }
                Monitor.Exit(XplatUIMine.paintlock);

                return true;
            });
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine(e.ExceptionObject);
        }

        float Magnitude(SKPoint point)
        {
            return (float)Math.Sqrt(Math.Pow(point.X, 2) + Math.Pow(point.Y, 2));
        }

        // pixels per dp
        private float Density
        {
            get
            {
                if (density == 0)
                {
                    var value = 1.0;
                    try
                    {
                        value = DeviceDisplay.MainDisplayInfo.Density;
                    }
                    catch
                    {
                    }

                    density = (float) Math.Max(value, 1);
                }

                return density;
            }
        }

        // Android's touch slop is 8dp, movement below this is finger jitter not a drag
        private float TouchSlop => 8 * Density;

        // Android's double tap slop is 100dp, a second tap this close to the first is a double click
        private float DoubleTapSlop => 100 * Density;

        // Android's long press timeout
        private const int LongPressMs = 500;

        // a finger that is down keeps reporting moves (position, pressure and size jitter),
        // one that has been silent this long has lost its release
        private const int StaleTouchMs = 3000;

        //Double-clicking the left mouse button actually generates a sequence of four messages: WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, and WM_LBUTTONUP.
        DateTime lastClick = DateTime.MinValue;
        SKPoint lastClickLocation;

        private Forms.Size size;
        private Forms.Size scale;
        private Dictionary<long, TouchInfo> touchDictionary = new Dictionary<long, TouchInfo>(10);
        // more than one finger is down, nothing is a click until they are all up
        private bool gesture;
        // finger spread at the last zoom step, 0 until the pinch has a baseline
        private float pinchDistance;
        private float density;
        static bool start;

        // hit testing walks the winforms control tree, so it has to run on the winforms thread
        private void SendMouse(Msg msg, IntPtr wParam, SKPoint location)
        {
            var x = (int) (location.X / scale.Width);
            var y = (int) (location.Y / scale.Height);
            var lParam = (IntPtr) ((y) << 16 | (x));

            Control target = MainV2.instance;
            if (target == null || !target.IsHandleCreated)
                target = Application.OpenForms.Cast<Form>().LastOrDefault(a => a.IsHandleCreated);
            if (target == null)
                return;

            target.BeginInvoke((Action) (() =>
            {
                try
                {
                    XplatUI.driver.SendMessage(IntPtr.Zero, msg, wParam, lParam);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }));
        }

        private void SendPress(TouchInfo info)
        {
            info.pressSent = true;
            var location = info.atdown.Location;

            SendMouse(Msg.WM_MOUSEMOVE, IntPtr.Zero, location);

            if (lastClick.AddMilliseconds(500) > info.DownTime &&
                Magnitude(location - lastClickLocation) <= DoubleTapSlop)
            {
                info.wasdouble = true;
                SendMouse(Msg.WM_LBUTTONDBLCLK, (IntPtr) MsgButtons.MK_LBUTTON, location);
            }
            else
            {
                SendMouse(Msg.WM_LBUTTONDOWN, (IntPtr) MsgButtons.MK_LBUTTON, location);
            }
        }

        // a drag in progress ends where it is, so the control is not left captured
        private void EndDrag(TouchInfo info)
        {
            if (info.pressSent && !info.wasright && !gesture)
                SendMouse(Msg.WM_LBUTTONUP, IntPtr.Zero, info.now.Location);
        }

        private void ResetTouches()
        {
            foreach (var info in touchDictionary.Values)
                EndDrag(info);

            touchDictionary.Clear();
            gesture = false;
        }

        private void StartGesture()
        {
            foreach (var info in touchDictionary.Values)
                EndDrag(info);

            gesture = true;
            lastClick = DateTime.MinValue;
        }

        private void Pinch()
        {
            if (touchDictionary.Count < 2)
                return;

            // the spread of the two fingers as they are now, either or both may be moving
            var fingers = touchDictionary.Values.Take(2).ToArray();
            var a = fingers[0].now.Location;
            var b = fingers[1].now.Location;
            var distance = Magnitude(a - b);
            var center = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);

            if (pinchDistance < TouchSlop)
            {
                pinchDistance = distance;
                return;
            }

            // a wheel step is a whole map zoom level, which is twice the scale
            var ratio = distance / pinchDistance;
            if (ratio >= 2 || ratio <= 0.5)
            {
                // the wheel goes to the control under the mouse, so put the mouse on the pinch
                SendMouse(Msg.WM_MOUSEMOVE, IntPtr.Zero, center);
                SendMouse(Msg.WM_MOUSEWHEEL, new IntPtr((ratio >= 2 ? 1 : -1) << 16), center);
                pinchDistance = distance;
            }
        }

        private void SkCanvasView_Touch(object sender, SkiaSharp.Views.Forms.SKTouchEventArgs e)
        {
            e.Handled = true;

            // the paint timer is what sets the scale, so ask for a paint even if this touch is dropped
            XplatUIMine.PaintPending = true;

            // not painted yet, so there is no mapping to winforms coords
            if (scale.Width == 0 || scale.Height == 0)
                return;

            try
            {
                // a finger is held back until it is a tap, drag or long press. a mouse goes straight through
                var deferred = e.DeviceType != SKTouchDeviceType.Mouse && Device.RuntimePlatform != Device.macOS;

                if (e.ActionType == SKTouchAction.Moved)
                {
                    if (!touchDictionary.TryGetValue(e.Id, out var info))
                    {
                        // hover
                        if (!e.InContact)
                            SendMouse(Msg.WM_MOUSEMOVE, IntPtr.Zero, e.Location);
                        return;
                    }

                    info.prev = info.now;
                    info.now = e;
                    info.lastSeen = DateTime.Now;

                    if (gesture)
                    {
                        Pinch();
                        return;
                    }

                    if (info.wasright)
                        return;

                    if (!info.pressSent)
                    {
                        if (Magnitude(e.Location - info.atdown.Location) <= TouchSlop)
                            return;

                        // start of a drag, press where the finger went down
                        SendPress(info);
                    }

                    SendMouse(Msg.WM_MOUSEMOVE, (IntPtr) MsgButtons.MK_LBUTTON, e.Location);
                }

                if (e.ActionType == SKTouchAction.Pressed && e.MouseButton == SKMouseButton.Left)
                {
                    var now = DateTime.Now;

                    // a lost release would leave its finger here and turn every later touch into a gesture
                    foreach (var stale in touchDictionary
                                 .Where(a => a.Value.lastSeen.AddMilliseconds(StaleTouchMs) < now)
                                 .ToList())
                    {
                        EndDrag(stale.Value);
                        touchDictionary.Remove(stale.Key);
                    }

                    if (touchDictionary.Count == 0)
                        gesture = false;

                    var info = new TouchInfo() {now = e, prev = e, DownTime = now, lastSeen = now, atdown = e};
                    touchDictionary[e.Id] = info;

                    if (touchDictionary.Count > 1)
                    {
                        if (!gesture)
                            StartGesture();
                        // the fingers changed, so the pinch starts again from here
                        pinchDistance = 0;
                        return;
                    }

                    if (!deferred)
                    {
                        SendPress(info);
                        return;
                    }

                    // right click handler
                    Device.StartTimer(TimeSpan.FromMilliseconds(LongPressMs), () =>
                    {
                        if (!gesture && touchDictionary.Count == 1 &&
                            touchDictionary.TryGetValue(e.Id, out var held) && held == info && !info.pressSent)
                        {
                            info.wasright = true;
                            var location = info.atdown.Location;
                            SendMouse(Msg.WM_MOUSEMOVE, IntPtr.Zero, location);
                            SendMouse(Msg.WM_RBUTTONDOWN, (IntPtr) MsgButtons.MK_RBUTTON, location);
                            SendMouse(Msg.WM_RBUTTONUP, IntPtr.Zero, location);
                        }

                        return false;
                    });
                }

                if (e.ActionType == SKTouchAction.Released && e.MouseButton == SKMouseButton.Left)
                {
                    if (!touchDictionary.TryGetValue(e.Id, out var info))
                        return;

                    touchDictionary.Remove(e.Id);

                    if (gesture)
                    {
                        if (touchDictionary.Count == 0)
                            gesture = false;
                        pinchDistance = 0;
                        return;
                    }

                    if (info.wasright)
                    {
                        lastClick = DateTime.MinValue;
                        return;
                    }

                    if (info.pressSent)
                    {
                        SendMouse(Msg.WM_LBUTTONUP, IntPtr.Zero, e.Location);
                    }
                    else
                    {
                        // a tap
                        SendPress(info);
                        SendMouse(Msg.WM_LBUTTONUP, IntPtr.Zero, info.atdown.Location);
                    }

                    // only a press and release in place can start a double click
                    if (!info.wasdouble && Magnitude(e.Location - info.atdown.Location) <= TouchSlop)
                    {
                        lastClick = DateTime.Now;
                        lastClickLocation = info.atdown.Location;
                    }
                    else
                    {
                        lastClick = DateTime.MinValue;
                    }
                }

                if (e.ActionType == SKTouchAction.Pressed && e.MouseButton == SKMouseButton.Right)
                {
                    ResetTouches();
                    SendMouse(Msg.WM_RBUTTONDOWN, (IntPtr) MsgButtons.MK_RBUTTON, e.Location);
                }

                if (e.ActionType == SKTouchAction.Released && e.MouseButton == SKMouseButton.Right)
                {
                    ResetTouches();
                    SendMouse(Msg.WM_RBUTTONUP, IntPtr.Zero, e.Location);
                }

                // the pointer left the view, or the system took the touch
                if (e.ActionType == SKTouchAction.Entered || e.ActionType == SKTouchAction.Exited ||
                    e.ActionType == SKTouchAction.Cancelled)
                {
                    ResetTouches();
                    if (e.ActionType != SKTouchAction.Cancelled)
                        SendMouse(Msg.WM_MOUSEMOVE, IntPtr.Zero, e.Location);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private void SkCanvasView_PaintSurface(object sender, SkiaSharp.Views.Forms.SKPaintSurfaceEventArgs e)
        {
            SkCanvasView_PaintSurface(sender, new SKPaintGLSurfaceEventArgs(e.Surface, null));
        }

        private void SkCanvasView_PaintSurface(object sender, SkiaSharp.Views.Forms.SKPaintGLSurfaceEventArgs e) // .SKPaintSurfaceEventArgs // SKPaintGLSurfaceEventArgs
        {
            try
            {

                var canvas = e.Surface.Canvas;

                canvas.Clear(SKColors.Gray);

                canvas.Scale((float) scale.Width, (float) scale.Height);

                foreach (Form form in Application.OpenForms.Select(a=>a).ToArray())
                {
                    if (form.IsHandleCreated)
                    {
                        if (form is MainV2 && form.WindowState != FormWindowState.Maximized)
                            form.BeginInvokeIfRequired(() => { form.WindowState = FormWindowState.Maximized; });

                        try
                        {
                            // skgl is not keeping the previous render. so true.
                            FormsRender.DrawOntoCanvas(form.Handle, canvas, true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    }
                }

                IEnumerable<Hwnd> menu;
                lock(Hwnd.windows)
                    menu = Hwnd.windows.Values.OfType<Hwnd>()
                    .Where(hw => hw.topmost && hw.Mapped && hw.Visible).ToArray();
                foreach (Hwnd hw in menu)
                {
                    var ctlmenu = Control.FromHandle(hw.ClientWindow);
                        if (ctlmenu != null)
                            FormsRender.DrawOntoCanvas(hw.ClientWindow, canvas, true);
                }

                if (Device.RuntimePlatform != Device.macOS && Device.RuntimePlatform != Device.UWP)
                {
                    canvas.ClipRect(
                        SKRect.Create(0, 0, Screen.PrimaryScreen.Bounds.Width,
                            Screen.PrimaryScreen.Bounds.Height), (SKClipOperation) 5);

                    var path = new SKPath();
            
                    path.MoveTo(cursorPoints.First());
                    cursorPoints.ForEach(a => path.LineTo(a));
                    path.Transform(new SKMatrix(1, 0, XplatUI.driver.MousePosition.X, 0, 1,
                        XplatUI.driver.MousePosition.Y, 0, 0, 1));

                    canvas.DrawPath(path,
                        new SKPaint()
                            {Color = SKColors.White, Style = SKPaintStyle.Fill, StrokeJoin = SKStrokeJoin.Miter});
                    canvas.DrawPath(path,
                        new SKPaint()
                            {Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Miter, IsAntialias = true});
                }

                canvas.DrawText("" + DateTime.Now.ToString("HH:mm:ss.fff"),
                    new SKPoint(10, 10), new SKPaint() {Color = SKColor.Parse("ffff00")});

                canvas.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
               
            }
        }

        private SKPoint[] cursorPoints = new SKPoint[]
        {
            new SKPoint(0f,0f),
            new SKPoint(0f,16.512804f),
            new SKPoint(4.205124f,12.717936f),
            new SKPoint(7.589736f,19.99998f),
            new SKPoint(9.641016f,19.076904f),
            new SKPoint(6.256404f,11.79486f),
            new SKPoint(12.102552f,11.179476f),
            new SKPoint(0f,0f),
        };

        static private Thread winforms;
        private static Action _initDevice;

        public void Activate()
        {
            Test.UsbDevices.USBEvent += DeviceAttached;
        }

#pragma warning disable AsyncFixer03 // Fire-and-forget async-void methods or delegates
        private async void DeviceAttached(object sender, MissionPlanner.ArduPilot.DeviceInfo e)
#pragma warning restore AsyncFixer03 // Fire-and-forget async-void methods or delegates
        {
            ICommsSerial portUsb = null;
            try
            {
                portUsb = await Test.UsbDevices.GetUSB(e).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
            }

            if (portUsb == null)
                return;

            if (MainV2.comPort.BaseStream.IsOpen)
                return;

            try
            {
                // send hook
                const int DBT_DEVTYP_PORT = 0x00000003;

                var prt = new MainV2.DEV_BROADCAST_PORT();
                prt.dbcp_devicetype = DBT_DEVTYP_PORT;
                prt.dbcp_name = e.board;
                prt.dbcp_size = prt.dbcp_name.Length * 2 + 4 * 3;

                IntPtr tosend;
                tosend = Marshal.AllocHGlobal(Marshal.SizeOf(prt));
                Marshal.StructureToPtr(prt, tosend, true);

                XplatUI.driver.SendMessage(IntPtr.Zero, Msg.WM_DEVICECHANGE,
                    (IntPtr) MainV2.WM_DEVICECHANGE_enum.DBT_DEVICEARRIVAL, tosend);
            }
            catch
            {

            }

            // autoconnect
            if (!e.board.ToLower().Contains("-bl") && !e.board.ToLower().Contains("-P2"))
            {
                try
                {
                    var ans = await DisplayAlert("Connect", "Connect to USB Device? " + e.board, "Yes", "No").ConfigureAwait(false);
                    if (ans)
                    {
                        MainV2.comPort.BaseStream = portUsb;
                        MainV2.instance.BeginInvoke((Action) delegate()
                        {
                            MainV2.instance.doConnect(MainV2.comPort, "preset", "0");
                        });
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                }
            }
        }

        public void Deactivate()
        {
            Test.UsbDevices.USBEvent -= DeviceAttached;
        }
    }

    public class TouchInfo
    {
        public DateTime DownTime = DateTime.MinValue;
        public SKTouchEventArgs atdown;
        public SKTouchEventArgs prev;
        public SKTouchEventArgs now;
        // the last event for this finger, a finger that goes quiet for too long lost its release
        public DateTime lastSeen;
        // the mouse down has been sent, either a drag started or this is a mouse
        public bool pressSent = false;
        public bool wasdouble = false;
        public bool wasright = false;
    }

    public class Speech : ISpeech
    {
        DateTime lastmsg = DateTime.MinValue;
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool speechEnable { get; set; }

        public Speech()
        {

        }

        public bool IsReady
        {
            get { if (lastmsg.AddSeconds(5) < DateTime.Now) return true;  return !isBusy; }
        }

        CancellationTokenSource cts;
        bool isBusy = false;

        public void SpeakAsync(string text)
        {
            if (!MainV2.speechEnabled())
                return;

            if (text == null || String.IsNullOrWhiteSpace(text))
                return;

            text = Regex.Replace(text, @"\bPreArm\b", "Pre Arm", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bdist\b", "distance", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bNAV\b", "Navigation", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)m\b", "$1 meters", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)ft\b", "$1 feet", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)\bbaud\b", "$1 baudrate", RegexOptions.IgnoreCase);

            cts = new CancellationTokenSource();
            lastmsg = DateTime.Now;
            isBusy = true;
            log.Info("TTS: say " + text);
            _ = Task.Run(async () =>
              {
                  try
                  {
                    var locales = await TextToSpeech.GetLocalesAsync();

                    // Grab the first locale
                    var locale = locales.FirstOrDefault();

                      var settings = new SpeechOptions()
                      {
                          Volume = 1.0f,
                          Pitch = 1.0f,
                          //Locale = locale
                      };

                      await TextToSpeech.SpeakAsync(text, settings, cts.Token).ConfigureAwait(false);
                  }
                  catch (Exception e)
                  {
                  }
                  finally
                  {
                      isBusy = false;
                  }
              });
        }

        public void SpeakAsyncCancelAll()
        {
            if (cts?.IsCancellationRequested ?? true)
                return;

            cts.Cancel();

            isBusy = false;
        }
    }

    public class Browser : IBrowserOpen
    {
        public bool OpenURL(Uri uri)
        {
            Xamarin.Essentials.Browser.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
            return true;
        }
    }
}