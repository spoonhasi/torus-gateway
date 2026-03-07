using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using TorusGateway.Torus;
using TorusGateway.WebServer;
using static TorusGateway.Torus.MachineListControl;

namespace TorusGateway
{
    //TORUS App 등록정보
    static class Constants
    {
        public const string TorusAppGuid = "0C1016A3-45BF-4DE9-B0B8-1ECBB948177D";
        public const string TorusAppName = "TorusGateway";
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private WebServer.WebServer? _webServer;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isActuallyClosing = false;
        private bool _torusConnectFailed = false; // Initialize 후 Connect 실패 여부

        private const int MaxLogLines = 1000;
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly ObservableCollection<string> _logEntries = new();
        private readonly DispatcherTimer _logTimer;

        private const string ConfigFile = "config.ini";
        private const string ConfigSection = "MainWindow";

        public MainWindow()
        {
            TorusInit(Constants.TorusAppName, Constants.TorusAppGuid);//TORUS App 등록정보를 입력합니다.
            InitializeComponent();
            ButtonWebServerOpen.IsEnabled = false;
            ButtonWebServerClose.IsEnabled = false;

            LoadSettings();

            ListBoxApiLog.ItemsSource = _logEntries;
            RequestLoggingMiddleware.RequestLogged += OnRequestLogged;

            // 200ms 간격으로 로그 배치 갱신 (Dispatcher 큐 과적 방지)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _logTimer.Tick += FlushLogQueue;
            _logTimer.Start();

            this.Closing += MainWindow_Closing;

            // 시스템 트레이 아이콘 초기화
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Text = "TorusGateway",
                Visible = false
            };
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;

            // --auto-connect 인자가 있으면 로드 후 자동 연결
            if (Environment.GetCommandLineArgs().Contains("--auto-connect"))
            {
                this.Loaded += (s, e) => ButtonTorusConnect.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, ButtonTorusConnect));
            }
        }

        // 미들웨어 스레드에서 호출 — ConcurrentQueue로 lock 없이 안전하게 적재
        private void OnRequestLogged(string logEntry)
        {
            _logQueue.Enqueue(logEntry);
        }

        // UI 스레드에서 200ms마다 큐를 비우고 ListBox에 반영
        private void FlushLogQueue(object? sender, EventArgs e)
        {
            bool added = false;
            while (_logQueue.TryDequeue(out var entry))
            {
                _logEntries.Add(entry);
                added = true;
            }

            while (_logEntries.Count > MaxLogLines)
            {
                _logEntries.RemoveAt(0);
            }

            // 사용자가 맨 아래에 있을 때만 자동 스크롤 (위로 스크롤해서 과거 로그 확인 중이면 방해하지 않음)
            if (added && _logEntries.Count > 0 && IsScrolledToBottom(ListBoxApiLog))
            {
                ListBoxApiLog.ScrollIntoView(_logEntries[^1]);
            }
        }

        private static bool IsScrolledToBottom(System.Windows.Controls.ListBox listBox)
        {
            var scrollViewer = GetScrollViewer(listBox);
            if (scrollViewer == null) return true;
            return scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1;
        }

        private static System.Windows.Controls.ScrollViewer? GetScrollViewer(System.Windows.DependencyObject dep)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(dep, i);
                if (child is System.Windows.Controls.ScrollViewer sv) return sv;
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isActuallyClosing)
            {
                e.Cancel = true;
                SaveSettings();
                this.Hide();
                _notifyIcon!.Visible = true;
                return;
            }
            // _isActuallyClosing == true면 ExitApplication에서 이미 정리 완료
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            _notifyIcon!.Visible = false;
        }

        private async void ExitApplication()
        {
            _isActuallyClosing = true;
            SaveSettings();
            _logTimer.Stop();
            RequestLoggingMiddleware.RequestLogged -= OnRequestLogged;
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            try
            {
                if (_webServer != null)
                {
                    await _webServer.StopAsync();
                    _webServer.Dispose();
                    _webServer = null;
                }
            }
            catch { }
            Environment.Exit(0);
        }

        private async void RestartApplication(bool autoConnect = false)
        {
            SaveSettings();
            // 웹서버가 켜져 있으면 먼저 종료 (포트 충돌 방지)
            try
            {
                if (_webServer != null)
                {
                    await _webServer.StopAsync();
                    _webServer.Dispose();
                    _webServer = null;
                }
            }
            catch { }
            // 새 프로세스 시작 후 현재 프로세스 강제 종료 (네이티브 DLL 포함 완전 해제)
            var args = autoConnect ? "--auto-connect" : "";
            System.Diagnostics.Process.Start(Environment.ProcessPath!, args);
            Environment.Exit(0);
        }

        private void LoadSettings()
        {
            TextBoxWebServerHost.Text = ReadConfig(ConfigFile, ConfigSection, "Host", "0.0.0.0");
            TextBoxWebServerPort.Text = ReadConfig(ConfigFile, ConfigSection, "Port", "5001");

            if (double.TryParse(ReadConfig(ConfigFile, ConfigSection, "Width", ""), out double w) && w >= 200)
                this.Width = w;
            if (double.TryParse(ReadConfig(ConfigFile, ConfigSection, "Height", ""), out double h) && h >= 150)
                this.Height = h;
        }

        private void SaveSettings()
        {
            WriteConfig(ConfigFile, ConfigSection, "Host", TextBoxWebServerHost.Text);
            WriteConfig(ConfigFile, ConfigSection, "Port", TextBoxWebServerPort.Text);

            if (this.WindowState == WindowState.Normal)
            {
                WriteConfig(ConfigFile, ConfigSection, "Width", this.Width.ToString("F0"));
                WriteConfig(ConfigFile, ConfigSection, "Height", this.Height.ToString("F0"));
            }
        }

        //TORUS에 연결하기 전에 App 등록정보를 TORUS 운용 클래스에 입력합니다.
        public static void TorusInit(string appName, string appGuid)
        {
            Torus.Torus.Initialize(appName, appGuid);
        }

        //TORUS에 연결합니다.
        public static int TorusConnect()
        {
            return Torus.Torus.Instance.Connect();
        }

        //웹서버를 구동합니다. 외부에서 이 웹 서버에 연결합니다. host와 port정보를 필요로 합니다.
        public void WebServerOpen(string host, int port)
        {
            if (_webServer == null)
            {
                _webServer = new WebServer.WebServer(host, port);
                _webServer.Start();
            }
        }

        //웹서버를 종료합니다.
        public async Task WebServerCloseAsync()
        {
            if (_webServer != null)
            {
                await _webServer.StopAsync();  // 비동기적으로 웹 서버를 종료
                _webServer.Dispose();
                _webServer = null;
                ButtonWebServerOpen.IsEnabled = true;
                ButtonWebServerClose.IsEnabled = false;
                TextBoxWebServerHost.IsReadOnly = false;
                TextBoxWebServerPort.IsReadOnly = false;
            }
        }

        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender == ButtonTorusConnect)//TORUS 연결
            {
                // Initialize 후 Connect 실패 이력이 있으면 앱 재시작으로 재초기화
                if (_torusConnectFailed)
                {
                    RestartApplication(autoConnect: true);
                    return;
                }

                ButtonTorusConnect.Content = "Connecting...";
                ButtonTorusConnect.IsEnabled = false;

                int result = await Task.Run(() => TorusConnect());

                if (result == 0)
                {
                    ButtonTorusConnect.Content = "Connected";
                    ButtonWebServerOpen.IsEnabled = true;
                }
                else
                {
                    _torusConnectFailed = true;
                    string message = Torus.Torus.Instance.LastErrorMessage;
                    MessageBox.Show(string.IsNullOrEmpty(message) ? "TORUS connection failed" : message, "Error");
                    ButtonTorusConnect.Content = "Reconnect";
                    ButtonTorusConnect.IsEnabled = true;
                }
            }
            else if (sender == ButtonWebServerOpen)//게이트웨이 서버 오픈
            {
                string host = TextBoxWebServerHost.Text;
                if (host.Trim() == "")
                {
                    MessageBox.Show("Invalid Host", "Error");
                    return;
                }
                if (!int.TryParse(TextBoxWebServerPort.Text, out int port) || port < 1 || port > 65535)
                {
                    MessageBox.Show("Invalid Port (1-65535)", "Error");
                    return;
                }
                try
                {
                    WebServerOpen(host, port);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Server start failed: {ex.Message}", "Error");
                    return;
                }
                ButtonWebServerOpen.IsEnabled = false;
                ButtonWebServerClose.IsEnabled = true;
                TextBoxWebServerHost.IsReadOnly = true;
                TextBoxWebServerPort.IsReadOnly = true;
            }
            else if (sender == ButtonWebServerClose)//게이트웨이 서버 종료
            {
                try { await WebServerCloseAsync(); }
                catch (Exception ex) { MessageBox.Show($"Server stop failed: {ex.Message}", "Error"); }
            }
            else if (sender == ButtonTorusSetting)//TORUS 설정 모달 창 열기
            {
                var settingWindow = new Torus.MachineListWindow();
                settingWindow.Owner = this;
                settingWindow.ShowDialog();
            }
            else if (sender == ButtonAbout)//About 정보 표시
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                MessageBox.Show(
                    $"TORUS Gateway\n\nVersion: {version!.Major}.{version.Minor}.{version.Build}\n\nTORUS Platform REST API Gateway",
                    "About TORUS Gateway",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (sender == ButtonMinimizeToTray)//트레이로 보내기
            {
                SaveSettings();
                this.Hide();
                _notifyIcon!.Visible = true;
            }
            else if (sender == ButtonExit)//프로그램 완전 종료
            {
                ExitApplication();
            }
        }
    }
}
