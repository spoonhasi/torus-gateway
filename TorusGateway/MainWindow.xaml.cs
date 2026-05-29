using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using TorusGateway.Torus;
using TorusGateway.WebServer;

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
        private int _modalDepth = 0; // 모달 창(Settings, Docs 등) 표시 중인 개수

        private const int MaxLogLines = 1000;
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly ObservableCollection<string> _logEntries = new();
        private readonly DispatcherTimer _logTimer;

        private const string ConfigFile = "config.ini";
        private const string ConfigSection = "MainWindow";
        private const string LoggingSection = "Logging";
        private const int DefaultLogMaxTotalMB = 50;

        public MainWindow()
        {
            TorusInit(Constants.TorusAppName, Constants.TorusAppGuid);//TORUS App 등록정보를 입력합니다.
            InitializeComponent();
            ButtonWebServerOpen.IsEnabled = false;
            ButtonWebServerClose.IsEnabled = false;

            // 이전 세션의 비정상 종료로 남은 임시 파일 디렉토리 정리.
            CustomController.CleanupOrphanedTempDirs();

            LoadSettings();

            ListBoxApiLog.ItemsSource = _logEntries;
            RequestLoggingMiddleware.RequestLogged += OnRequestLogged;

            // 200ms 간격으로 로그 배치 갱신 (Dispatcher 큐 과적 방지)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _logTimer.Tick += FlushLogQueue;
            _logTimer.Start();

            this.Closing += MainWindow_Closing;
            this.StateChanged += MainWindow_StateChanged;

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
            // 모달 창이 열려있는 동안은 작업표시줄 등으로 인한 종료/트레이 이동을 차단
            if (_modalDepth > 0)
            {
                e.Cancel = true;
                return;
            }
            if (!_isActuallyClosing)
            {
                e.Cancel = true;
                SaveWindowSettings();
                this.Hide();
                _notifyIcon!.Visible = true;
                return;
            }
            // _isActuallyClosing == true면 ExitApplication에서 이미 정리 완료
        }

        // 모달 창이 열린 상태에서 작업표시줄 클릭 등으로 최소화되는 것을 차단
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_modalDepth > 0 && this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;
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
            var result = MessageBox.Show(
                "Are you sure you want to exit TORUS Gateway?",
                "Exit Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _isActuallyClosing = true;
            SaveWindowSettings();
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
            SaveWindowSettings();
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

        // 라디오 버튼 전환 시 IP 지정 패널의 표시 여부 갱신 + 실행 중이면 즉시 반영.
        private void RadioAccess_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelAllowedIPs == null) return; // InitializeComponent 중 이벤트 발생 대비
            PanelAllowedIPs.Visibility = RadioAccessSpecific.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyAllowlistIfRunning();
        }

        // config.ini의 AllowedIPs 문자열을 읽어 라디오 상태/리스트로 복원
        private void LoadAccessMode(string csv)
        {
            var entries = (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (entries.Count == 1 && entries[0] == "*")
            {
                RadioAccessAll.IsChecked = true;
            }
            else if (entries.Count > 0 && entries.All(e => e == "127.0.0.1" || e == "::1"))
            {
                RadioAccessLocal.IsChecked = true;
            }
            else
            {
                RadioAccessSpecific.IsChecked = true;
                ListBoxAllowedIPs.Items.Clear();
                foreach (var e in entries) ListBoxAllowedIPs.Items.Add(e);
            }
        }

        // 현재 라디오/리스트 상태를 AllowedIPs CSV로 직렬화
        private string GetAllowedIPsCsv()
        {
            if (RadioAccessAll.IsChecked == true) return "*";
            if (RadioAccessLocal.IsChecked == true) return "127.0.0.1";
            return string.Join(",", ListBoxAllowedIPs.Items.Cast<string>());
        }

        // IP 또는 CIDR 형식 유효성 검사
        private static bool IsValidIpEntry(string entry)
        {
            try
            {
                if (entry.Contains('/'))
                    _ = System.Net.IPNetwork.Parse(entry);
                else
                    _ = System.Net.IPAddress.Parse(entry);
                return true;
            }
            catch { return false; }
        }

        // 웹서버 실행 중 접근 제어가 바뀌면 즉시 반영 + config 저장.
        private void ApplyAllowlistIfRunning()
        {
            if (_webServer == null) return;
            var csv = GetAllowedIPsCsv();
            _webServer.UpdateAllowlist(csv);
            ConfigManager.Write(ConfigFile, ConfigSection, "AllowedIPs", csv);
        }

        private void LoadSettings()
        {
            TextBoxWebServerHost.Text = ConfigManager.Read(ConfigFile, ConfigSection, "Host", "0.0.0.0");
            TextBoxWebServerPort.Text = ConfigManager.Read(ConfigFile, ConfigSection, "Port", "5001");
            LoadAccessMode(ConfigManager.Read(ConfigFile, ConfigSection, "AllowedIPs", "*"));

            if (double.TryParse(ConfigManager.Read(ConfigFile, ConfigSection, "Width", ""), out double w) && w >= 200)
                this.Width = w;
            if (double.TryParse(ConfigManager.Read(ConfigFile, ConfigSection, "Height", ""), out double h) && h >= 150)
                this.Height = h;

            // 파일 로그 초기화 (0이면 비활성화)
            int maxLogMB = DefaultLogMaxTotalMB;
            if (int.TryParse(ConfigManager.Read(ConfigFile, LoggingSection, "MaxTotalMB", DefaultLogMaxTotalMB.ToString()), out int m) && m >= 0)
                maxLogMB = m;
            FileLogger.Init(maxLogMB);
        }

        // 창 크기만 저장 (창 닫기/종료/재시작/트레이 시)
        private void SaveWindowSettings()
        {
            if (this.WindowState == WindowState.Normal)
            {
                ConfigManager.Write(ConfigFile, ConfigSection, "Width", this.Width.ToString("F0"));
                ConfigManager.Write(ConfigFile, ConfigSection, "Height", this.Height.ToString("F0"));
            }
        }

        // 서버 관련 설정 저장 (WebServer Open 성공 시에만)
        private void SaveServerSettings()
        {
            ConfigManager.Write(ConfigFile, ConfigSection, "Host", TextBoxWebServerHost.Text);
            ConfigManager.Write(ConfigFile, ConfigSection, "Port", TextBoxWebServerPort.Text);
            ConfigManager.Write(ConfigFile, ConfigSection, "AllowedIPs", GetAllowedIPsCsv());
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
        public async Task WebServerOpenAsync(string host, int port, string allowedIpsCsv)
        {
            if (_webServer != null) return;

            var server = new WebServer.WebServer(host, port, allowedIpsCsv);
            try
            {
                await server.StartAsync(); // 바인딩 실패 시 예외 발생
            }
            catch
            {
                server.Dispose();
                throw; // 호출부(Button_Click)에서 메시지 표시
            }
            _webServer = server; // 정상 기동 시에만 보관
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
                    await WebServerOpenAsync(host, port, GetAllowedIPsCsv());
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Server start failed: {ex.Message}", "Error");
                    return;
                }
                SaveServerSettings(); // 개통 성공 시에만 config.ini에 영구 저장
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
            else if (sender == ButtonDocs)//커스텀 엔드포인트 문서 모달 창 열기
            {
                var docsWindow = new DocsWindow { Owner = this };
                _modalDepth++;
                try { docsWindow.ShowDialog(); }
                finally { _modalDepth--; }
            }
            else if (sender == ButtonTestRequest)//GET/POST 테스트 모달 창 열기
            {
                var testWindow = new TestRequestWindow(TextBoxWebServerHost.Text, TextBoxWebServerPort.Text)
                { Owner = this };
                _modalDepth++;
                try { testWindow.ShowDialog(); }
                finally { _modalDepth--; }
            }
            else if (sender == ButtonTorusSetting)//TORUS 설정 모달 창 열기
            {
                var settingWindow = new Torus.MachineListWindow();
                settingWindow.Owner = this;
                _modalDepth++;
                try { settingWindow.ShowDialog(); }
                finally { _modalDepth--; }
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
            else if (sender == ButtonAddIP)//허용 IP 추가
            {
                var entry = TextBoxNewIP.Text.Trim();
                if (string.IsNullOrEmpty(entry)) return;
                if (!IsValidIpEntry(entry))
                {
                    MessageBox.Show("Invalid IP or CIDR format.\nExample: 192.168.1.10 or 192.168.1.0/24", "Error");
                    return;
                }
                if (!ListBoxAllowedIPs.Items.Contains(entry))
                    ListBoxAllowedIPs.Items.Add(entry);
                TextBoxNewIP.Clear();
                ApplyAllowlistIfRunning();
            }
            else if (sender == ButtonRemoveIP)//허용 IP 제거
            {
                if (ListBoxAllowedIPs.SelectedItem is string s)
                {
                    ListBoxAllowedIPs.Items.Remove(s);
                    ApplyAllowlistIfRunning();
                }
            }
            else if (sender == ButtonMinimizeToTray)//트레이로 보내기
            {
                SaveWindowSettings();
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
