using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace TorusGateway
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool isRestart = e.Args.Contains("--auto-connect");

            _mutex = new Mutex(true, "TorusGateway_SingleInstance", out bool createdNew);

            if (!createdNew && !isRestart)
            {
                // 기존 프로세스의 메인 윈도우 핸들로 트레이 상태 판별
                var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
                var existing = Array.Find(
                    Process.GetProcessesByName(processName),
                    p => p.Id != Environment.ProcessId);

                string msg = existing?.MainWindowHandle == IntPtr.Zero
                    ? "TORUS Gateway is already running in the system tray."
                    : "TORUS Gateway is already running.";

                MessageBox.Show(msg, "TORUS Gateway", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
            new MainWindow().Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CleanupTempDirectories(WebServer.FileController.TempDirDownload);
            CleanupTempDirectories(WebServer.FileController.TempDirUpload);
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }

        private static void CleanupTempDirectories(string dirName)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dirName);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
