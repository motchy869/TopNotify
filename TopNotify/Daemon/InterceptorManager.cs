using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopNotify.Common;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using static TopNotify.Daemon.NativeInterceptor;

namespace TopNotify.Daemon
{
    public class InterceptorManager
    {
        #region WinAPI Methods

        #endregion

        public static InterceptorManager Instance;
        public List<Interceptor> Interceptors = new();

        public Settings CurrentSettings;

        /// <summary>Update interval (in milliseconds) used while a notification window is being tracked</summary>
        public const int ActiveUpdateInterval = 10;

        /// <summary>Update interval (in milliseconds) used while nothing is being tracked (CPU back-off)</summary>
        public const int IdleUpdateInterval = 250;

        /// <summary>How often (in milliseconds) windows, monitors and configs are rediscovered</summary>
        public const int ReflowInterval = 500;

        // Wakes The Main Loop Up Early Whenever Something Happened
        readonly ManualResetEventSlim updateSignal = new ManualResetEventSlim(false);

        // Set When A Reflow Should Run Before The Next Scheduled One
        volatile bool reflowRequested = false;

        /// <summary>
        /// True While A Reflow Was Requested Outside Of The Normal Schedule
        /// (Interceptors Can Use This To Allow Expensive Detection)
        /// </summary>
        public bool IsForcedReflow { get; private set; }

        public ConcurrentDictionary<uint, Action?> CleanUpFunctions = new ConcurrentDictionary<uint, Action?>(); // Maps HandledNotifications to the associated clean up function
        public UserNotificationListener Listener;
        public bool CanListenToNotifications = false;

        public static Interceptor[] InstalledInterceptors =
        {
            new NativeInterceptor(),
            new DiscoveryInterceptor(),
            new SoundInterceptor(),
            new ReadAloudInterceptor()
        };

        public void Start()
        {
            Instance = this;
            CurrentSettings = Settings.Get();

            foreach (var possibleInterceptor in InstalledInterceptors)
            {
                // Check If It's Eligible To Be Enabled
                if (possibleInterceptor.ShouldEnable())
                {
                    Interceptors.Add(possibleInterceptor);
                }
            }

            Listener = UserNotificationListener.Current;
            Task.Run(async () =>
            {

                //Ask For Permissions To Read Notifications
                var access = await Listener.RequestAccessAsync();
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    var msg = "Failed To Start Notification Listener: Permission Denied";
                    DaemonErrorHandler.ThrowNonCritical(new DaemonError("listener_failure_no_permission", msg));
                    return;
                }


                try
                {
                    //Throws a COM exception if not packaged into an MSIX app
                    //Currently no workaround
                    Listener.NotificationChanged += OnNotificationChanged;
                }
                catch (Exception ex)
                {
                    var msg = "Failed To Start Notification Listener: Not Packaged";
                    DaemonErrorHandler.ThrowNonCritical(new DaemonError("listener_failure_not_packaged", msg));
                    return;
                }

                CanListenToNotifications = true;

            });

            foreach (Interceptor i in Interceptors)
            {
                i.Start();
            }

            MainLoop();
        }

        /// <summary>
        /// True While At Least One Interceptor Needs To Be Updated At A High Frequency
        /// </summary>
        public bool NeedsFrequentUpdate
        {
            get
            {
                foreach (Interceptor i in Interceptors)
                {
                    if (i.NeedsFrequentUpdate) { return true; }
                }

                return false;
            }
        }

        /// <summary>
        /// Wakes The Main Loop Up, So Changes Are Applied Without Waiting For The Idle Interval
        /// </summary>
        public void RequestImmediateUpdate()
        {
            updateSignal.Set();
        }

        /// <summary>
        /// Wakes The Main Loop Up And Runs A Reflow Before The Next Scheduled One
        /// (Used When A Notification Arrives, So Its Window Is Detected Right Away)
        /// </summary>
        public void RequestImmediateReflow()
        {
            reflowRequested = true;
            updateSignal.Set();
        }

        public void MainLoop()
        {
            var reflowTimer = Stopwatch.StartNew();

            while (true)
            {
                if (reflowRequested || reflowTimer.ElapsedMilliseconds >= ReflowInterval)
                {
                    var wasRequested = reflowRequested;
                    reflowRequested = false;

                    reflowTimer.Restart();
                    Reflow(wasRequested);
                }

                Update();

                // Poll At Full Speed While A Notification Window Is Visible,
                // Otherwise Back Off So The Daemon Doesn't Burn CPU While Idle
                var updateInterval = NeedsFrequentUpdate ? ActiveUpdateInterval : IdleUpdateInterval;

                if (updateSignal.Wait(updateInterval))
                {
                    updateSignal.Reset();
                }
            }
        }

        public void Reflow()
        {
            Reflow(false);
        }

        /// <summary>
        /// Runs A Reflow On Every Interceptor
        /// </summary>
        /// <param name="forced">True When A Notification Arrived, So Expensive Detection Is Worth Running Right Away</param>
        public void Reflow(bool forced)
        {
            IsForcedReflow = forced;

            try
            {
                foreach (Interceptor i in Interceptors)
                {
                    try
                    {
                        i.Reflow();
                    }
                    catch (Exception ex) { }
                }
            }
            finally
            {
                IsForcedReflow = false;
            }
        }

        public void Update()
        {
            foreach (Interceptor i in Interceptors)
            {
                try
                {
                    i.Update();
                }
                catch (Exception ex) { }
            }
        }

        // Called from C++ to safely invoke the OnKeyUpdate method
        public static void TryOnKeyUpdate()
        {
            if (Instance == null) { return; }
            Instance.OnKeyUpdate();
        }

        public void OnKeyUpdate()
        {
            foreach (Interceptor i in Interceptors)
            {
                try
                {
                    i.OnKeyUpdate();
                }
                catch (Exception ex) { }
            }
        }

        // Runs When A New Notification Is Added Or Removed
        public async void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            var userNotifications = await Listener.GetNotificationsAsync(NotificationKinds.Toast);
            var userNotification = userNotifications.Where((n) => n.Id == args.UserNotificationId).FirstOrDefault();

            if (args.ChangeKind == UserNotificationChangedKind.Added)
            {
                foreach (Interceptor i in Interceptors)
                {
                    try
                    {
                        i.OnNotification(userNotification);
                    }
                    catch { }
                }
            }

            // Detect The Notification Window Right Away, Instead Of Waiting For The Next Scheduled Reflow
            RequestImmediateReflow();
        }

        public void OnSettingsChanged()
        {
            CurrentSettings = Settings.Get();

            // The Preferred Monitor May Have Changed, So Cached Monitor Data Must Be Thrown Away
            ResolutionFinder.InvalidateCache();

            foreach (Interceptor i in Interceptors)
            {
                try
                {
                    i.Restart();
                    i.Reflow();
                }
                catch (Exception ex) { }
            }

            RequestImmediateUpdate();
        }
    }
}
