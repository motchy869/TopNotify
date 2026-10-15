using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Threading.Tasks;
using TopNotify.Common;
using SamsidParty_TopNotify.Daemon;
using Windows.UI.Notifications.Management;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;
using static TopNotify.Daemon.ResolutionFinder;

namespace TopNotify.Daemon
{

    public class NativeInterceptor : Interceptor
    {
        #region WinAPI Methods

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr hWndChildAfter, string className, string windowTitle);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        public static extern IntPtr SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int Y, int cx, int cy, int wFlags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, ref Rectangle rectangle);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        const UInt32 WM_CLOSE = 0x0010;
        const short SWP_NOMOVE = 0X2;
        const short SWP_NOSIZE = 1;
        const short SWP_NOZORDER = 0X4;
        const int SWP_SHOWWINDOW = 0x0040;

        [DllImport("TopNotify.Native")]
        private static extern bool TopNotifyEnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        #endregion

        public IntPtr hwnd;
        public ExtendedStyleManager ExStyleManager;
        public int ScaledPreferredDisplayWidth;
        public int ScaledPreferredDisplayHeight;
        public int RealPreferredDisplayWidth;
        public int RealPreferredDisplayHeight;
        public float ScaleFactor;

        /// <summary>Tolerance (in pixels) used when checking whether the notification window is already in place</summary>
        const int PositionTolerance = 2;

        /// <summary>
        /// How Often (In Milliseconds) The Expensive Fallback Detection May Run While Nothing Is Being Tracked.
        /// New Notifications Force A Reflow, So This Doesn't Delay The Notification From Being Moved.
        /// </summary>
        const int FallbackDetectionInterval = 2000;

        // Log Gates, So An Idle Daemon Doesn't Write The Same Message To The Log Twice A Second
        bool hasLoggedForcedFallback = false;
        bool hasLoggedFallbackDetection = false;
        bool hasLoggedMissingWindow = false;

        DateTime lastFallbackDetection = DateTime.MinValue;

        /// <summary>
        /// Only Poll Fast While A Notification Window Is Actually Visible,
        /// So The Main Loop Can Back Off While Nothing Is Happening
        /// </summary>
        public override bool NeedsFrequentUpdate
        {
            get { return hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd); }
        }

        /// <summary>
        /// The Fallback Detection Has To Enumerate Every Top Level Window, So It Is Skipped While Idle
        /// </summary>
        bool ShouldRunFallbackDetection()
        {
            if (hwnd != IntPtr.Zero) { return true; } // A Window Is Tracked, Keep Its Handle Fresh

            // A Notification Just Arrived, So It Is Worth Looking For Its Window Right Away
            if (InterceptorManager.Instance != null && InterceptorManager.Instance.IsForcedReflow) { return true; }

            return (DateTime.UtcNow - lastFallbackDetection).TotalMilliseconds >= FallbackDetectionInterval;
        }

        public override void Start()
        {
            base.Start();
            ExStyleManager = new ExtendedStyleManager(new IntPtr(0x00200008)); // Magic Number, Default Notification Style
            Reflow();
        }

        public override void Restart()
        {
            base.Restart();
        }

        // Modified From https://stackoverflow.com/a/20276701/18071273
        public static IEnumerable<IntPtr> FindCoreWindows()
        {
            IntPtr found = IntPtr.Zero;
            List<IntPtr> windows = new List<IntPtr>();

            // Reused For Every Window, Because This Enumerates The Whole Desktop
            // (Class Names Can't Be Longer Than 256 Characters)
            var classGet = new StringBuilder(256);

            TopNotifyEnumWindows(delegate (IntPtr hwnd, IntPtr param)
            {
                classGet.Clear();
                GetClassName(hwnd, classGet, classGet.Capacity);
                if (classGet.ToString() == "Windows.UI.Core.CoreWindow")
                {
                    windows.Add(hwnd);
                }

                return true;
            }, IntPtr.Zero);

            return windows;
        }

        public override void Reflow()
        {
            if (ExStyleManager == null) { return; } // Return If Start() Has Not Been Called Yet

            base.Reflow();

            try
            {
                // Forget The Handle If The Notification Window Is Gone,
                // Otherwise Win32 Would Be Called On A Dead Handle On Every Single Tick
                if (hwnd != IntPtr.Zero && !IsWindow(hwnd))
                {
                    hwnd = IntPtr.Zero;
                }

                var foundHwnd = FindWindow("Windows.UI.Core.CoreWindow", Language.NotificationName);

                if (Settings.EnableDebugForceFallbackMode)
                {
                    if (!hasLoggedForcedFallback)
                    {
                        Program.Logger.Information($"Fallback detection is being forced");
                        hasLoggedForcedFallback = true;
                    }

                    foundHwnd = IntPtr.Zero; // Always use fallback mode if this setting is enabled
                }

                ScaledPreferredDisplayWidth = ResolutionFinder.GetScaledResolution().Width;
                ScaledPreferredDisplayHeight = ResolutionFinder.GetScaledResolution().Height;
                RealPreferredDisplayWidth = ResolutionFinder.GetRealResolution().Width;
                RealPreferredDisplayHeight = ResolutionFinder.GetRealResolution().Height;
                ScaleFactor = ResolutionFinder.GetInverseScale();

                //The Notification Isn't In A Supported Language
                if (foundHwnd == IntPtr.Zero && ShouldRunFallbackDetection())
                {
                    if (!hasLoggedFallbackDetection)
                    {
                        Program.Logger.Information($"Couldn't use language-specific window detection, using fallback detection");
                        hasLoggedFallbackDetection = true;
                    }

                    lastFallbackDetection = DateTime.UtcNow;

                    //The Notification Window Is The Only One That Is 396 x 152
                    foreach (var win in FindCoreWindows())
                    {
                        Rectangle rect = new Rectangle();
                        GetWindowRect(win, ref rect);

                        if ((ScaledPreferredDisplayWidth - rect.X) == 396)
                        {
                            foundHwnd = win;
                        }
                    }
                }

                if (foundHwnd != IntPtr.Zero)
                {
                    // Report These Again The Next Time Something Goes Wrong
                    hasLoggedFallbackDetection = false;
                    hasLoggedMissingWindow = false;

                    if (hwnd != foundHwnd)
                    {
                        Program.Logger.Information($"Found notification window {foundHwnd}");
                        hwnd = foundHwnd;
                    }

                    // Apply The Position/Opacity Right Away Instead Of Waiting For The Next Tick
                    InterceptorManager.Instance?.RequestImmediateUpdate();
                }
                else if (!hasLoggedMissingWindow)
                {
                    // Only Logged Once, So An Idle Daemon Doesn't Spam The Log File
                    Program.Logger.Information($"Couldn't find the handle of the notification window");
                    hasLoggedMissingWindow = true;
                }

                Update();

            }
            catch { }
        }

        public override void OnKeyUpdate()
        {
            // Delay until the keypress has been processed
            Task.Run(async () =>
            {
                await Task.Delay(100);
                ExStyleManager.Update(hwnd);
            });
            
            base.OnKeyUpdate();
        }

        public override void Update()
        {
            base.Update();

            // Nothing To Do Until Reflow() Finds The Notification Window
            if (hwnd == IntPtr.Zero) { return; }

            // The Notification Window Can Disappear At Any Moment,
            // Don't Keep Calling Win32 On A Dead Handle
            if (!IsWindow(hwnd))
            {
                Program.Logger.Information($"Notification window {hwnd} no longer exists");
                hwnd = IntPtr.Zero;
                return;
            }

            // Update extended styles
            ExStyleManager.Update(hwnd);

            // Find The Bounds Of The Notification Window
            Rectangle NotifyRect = new Rectangle();
            if (!GetWindowRect(hwnd, ref NotifyRect)) { return; }

            // Find The Bounds Of The Preferred Monitor
            // (Cached, So This Doesn't Enumerate Displays On Every Tick)
            var currentMonitorInfo = ResolutionFinder.GetPreferredDisplayInfo();
            var originX = currentMonitorInfo.Monitor.Left;
            var originY = currentMonitorInfo.Monitor.Top;

            var scaledWidth = (int)((NotifyRect.Width - NotifyRect.X * ScaleFactor));
            var scaledHeight = (int)((NotifyRect.Height - NotifyRect.Y * ScaleFactor));
            var unscaledWidth = (int)((NotifyRect.Width - NotifyRect.X));
            var unscaledHeight = (int)((NotifyRect.Height - NotifyRect.Y));

            int targetX;
            int targetY;

            if (Settings.Location == NotifyLocation.TopLeft)
            {
                //Easy Peesy
                targetX = originX + 0;
                targetY = originY + 0;
            }
            else if (Settings.Location == NotifyLocation.TopRight)
            {
                targetX = originX + (RealPreferredDisplayWidth - unscaledWidth);
                targetY = 0;
            }
            else if (Settings.Location == NotifyLocation.BottomLeft)
            {
                targetX = originX + 0;
                targetY = originY + (RealPreferredDisplayHeight - unscaledHeight - (int)Math.Round(50f));
            }
            else if (Settings.Location == NotifyLocation.BottomRight) // Default In Windows, But Here For Completeness Sake
            {
                targetX = originX + (RealPreferredDisplayWidth - unscaledWidth);
                targetY = originY + (RealPreferredDisplayHeight - unscaledHeight - (int)Math.Round(50f));
            }
            else // Custom Position
            {
                var xPosition = (int)(Settings.CustomPositionPercentX / 100f * RealPreferredDisplayWidth);
                var yPosition = (int)(Settings.CustomPositionPercentY / 100f * RealPreferredDisplayHeight);

                if (!Settings.EnableDebugRemoveBoundsCorrection)
                {
                    // Make Sure Position Isn't Out Of Bounds
                    xPosition = Math.Clamp(xPosition, 0, RealPreferredDisplayWidth - unscaledWidth);
                    yPosition = Math.Clamp(yPosition, 0, RealPreferredDisplayHeight - unscaledHeight);
                }

                targetX = originX + xPosition;
                targetY = originY + yPosition;
            }

            // Only Move The Window When It Isn't Already Where It Should Be,
            // Otherwise It Would Be Moved 100 Times Per Second For No Reason
            var alreadyInPosition = IsWindowVisible(hwnd)
                && Math.Abs(NotifyRect.X - targetX) <= PositionTolerance
                && Math.Abs(NotifyRect.Y - targetY) <= PositionTolerance;

            if (!alreadyInPosition)
            {
                SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
            }

        }
    }
}
