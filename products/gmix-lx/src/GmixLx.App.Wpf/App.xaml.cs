using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace GmixLx.App.Wpf
{
    public partial class App : Application
    {
        /// <summary>单实例互斥体名；与设备页/主窗口无关，仅约束本进程不得开第二份。</summary>
        const string SingleInstanceMutexName = "GmixLx.App.Wpf.SingleInstance";

        Mutex _mutex;
        bool _mutexOwned;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, SingleInstanceMutexName, out _mutexOwned);
            if (!_mutexOwned)
            {
                // 第二份实例：尽力把已有窗口提到前台，然后静默退出，不再创建主窗口。
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);
            Visualization.CurvePlotControl.Prewarm();
            new GmixLx.App.Wpf.MainWindow().Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutexOwned)
            {
                _mutex.ReleaseMutex();
                _mutexOwned = false;
            }
            if (_mutex != null)
            {
                _mutex.Dispose();
                _mutex = null;
            }
            base.OnExit(e);
        }

        /// <summary>按进程名找同程序的另一实例，恢复并置前其主窗口；找不到时不做兜底。</summary>
        static void ActivateExistingInstance()
        {
            Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id) continue;

                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;

                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                return;
            }
        }

        const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
