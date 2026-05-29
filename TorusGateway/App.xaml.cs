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
        private bool _ownsMutex; // 뮤텍스를 새로 만들어 소유한 경우만 true (해제는 소유 시에만 가능)

        protected override void OnStartup(StartupEventArgs e)
        {
            // 전역 예외 핸들러 등록 — 처리되지 않은 예외로 인한 무통보 크래시 방지.
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;

            bool isRestart = e.Args.Contains("--auto-connect");

            _mutex = new Mutex(true, "TorusGateway_SingleInstance", out bool createdNew);
            _ownsMutex = createdNew;

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
            CleanupTempDirectories(WebServer.CustomController.TempDirDownload);
            CleanupTempDirectories(WebServer.CustomController.TempDirUpload);
            // 소유한 경우에만 해제 (소유하지 않은 스레드의 ReleaseMutex는 ApplicationException).
            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
            }
            _mutex?.Dispose(); // Dispose는 소유와 무관하게 항상 안전
            base.OnExit(e);
        }

        // UI 스레드에서 처리되지 않은 예외: 로그 + 안내 후 앱을 계속 실행한다(게이트웨이 가동 유지 우선).
        private void App_DispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            WebServer.FileLogger.WriteError($"Unhandled UI exception: {e.Exception}");
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}",
                "TORUS Gateway", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true; // 예외를 삼켜 앱 종료를 막음
        }

        // UI 스레드 외(백그라운드 스레드 등)의 치명적 예외: 종료를 막을 수 없으므로 로그만 남긴다.
        private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            WebServer.FileLogger.WriteError(
                $"Fatal unhandled exception (terminating={e.IsTerminating}): {ex}");
        }

        private static void CleanupTempDirectories(string dirName)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dirName);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                WebServer.FileLogger.WriteWarn($"Temp dir cleanup failed: {path} - {ex.Message}");
            }
        }
    }
}
