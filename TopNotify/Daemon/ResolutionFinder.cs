using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TopNotify.Common;
using Windows.Devices.Display.Core;
using Windows.Media.DialProtocol;
using static TopNotify.Daemon.NativeInterceptor;

namespace TopNotify.Daemon
{
    public class ResolutionFinder
    {
        #region WinAPI


        [DllImport("shcore.dll")]
        private static extern IntPtr GetDpiForMonitor([In] IntPtr hmonitor, [In] DpiType dpiType, [Out] out uint dpiX, [Out] out uint dpiY);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint([In] Point pt, [In] uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MonitorInfo lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

        delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DisplayDevice
        {
            [MarshalAs(UnmanagedType.U4)]
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            [MarshalAs(UnmanagedType.U4)]
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public class MonitorInfo
        {
            public int Size = 72;
            public Rect Monitor = new Rect();
            public Rect WorkArea = new Rect();
            public uint Flags = 0;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName = "";
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private enum DpiType
        {
            Effective = 0,
            Angular = 1,
            Raw = 2,
        }

        #endregion

        #region Cache

        // Monitor Data Is Cached, Because Otherwise Every Update Tick Would Enumerate All Displays.
        // The Cache Is Rebuilt After CacheLifetime, Or Immediately When InvalidateCache() Is Called.

        static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

        static IntPtr cachedPreferredDisplay = IntPtr.Zero;
        static bool cachedPreferredDisplayValid = false;
        static DateTime cachedPreferredDisplayStamp = DateTime.MinValue;

        static MonitorInfo cachedMonitorInfo = new MonitorInfo();
        static bool cachedMonitorInfoValid = false;
        static DateTime cachedMonitorInfoStamp = DateTime.MinValue;

        static float cachedInverseScale = 1f;
        static bool cachedInverseScaleValid = false;
        static DateTime cachedInverseScaleStamp = DateTime.MinValue;

        static bool IsCacheFresh(DateTime stamp)
        {
            return (DateTime.UtcNow - stamp) < CacheLifetime;
        }

        /// <summary>
        /// Drops All Cached Monitor Data, So It Will Be Read Again On Next Use
        /// (Call This When The Settings Or The Display Configuration Change)
        /// </summary>
        public static void InvalidateCache()
        {
            cachedPreferredDisplayValid = false;
            cachedMonitorInfoValid = false;
            cachedInverseScaleValid = false;
        }

        /// <summary>
        /// Returns The Monitor Info (Bounds, Work Area, Device Name) Of The Preferred Display
        /// </summary>
        public static MonitorInfo GetPreferredDisplayInfo()
        {
            if (cachedMonitorInfoValid && IsCacheFresh(cachedMonitorInfoStamp))
            {
                return cachedMonitorInfo;
            }

            var monitorInfo = new MonitorInfo();
            GetMonitorInfo(GetPreferredDisplay(), monitorInfo);

            cachedMonitorInfo = monitorInfo;
            cachedMonitorInfoValid = true;
            cachedMonitorInfoStamp = DateTime.UtcNow;

            return cachedMonitorInfo;
        }

        #endregion

        public static List<MonitorData> GetMonitors()
        {
            var monitors = new List<MonitorData>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
            {
                MonitorInfo currentMonitorInfo = new MonitorInfo();

                if (GetMonitorInfo(hMonitor, currentMonitorInfo))
                {
                    monitors.Add(new MonitorData()
                    {
                        GraphicsDriverName = "",
                        FriendlyName = currentMonitorInfo.DeviceName,
                        ID = currentMonitorInfo.DeviceName,
                        Path = currentMonitorInfo.DeviceName,
                    });
                }
                return true;
            }, IntPtr.Zero);

            return monitors;
        }

        /// <summary>
        /// Returns An HMONITOR IntPtr
        /// The Result Is Cached, So The Display List Isn't Enumerated On Every Update Tick
        /// </summary>
        public static IntPtr GetPreferredDisplay()
        {
            if (cachedPreferredDisplayValid && IsCacheFresh(cachedPreferredDisplayStamp))
            {
                return cachedPreferredDisplay;
            }

            var returnedMonitor = IntPtr.Zero;

            // Find Loaded Settings File Or Load It
            Settings settingsFile;

            if (InterceptorManager.Instance != null)
            {
                settingsFile = InterceptorManager.Instance.CurrentSettings;
            }
            else
            {
                settingsFile = Settings.Get();
            }

            // Determine Whether To Use A Non-Primary Monitor
            if (settingsFile.PreferredMonitor != "primary")
            {
                // Try To Find The Preferred Monitor
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
                {
                    MonitorInfo currentMonitorInfo = new MonitorInfo();

                    if (GetMonitorInfo(hMonitor, currentMonitorInfo) && currentMonitorInfo.DeviceName == settingsFile.PreferredMonitor)
                    {
                        returnedMonitor = hMonitor;
                    }
                    return true;
                }, IntPtr.Zero);
            }

            // Return Primary Display If returnedMonitor Is 0
            cachedPreferredDisplay = (returnedMonitor != IntPtr.Zero) ? returnedMonitor : MonitorFromPoint(new Point(0, 0), 0x00000001);
            cachedPreferredDisplayValid = true;
            cachedPreferredDisplayStamp = DateTime.UtcNow;

            return cachedPreferredDisplay;
        }

        public static Rectangle GetScaledResolution()
        {
            var factor = 1f + (((1f / GetInverseScale()) - 1f) / 2);
            var realRes = GetRealResolution();

            return new Rectangle(0, 0, (int)(realRes.Width * factor), (int)(realRes.Height * factor));
        }

        public static Rectangle GetRealResolution()
        {
            var monitorInfo = GetPreferredDisplayInfo();
            return new Rectangle(0, 0, monitorInfo.Monitor.Right - monitorInfo.Monitor.Left, monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);
        }

        public static float GetInverseScale()
        {
            if (cachedInverseScaleValid && IsCacheFresh(cachedInverseScaleStamp))
            {
                return cachedInverseScale;
            }

            uint dpiX;
            GetDpiForMonitor(GetPreferredDisplay(), DpiType.Effective, out dpiX, out _);

            // Fall Back To 100% Scaling If The DPI Query Failed
            if (dpiX == 0) { dpiX = 96; }

            cachedInverseScale = 100f / (dpiX * 100 / 96f);
            cachedInverseScaleValid = true;
            cachedInverseScaleStamp = DateTime.UtcNow;

            return cachedInverseScale;
        }

        public static float GetScale()
        {
            return 1f / GetInverseScale();
        }
    }
}
