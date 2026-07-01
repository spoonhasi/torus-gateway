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

        // TORUS 콘솔 임베드. 매니저 개수만큼 패널/호스트를 만들어 ConsoleHostGrid에 겹쳐 두고 Visibility로 전환.
        private readonly List<System.Windows.Forms.Panel> _consolePanels = new();
        private readonly List<System.Windows.Forms.Integration.WindowsFormsHost> _consoleHosts = new();
        private readonly ConsoleEmbedder _embedder = new();

        private const int MaxLogLines = 1000;
        // 미들웨어 스레드 → UI 스레드(flush)로 넘기는 요청 큐.
        private readonly ConcurrentQueue<RequestLogInfo> _logQueue = new();
        // 아래 패널: POST 및 GET 외 메서드 — 스크롤 목록.
        private readonly ObservableCollection<string> _logEntriesPost = new();
        // 위 패널(GET)은 정기(주기)/간헐로 다시 나뉜다.
        private readonly ObservableCollection<GetRegularRow> _getRegularRows = new(); // 정기 — in-place 갱신 표
        private readonly ObservableCollection<string> _getIntermittentRows = new();   // 간헐 — 스크롤 목록
        private readonly Dictionary<string, GetAggEntry> _getAgg = new();             // GET 시그니처별 집계(UI 스레드 전용)
        private readonly DispatcherTimer _logTimer;

        // ── GET 주기성 판정/집계 파라미터 (조정 가능) ──
        private const double EmaAlpha = 0.3;              // 도착 간격 EMA 가중치
        private const int RegularMinCount = 4;            // 정기 승격 최소 횟수(간격 3개 이상 확보)
        private const double RegularMaxPeriodMs = 10000;   // 이보다 느린 주기는 정기로 보지 않음
        private const double RegularMaxCv = 0.4;          // 변동계수(편차/평균)가 이하면 '주기적'
        private const double IdleFloorMs = 60000;         // 최소 유휴 제거 임계(60초)
        private const double IdleFactor = 5;              // 주기의 N배 동안 안 오면 제거
        private const int MaxAggKeys = 500;               // 집계 키 상한(초과 시 LRU 제거)

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
            ButtonNcExplorer.IsEnabled = false; // 웹서버 Open 시에만 활성화 (모든 기능이 HTTP 경유)

            // 이전 세션의 비정상 종료로 남은 임시 파일 디렉토리 정리.
            CustomController.CleanupOrphanedTempDirs();

            LoadSettings();

            ListViewGetRegular.ItemsSource = _getRegularRows;
            ListBoxGetIntermittent.ItemsSource = _getIntermittentRows;
            ListBoxApiLogPost.ItemsSource = _logEntriesPost;
            RequestLoggingMiddleware.RequestLogged += OnRequestLogged;

            // 시작 시 게이트웨이가 띄운 TORUS 콘솔 기록이 있으면 다시 임베드(재시작/재연결 흐름 대비).
            this.Loaded += MainWindow_Loaded;

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

        // bat의 활성 매니저 개수만큼 상단 탭 헤더(MainTabs)와 콘솔 호스트(ConsoleHostGrid)를 동적 생성한다.
        // 호스트는 모두 항상 트리에 두고 Visibility로만 전환 → 탭을 바꿔도 콘솔 HWND가 파괴되지 않는다.
        private void BuildConsoleTabs(IReadOnlyList<string> managerNames)
        {
            // 기존 콘솔 탭 헤더 제거 (index 0 = "API Log"는 유지).
            while (MainTabs.Items.Count > 1)
                MainTabs.Items.RemoveAt(MainTabs.Items.Count - 1);
            // 기존 호스트 정리(이 시점엔 임베드된 콘솔이 없음 — 호출부에서 보장).
            foreach (var h in _consoleHosts)
            {
                try { h.Dispose(); } catch { }
            }
            ConsoleHostGrid.Children.Clear();
            _consoleHosts.Clear();
            _consolePanels.Clear();

            for (int i = 0; i < managerNames.Count; i++)
            {
                var panel = MakeConsolePanel();
                var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = panel };
                ConsoleHostGrid.Children.Add(host);
                _consoleHosts.Add(host);
                _consolePanels.Add(panel);

                MainTabs.Items.Add(new System.Windows.Controls.TabItem { Header = FriendlyName(managerNames[i]) });

                int idx = i;
                panel.SizeChanged += (_, _) => _embedder.OnHostResized(idx, panel);
            }

            // 모든 호스트 핸들을 미리 생성해 임베드 대상(panel.Handle)이 유효하도록 한다.
            if (managerNames.Count > 0)
            {
                ConsoleHostGrid.Visibility = Visibility.Visible;
                UpdateLayout();
                foreach (var p in _consolePanels) { try { _ = p.Handle; } catch { } }
            }
            UpdateContentVisibility();
        }

        private static System.Windows.Forms.Panel MakeConsolePanel() => new()
        {
            BackColor = System.Drawing.Color.Black,
            Dock = System.Windows.Forms.DockStyle.Fill
        };

        // 탭 라벨: exe 이름에서 유도. mgrApplication.exe → "Application", mgrCommand.exe → "Command".
        private static string FriendlyName(string exeName)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(exeName);
            if (n.StartsWith("mgr", StringComparison.OrdinalIgnoreCase) && n.Length > 3)
                n = n.Substring(3);
            return string.IsNullOrEmpty(n) ? exeName : n;
        }

        private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.OriginalSource is not System.Windows.Controls.TabControl) return; // 하위 컨트롤 이벤트 무시
            UpdateContentVisibility();
        }

        // 선택된 탭에 따라 로그/콘솔 표시 전환. 콘텐츠는 항상 트리에 있고 Visibility만 바꾼다(HWND 보존).
        private void UpdateContentVisibility()
        {
            if (LogArea == null || ConsoleHostGrid == null) return; // InitializeComponent 중 호출 대비
            int idx = MainTabs.SelectedIndex; // 0 = API Log, 1.. = 콘솔
            bool consoleSelected = idx >= 1 && idx - 1 < _consoleHosts.Count;
            LogArea.Visibility = consoleSelected ? Visibility.Collapsed : Visibility.Visible;
            ConsoleHostGrid.Visibility = consoleSelected ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < _consoleHosts.Count; i++)
                _consoleHosts[i].Visibility = (consoleSelected && i == idx - 1) ? Visibility.Visible : Visibility.Collapsed;

            // 콘솔이 막 보이게 됐으면 강제로 다시 그린다 — Visibility 토글만으론 WM_PAINT가 안 가
            // 이전에 그린 영역만 남는(빈/잔상) 문제를 막는다. 뷰포트 1회 렌더라 누적량과 무관하게 저렴.
            if (consoleSelected)
                _embedder.ForceRedraw(idx - 1);
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // 콘솔 임베드 기능은 아직 다듬는 중이라 시작 시 자동 재부착을 비활성화한다.
            // Run은 예전처럼 bat 외부 실행만 하므로 임베드/재부착이 일어나지 않는다. (기능 완성 시 아래 복원)
            return;
#pragma warning disable CS0162 // 의도적으로 비활성화된 재부착 경로(기능 완성 시 복원)
            // 게이트웨이가 띄운 콘솔 기록이 있을 때만 재부착 시도(사용자가 직접 띄운 TORUS는 건드리지 않음).
            if (!_embedder.HasReattachableSession()) return;
            string runPath = ConfigManager.Read(ConfigFile, "Main", "TorusRunPath", "");
            var names = _embedder.GetManagerNames(runPath);
            if (names.Count == 0) return;

            BuildConsoleTabs(names);
            if (_embedder.TryReattach(runPath, _consolePanels))
                MainTabs.SelectedIndex = 1; // 첫 콘솔 탭으로
            else
                BuildConsoleTabs(Array.Empty<string>()); // 재부착할 게 없으면 빈 콘솔 탭 제거
#pragma warning restore CS0162
        }

        // TORUS Settings의 Run 버튼 → bat 개수만큼 탭 생성 후 매니저를 conhost로 띄워 임베드.
        private async void OnTorusRunRequested(object? sender, EventArgs e)
        {
            // 이미 임베드돼 있으면 탭 재생성 없이 콘솔 탭으로만 전환(재생성은 기존 콘솔을 파괴함).
            if (_embedder.HasEmbedded)
            {
                if (MainTabs.Items.Count > 1) MainTabs.SelectedIndex = 1;
                return;
            }

            string runPath = ConfigManager.Read(ConfigFile, "Main", "TorusRunPath", "");
            var names = _embedder.GetManagerNames(runPath);
            if (names.Count == 0)
            {
                MessageBox.Show(this, "No managers found in the TORUS run script.",
                    "TORUS Console", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BuildConsoleTabs(names);
            int embedded = await _embedder.LaunchAndEmbedAsync(runPath, _consolePanels,
                msg => MessageBox.Show(this, msg, "TORUS Console", MessageBoxButton.OK, MessageBoxImage.Warning));
            if (embedded > 0)
                MainTabs.SelectedIndex = 1; // 첫 콘솔 탭으로
            else
                BuildConsoleTabs(Array.Empty<string>()); // 임베드 못 했으면 빈 콘솔 탭 제거
        }

        // TORUS Settings의 Exit 버튼 → 매니저(콘솔) 종료 + 콘솔 탭 제거.
        private void OnTorusExitRequested(object? sender, EventArgs e)
        {
            string runPath = ConfigManager.Read(ConfigFile, "Main", "TorusRunPath", "");
            _embedder.ExitManagers(runPath);
            BuildConsoleTabs(Array.Empty<string>());
            MainTabs.SelectedIndex = 0;
        }

        // 미들웨어 스레드에서 호출 — ConcurrentQueue로 lock 없이 적재. 집계는 UI 스레드 flush에서 처리.
        private void OnRequestLogged(RequestLogInfo info)
        {
            _logQueue.Enqueue(info);
        }

        // UI 스레드에서 200ms마다 큐를 비워 GET(정기/간헐)·POST 패널에 반영하고, 유휴 GET 집계를 정리한다.
        private void FlushLogQueue(object? sender, EventArgs e)
        {
            bool addedIntermittent = false, addedPost = false;
            while (_logQueue.TryDequeue(out var info))
            {
                string line = FormatLogLine(info);
                if (string.Equals(info.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (ProcessGet(info, line)) addedIntermittent = true;
                }
                else
                {
                    _logEntriesPost.Add(line);
                    addedPost = true;
                }
            }

            EvictIdleGetAgg();

            TrimAndAutoScroll(_getIntermittentRows, ListBoxGetIntermittent, addedIntermittent);
            TrimAndAutoScroll(_logEntriesPost, ListBoxApiLogPost, addedPost);
        }

        // 파일 로그와 동일한 본문 앞에 시각을 붙인 UI 표시 한 줄. (IP·메서드·경로+쿼리·상태·소요)
        private static string FormatLogLine(RequestLogInfo info)
        {
            string body = $"{info.ClientIp,-15} {info.Method,-6} {info.PathAndQuery,-40} {info.StatusCode}  {info.ElapsedMs}ms";
            return $"[{info.TimestampLocal:HH:mm:ss}] {body}";
        }

        // GET 한 건 집계. 시그니처는 'IP + 경로+쿼리'(입력 그대로, 대소문자 구분).
        // 도착 간격의 규칙성(변동계수)이 작고 충분히 반복되면 '정기'로 승격해 표를 in-place 갱신,
        // 아니면 '간헐' 목록에 한 줄 추가한다. 반환: 간헐 목록에 줄을 추가했으면 true.
        private bool ProcessGet(RequestLogInfo info, string line)
        {
            string key = info.ClientIp + " " + info.PathAndQuery;
            DateTime now = info.TimestampLocal;
            // /health는 조회 데이터와 무관한 정적 헬스 체크이므로, 1회만 와도 무조건 정기로 취급한다.
            bool isHealth = IsHealthPath(info.PathAndQuery);

            if (_getAgg.TryGetValue(key, out var entry))
            {
                double interval = (now - entry.LastSeen).TotalMilliseconds;
                if (interval < 0) interval = 0;
                if (entry.Count == 1)
                {
                    entry.EmaInterval = interval;
                    entry.EmaDev = 0;
                }
                else
                {
                    double dev = Math.Abs(interval - entry.EmaInterval);
                    entry.EmaInterval = EmaAlpha * interval + (1 - EmaAlpha) * entry.EmaInterval;
                    entry.EmaDev = EmaAlpha * dev + (1 - EmaAlpha) * entry.EmaDev;
                }
                entry.Count++;
                entry.LastSeen = now;
                entry.LastStatus = info.StatusCode;

                double cv = entry.EmaInterval > 0 ? entry.EmaDev / entry.EmaInterval : double.PositiveInfinity;
                bool regular = isHealth
                               || (entry.Count >= RegularMinCount
                                   && entry.EmaInterval <= RegularMaxPeriodMs
                                   && cv <= RegularMaxCv);

                if (regular)
                {
                    if (entry.Row == null)
                    {
                        entry.Row = new GetRegularRow(info.ClientIp, info.PathAndQuery);
                        _getRegularRows.Add(entry.Row);
                    }
                    entry.Row.Update(entry.Count, entry.EmaInterval, now, info.StatusCode);
                    return false; // 정기는 스크롤 줄을 쌓지 않음
                }

                _getIntermittentRows.Add(line); // 아직 간헐
                return true;
            }

            // 처음 본 요청
            if (_getAgg.Count >= MaxAggKeys) EvictOldestGetAgg();
            var fresh = new GetAggEntry { Count = 1, LastSeen = now, LastStatus = info.StatusCode };
            _getAgg[key] = fresh;

            if (isHealth)
            {
                // /health는 첫 요청부터 바로 정기 표에 표시(간헐로 안 보냄).
                fresh.Row = new GetRegularRow(info.ClientIp, info.PathAndQuery);
                _getRegularRows.Add(fresh.Row);
                fresh.Row.Update(fresh.Count, fresh.EmaInterval, now, info.StatusCode);
                return false;
            }

            _getIntermittentRows.Add(line);
            return true;
        }

        // /health 경로 여부. 라우팅이 대소문자 무관이므로 경로 비교도 대소문자 무관으로 한다.
        private static bool IsHealthPath(string pathAndQuery)
        {
            int q = pathAndQuery.IndexOf('?');
            string path = q >= 0 ? pathAndQuery.Substring(0, q) : pathAndQuery;
            return string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase);
        }

        // 한동안 안 온 GET 집계를 제거(정기 표 행도 함께) → '현재 살아있는 폴러'만 유지.
        private void EvictIdleGetAgg()
        {
            if (_getAgg.Count == 0) return;
            DateTime now = DateTime.Now;
            List<string>? toRemove = null;
            foreach (var kv in _getAgg)
            {
                double period = kv.Value.EmaInterval > 0 ? kv.Value.EmaInterval : IdleFloorMs;
                double threshold = Math.Max(IdleFloorMs, IdleFactor * period);
                if ((now - kv.Value.LastSeen).TotalMilliseconds > threshold)
                    (toRemove ??= new()).Add(kv.Key);
            }
            if (toRemove == null) return;
            foreach (var key in toRemove)
            {
                if (_getAgg.TryGetValue(key, out var e) && e.Row != null)
                    _getRegularRows.Remove(e.Row);
                _getAgg.Remove(key);
            }
        }

        // 집계 키 상한 초과 시 가장 오래 안 본 항목 제거(LRU).
        private void EvictOldestGetAgg()
        {
            string? oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kv in _getAgg)
                if (kv.Value.LastSeen < oldest) { oldest = kv.Value.LastSeen; oldestKey = kv.Key; }
            if (oldestKey == null) return;
            if (_getAgg.TryGetValue(oldestKey, out var e) && e.Row != null)
                _getRegularRows.Remove(e.Row);
            _getAgg.Remove(oldestKey);
        }

        // 최대 줄 수로 자르고, 사용자가 맨 아래에 있을 때만 자동 스크롤한다
        // (위로 스크롤해서 과거 로그 확인 중이면 방해하지 않음).
        private void TrimAndAutoScroll(ObservableCollection<string> entries, System.Windows.Controls.ListBox listBox, bool added)
        {
            while (entries.Count > MaxLogLines)
                entries.RemoveAt(0);

            if (added && entries.Count > 0 && IsScrolledToBottom(listBox))
                listBox.ScrollIntoView(entries[^1]);
        }

        // GET 시그니처별 집계 상태 (UI 스레드 전용, 락 불필요).
        private sealed class GetAggEntry
        {
            public int Count;
            public DateTime LastSeen;
            public int LastStatus;
            public double EmaInterval;   // 도착 간격 평균(ms)
            public double EmaDev;        // 도착 간격 편차 평균(ms)
            public GetRegularRow? Row;   // 정기 승격 시 연결되는 표 행
        }

        // 정기(주기) GET 표의 한 행. 횟수/주기/마지막/상태가 in-place로 갱신된다.
        private sealed class GetRegularRow : System.ComponentModel.INotifyPropertyChanged
        {
            public string Ip { get; }
            public string Request { get; }
            private int _count;
            private string _period = "";
            private string _last = "";
            private int _status;

            public GetRegularRow(string ip, string request) { Ip = ip; Request = request; }

            public int Count { get => _count; private set { _count = value; OnChanged(nameof(Count)); } }
            public string Period { get => _period; private set { _period = value; OnChanged(nameof(Period)); } }
            public string Last { get => _last; private set { _last = value; OnChanged(nameof(Last)); } }
            public int Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

            // 평균 주기를 보기 좋은 단위(ms/s)로 표시.
            public void Update(int count, double periodMs, DateTime last, int status)
            {
                Count = count;
                // 아직 도착 간격이 없으면(예: /health 첫 요청) 주기를 '-'로 표시.
                Period = periodMs <= 0 ? "-"
                       : periodMs >= 1000 ? $"{periodMs / 1000.0:0.0}s"
                       : $"{periodMs:0}ms";
                Last = last.ToString("HH:mm:ss");
                Status = status;
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string name) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
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
            // 게이트웨이가 띄운(임베드한) TORUS가 있으면 함께 종료. (사용자가 직접 띄운 것은 추적되지 않아 안 건드림)
            _embedder.KillEmbeddedManagers();
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
            // 임베드된 콘솔을 부모에서 떼어 둔다(재시작된 프로세스가 다시 재부착).
            _embedder.DetachAllForShutdown();
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
                ButtonNcExplorer.IsEnabled = false; // 웹서버 종료 시 탐색기도 비활성화
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
                ButtonNcExplorer.IsEnabled = true; // 웹서버 가동 중에만 NC 탐색기 사용 가능 (HTTP 경유)
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
            else if (sender == ButtonNcExplorer)//NC 파일 탐색기 모달 창 열기 (HTTP 경유, Host/Port 전달)
            {
                var explorerWindow = new NcExplorerWindow(TextBoxWebServerHost.Text, TextBoxWebServerPort.Text)
                { Owner = this };
                _modalDepth++;
                try { explorerWindow.ShowDialog(); }
                finally { _modalDepth--; }
            }
            else if (sender == ButtonTorusSetting)//TORUS 설정 모달 창 열기
            {
                var settingWindow = new Torus.MachineListWindow();
                settingWindow.Owner = this;
                // Run/Exit 버튼은 콘솔 임베드를 호스트(MainWindow)에 위임 → 이벤트 구독.
                settingWindow.MachineListControlInstance.TorusRunRequested += OnTorusRunRequested;
                settingWindow.MachineListControlInstance.TorusExitRequested += OnTorusExitRequested;
                _modalDepth++;
                try { settingWindow.ShowDialog(); }
                finally
                {
                    _modalDepth--;
                    settingWindow.MachineListControlInstance.TorusRunRequested -= OnTorusRunRequested;
                    settingWindow.MachineListControlInstance.TorusExitRequested -= OnTorusExitRequested;
                }
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
