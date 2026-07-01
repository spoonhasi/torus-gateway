using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WinFormsPanel = System.Windows.Forms.Panel;

namespace TorusGateway.Torus
{
    // TORUS 매니저 콘솔들을 시스템 conhost로 띄워 그 콘솔 창을 WPF 안 WinForms 패널에 SetParent로 임베드한다.
    // 임베드 대상/개수는 Platform_Run.bat의 활성 start 줄(주석 제외)에서 동적으로 결정된다(슬롯 = 0-based 인덱스).
    //
    // Windows 11 기본 터미널이 Windows Terminal(의사콘솔)이면 그대로는 재부모지정이 안 되므로,
    // 매니저를 항상 conhost.exe를 거쳐 실행해 "고전 콘솔 창"(ConsoleWindowClass)을 강제한다.
    //
    // 매니저는 게이트웨이보다 오래 살아야 한다(--auto-connect 재시작 흐름). 따라서:
    //  - Job Object를 쓰지 않고(부모와 함께 죽지 않게),
    //  - 종료/재시작 직전에는 DetachAllForShutdown()으로 부모를 떼어 콘솔이 함께 파괴되는 것을 막고,
    //  - 시작 시 TryReattach()로 이미 떠 있는 매니저 콘솔에 다시 붙는다.
    internal sealed class ConsoleEmbedder
    {
        private const string ConsoleWindowClass = "ConsoleWindowClass";
        private const int CaptureTimeoutMs = 15000;  // 콘솔 창 HWND 캡처 최대 대기(TORUS 초기화 부하로 지연 가능)
        private const int CapturePollMs = 50;
        private const int MaxConsoles = 8;            // 안전 상한(매니저 개수는 bat에서 동적 결정)

        // 슬롯별 상태. Host는 우리가 띄운 conhost(재부착 슬롯은 null).
        private sealed class SlotState
        {
            public string ExeName = "";          // mgrXxx.exe (파일명만)
            public Process? Host;                // 우리가 시작한 conhost (재부착 시 null)
            public uint ConhostPid;              // 콘솔 창을 소유한 conhost PID (세션 추적용)
            public IntPtr Hwnd = IntPtr.Zero;    // ConsoleWindowClass 최상위 창
            public WinFormsPanel? Panel;
            public bool Embedded;
        }

        // 우리가 띄운 conhost PID 목록을 저장하는 파일. 재시작/재실행 시 "게이트웨이가 띄운"
        // 콘솔만 재부착하기 위한 식별자(사용자가 직접 띄운 TORUS는 절대 건드리지 않음).
        private static string SessionFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "embedSession.txt");

        private readonly Dictionary<int, SlotState> _slots = new(); // 키 = 슬롯 인덱스(0-based)

        public bool HasEmbedded => _slots.Values.Any(s => s.Embedded);

        // bat의 활성 start 줄에서 매니저 exe 파일명을 순서대로 추출(최대 MaxConsoles개).
        // MainWindow가 이 개수만큼 콘솔 탭을 동적 생성하는 데 쓴다. 주석(rem/::) 줄은 파서가 제외.
        public IReadOnlyList<string> GetManagerNames(string torusRunPath)
        {
            if (string.IsNullOrWhiteSpace(torusRunPath) || !File.Exists(torusRunPath))
                return Array.Empty<string>();
            try
            {
                return ParseStartTargets(File.ReadAllText(torusRunPath))
                    .Select(e => Path.GetFileName(e) ?? "")
                    .Where(n => n.Length > 0)
                    .Take(MaxConsoles)
                    .ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        // 게이트웨이가 띄운 콘솔 기록(세션 파일)이 있는지. 시작 시 재부착 시도 여부 판단용.
        public bool HasReattachableSession() => LoadSessionPids().Count > 0;

        // ── 순수 헬퍼: Platform_Run.bat에서 start로 실행되는 대상(exe) 목록을 순서대로 추출 ──
        // 예: start "TORUS Application Manager" /MIN mgrApplication.exe → mgrApplication.exe
        // 규칙: 첫 토큰이 start(대소문자 무관)인 줄만, 첫 따옴표 "제목"은 1회 제거, /스위치 제거,
        //       마지막 비스위치 토큰을 대상으로 채택. echo/rem/:: 및 비-start 줄은 무시.
        internal static IReadOnlyList<string> ParseStartTargets(string batContent)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(batContent)) return result;

            foreach (var rawLine in batContent.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                var tokens = TokenizeRespectingQuotes(line);
                if (tokens.Count == 0) continue;

                // @echo 처럼 앞에 @가 붙는 경우까지 고려해 첫 토큰을 정규화.
                string first = tokens[0].TrimStart('@');
                if (!first.Equals("start", StringComparison.OrdinalIgnoreCase)) continue;

                string? target = null;
                bool titleSkipped = false;
                for (int i = 1; i < tokens.Count; i++)
                {
                    string tok = tokens[i];
                    bool quoted = tok.Length >= 2 && tok[0] == '"' && tok[^1] == '"';
                    string unq = quoted ? tok.Substring(1, tok.Length - 2) : tok;

                    // 첫 따옴표 토큰은 창 제목으로 간주(1회만). 단 그게 .exe면 대상일 수 있으니 예외.
                    if (quoted && !titleSkipped && !unq.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        titleSkipped = true;
                        continue;
                    }
                    // /MIN, /WAIT, /B 등 스위치 제거.
                    if (!quoted && unq.StartsWith("/")) continue;

                    target = unq; // 마지막 비스위치 토큰이 최종 대상.
                }

                if (!string.IsNullOrWhiteSpace(target))
                    result.Add(target!);
            }
            return result;
        }

        // 공백으로 토큰화하되 따옴표로 감싼 구간은 한 토큰으로 유지(따옴표 문자 포함).
        private static List<string> TokenizeRespectingQuotes(string line)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            bool hasContent = false;
            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    hasContent = true;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (hasContent) { tokens.Add(sb.ToString()); sb.Clear(); hasContent = false; }
                }
                else { sb.Append(c); hasContent = true; }
            }
            if (hasContent) tokens.Add(sb.ToString());
            return tokens;
        }

        // ── Run 버튼: 매니저들을 conhost로 띄워 패널에 임베드 ──
        // 반환: 임베드된 콘솔 수. 경로 문제/실패는 onError로 보고하고 가능한 만큼 진행.
        public async Task<int> LaunchAndEmbedAsync(
            string torusRunPath,
            IReadOnlyList<WinFormsPanel> panels,
            Action<string>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(torusRunPath) || !File.Exists(torusRunPath))
            {
                onError?.Invoke("TORUS run path is not set or the file does not exist.");
                return 0;
            }

            // 이미 떠 있는 매니저가 있으면 새로 띄우지 않고 거기에 붙인다(중복 실행 방지).
            if (TryReattach(torusRunPath, panels))
                return _slots.Values.Count(s => s.Embedded);

            string[] exes;
            try { exes = ParseStartTargets(File.ReadAllText(torusRunPath)).ToArray(); }
            catch (Exception ex) { onError?.Invoke($"Failed to read run script: {ex.Message}"); return 0; }

            if (exes.Length == 0)
            {
                onError?.Invoke("No executables found in the TORUS run script.");
                return 0;
            }

            int count = Math.Min(exes.Length, Math.Min(panels.Count, MaxConsoles));

            // 외부에서 이미 실행 중인 매니저가 하나라도 있으면 중복 실행하지 않는다.
            // (게이트웨이가 띄운 것은 위 TryReattach에서 이미 붙였으므로, 여기 남은 건 '외부 실행'.)
            var alreadyRunning = new List<string>();
            for (int i = 0; i < count; i++)
                if (IsManagerRunning(exes[i]))
                    alreadyRunning.Add(exes[i]);
            if (alreadyRunning.Count > 0)
            {
                onError?.Invoke(
                    "TORUS is already running outside the gateway (" + string.Join(", ", alreadyRunning) +
                    "). The gateway did not start a duplicate.\n" +
                    "To embed it in this window, click 'Exit Torus' first, then Run.");
                return 0;
            }

            string binaryDir = Path.GetDirectoryName(torusRunPath) ?? "";
            string conhost = ResolveConhostPath();
            if (!File.Exists(conhost))
            {
                onError?.Invoke($"conhost.exe not found at: {conhost}");
                return 0;
            }

            // 1단계: 매니저 conhost들을 먼저 모두 띄운다.
            // 병렬 기동이라 뒤 슬롯이 앞 슬롯의 캡처 완료를 기다리지 않는다(순차 캡처 시 3번째가 타임아웃 나던 문제 해소).
            var launched = new List<(int Slot, string ExeName, Process Host, WinFormsPanel Panel)>();
            for (int i = 0; i < count; i++)
            {
                int slot = i;
                var panel = panels[i];

                // 해당 슬롯이 이미 살아있으면 건너뛴다.
                if (_slots.TryGetValue(slot, out var existing) && existing.Embedded && IsWindow(existing.Hwnd))
                    continue;

                string exeName = exes[i];
                string exeFull = Path.GetFullPath(Path.Combine(binaryDir, exeName));
                if (!File.Exists(exeFull))
                {
                    onError?.Invoke($"Manager not found: {exeName}");
                    continue;
                }

                Process? host;
                try
                {
                    host = Process.Start(new ProcessStartInfo
                    {
                        FileName = conhost,
                        Arguments = "\"" + exeFull + "\"",
                        WorkingDirectory = binaryDir,
                        UseShellExecute = false,          // conhost를 직접 띄워 고전 콘솔 강제 + PID 추적
                        WindowStyle = ProcessWindowStyle.Hidden, // 초기 깜빡임 최소화(캡처 후 다시 숨김)
                    });
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Failed to launch {exeName}: {ex.Message}");
                    continue;
                }
                if (host == null)
                {
                    onError?.Invoke($"Failed to launch {exeName}.");
                    continue;
                }

                launched.Add((slot, exeName, host, panel));
            }

            // 2단계: 공유 데드라인으로 모든 콘솔 창을 폴링 캡처한다.
            // 콘솔은 "conhost의 자식 프로세스 이름"으로 매칭한다(host.Id가 콘솔을 직접 소유하지 않는
            // 매니저도 잡기 위함 — mgrCommand가 이 경우였다). 소유 conhost PID는 창에서 되읽어 기록한다
            // (재부착 세션 일관성). 매니저 기동/초기화 부하로 지연될 수 있어 CaptureTimeoutMs만큼 대기.
            int embedded = 0;
            var pending = new List<(int Slot, string ExeName, Process Host, WinFormsPanel Panel)>(launched);
            int waited = 0;
            while (pending.Count > 0)
            {
                var consoles = FindExistingManagerConsoles(pending.Select(p => p.ExeName), null);
                for (int k = pending.Count - 1; k >= 0; k--)
                {
                    var li = pending[k];
                    string key = (Path.GetFileName(li.ExeName) ?? "").ToLowerInvariant();
                    if (!consoles.TryGetValue(key, out var hwnd) || !IsWindow(hwnd)) continue;

                    GetWindowThreadProcessId(hwnd, out uint conhostPid); // 실제 콘솔 소유 conhost
                    Embed(hwnd, li.Panel);
                    _slots[li.Slot] = new SlotState { ExeName = li.ExeName, Host = li.Host, ConhostPid = conhostPid, Hwnd = hwnd, Panel = li.Panel, Embedded = true };
                    embedded++;
                    pending.RemoveAt(k);
                }
                if (pending.Count == 0 || waited >= CaptureTimeoutMs) break;
                await Task.Delay(CapturePollMs);
                waited += CapturePollMs;
            }

            foreach (var li in pending)
                onError?.Invoke($"Could not capture console window for {li.ExeName}.");

            if (embedded > 0) SaveSession();
            return embedded;
        }

        // ── 시작 시 재부착: 이미 떠 있는 매니저 콘솔을 찾아 다시 임베드 ──
        public bool TryReattach(string torusRunPath, IReadOnlyList<WinFormsPanel> panels)
        {
            if (string.IsNullOrWhiteSpace(torusRunPath) || !File.Exists(torusRunPath)) return false;

            // 핵심: "게이트웨이가 직접 띄운" conhost PID만 재부착 대상으로 삼는다.
            // 세션 파일에 기록이 없으면(= 우리가 띄운 적 없음) 아무것도 건드리지 않는다.
            // → 사용자가 직접 실행해 둔 TORUS는 절대 임베드로 빨려들어가지 않는다.
            var allowed = LoadSessionPids();
            if (allowed.Count == 0) return false;

            string[] exes;
            try { exes = ParseStartTargets(File.ReadAllText(torusRunPath)).ToArray(); }
            catch { return false; }
            if (exes.Length == 0) return false;

            var existing = FindExistingManagerConsoles(exes, allowed); // exeName(소문자) → 콘솔 HWND
            if (existing.Count == 0) return false;

            int count = Math.Min(exes.Length, Math.Min(panels.Count, MaxConsoles));
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                int slot = i;
                var panel = panels[i];

                // 해당 슬롯이 이미 임베드되어 있으면 유지.
                if (_slots.TryGetValue(slot, out var cur) && cur.Embedded && IsWindow(cur.Hwnd))
                {
                    any = true;
                    continue;
                }

                string key = (Path.GetFileName(exes[i]) ?? "").ToLowerInvariant();
                if (!existing.TryGetValue(key, out var hwnd) || !IsWindow(hwnd)) continue;

                GetWindowThreadProcessId(hwnd, out uint conhostPid);
                Embed(hwnd, panel);
                _slots[slot] = new SlotState { ExeName = exes[i], Host = null, ConhostPid = conhostPid, Hwnd = hwnd, Panel = panel, Embedded = true };
                any = true;
            }
            if (any) SaveSession(); // 살아있는 집합으로 갱신(죽은 PID 정리)
            return any;
        }

        // ── Exit 버튼: 매니저(콘솔) 종료 + 패널 비움 ──
        public void ExitManagers(string torusRunPath)
        {
            // 1) 우리가 띄운 conhost 트리(자식 mgr 포함) 종료.
            foreach (var s in _slots.Values)
            {
                try { if (s.Host is { HasExited: false }) s.Host.Kill(entireProcessTree: true); }
                catch { }
            }
            // 2) 재부착 슬롯(Host 없음): 콘솔 HWND → conhost PID 트리 종료.
            foreach (var s in _slots.Values)
            {
                if (s.Host != null) continue;
                if (s.Hwnd != IntPtr.Zero && IsWindow(s.Hwnd))
                {
                    GetWindowThreadProcessId(s.Hwnd, out uint conhostPid);
                    TryKillTree(conhostPid);
                }
            }
            // 3) 폴백: run 스크립트에서 파싱한 이름으로 남은 매니저 종료.
            try
            {
                if (!string.IsNullOrWhiteSpace(torusRunPath) && File.Exists(torusRunPath))
                    foreach (var exe in ParseStartTargets(File.ReadAllText(torusRunPath)))
                        KillByImageName(exe);
            }
            catch { }

            DisposeHosts();
            _slots.Clear();
            ClearSession(); // 우리가 띄운 기록 제거 → 더 이상 재부착 대상 없음.
        }

        // ── 게이트웨이 종료 시: 게이트웨이가 띄운(임베드한) 매니저만 종료 ──
        // ExitManagers와 달리 이름 기반 광역 종료를 하지 않으므로, 사용자가 직접 띄운 TORUS는 건드리지 않는다.
        public void KillEmbeddedManagers()
        {
            // 우리가 직접 띄운 conhost 트리.
            foreach (var s in _slots.Values)
            {
                try { if (s.Host is { HasExited: false }) s.Host.Kill(entireProcessTree: true); }
                catch { }
            }
            // 재부착 슬롯(Host 없음): 추적해 둔 conhost PID 트리.
            foreach (var s in _slots.Values)
            {
                if (s.Host != null) continue;
                uint pid = s.ConhostPid;
                if (pid == 0 && s.Hwnd != IntPtr.Zero && IsWindow(s.Hwnd))
                    GetWindowThreadProcessId(s.Hwnd, out pid);
                TryKillTree(pid);
            }
            DisposeHosts();
            _slots.Clear();
            ClearSession();
        }

        // 패널 크기 변경 시 임베드된 콘솔 창을 패널에 맞춤.
        public void OnHostResized(int slot, WinFormsPanel panel)
        {
            if (_slots.TryGetValue(slot, out var s) && s.Embedded && IsWindow(s.Hwnd))
            {
                var size = panel.ClientSize;
                if (size.Width > 0 && size.Height > 0)
                    MoveWindow(s.Hwnd, 0, 0, size.Width, size.Height, true);
            }
        }

        // 콘솔 탭이 화면에 다시 보이게 될 때 1회 호출 — conhost가 뷰포트 전체를 다시 그리게 강제한다.
        // conhost는 WM_PAINT 무효화(RedrawWindow)만으론 클라이언트 영역을 다시 안 그리고, 크기 변경(WM_SIZE)에
        // 반응해 다시 그린다(창 리사이즈/스크롤로만 갱신되던 원인). 그래서 패널 크기로 1px 줄였다 되돌려
        // 리사이즈-리페인트를 강제한다. 비용은 뷰포트 렌더 1회로 스크롤백 누적량과 무관하게 상수다.
        public void ForceRedraw(int slot)
        {
            if (!_slots.TryGetValue(slot, out var s) || !s.Embedded || !IsWindow(s.Hwnd)) return;

            if (s.Panel != null)
            {
                var size = s.Panel.ClientSize;
                if (size.Width > 1 && size.Height > 1)
                {
                    MoveWindow(s.Hwnd, 0, 0, size.Width - 1, size.Height, false);
                    MoveWindow(s.Hwnd, 0, 0, size.Width, size.Height, true);
                }
            }
            RedrawWindow(s.Hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        }

        // ── 종료/재시작 직전: 콘솔을 부모에서 떼어 우리 HWND와 함께 파괴되지 않게 한다 ──
        // 매니저 프로세스는 살려두고(다음 실행에서 재부착), 창은 숨겨 작업표시줄에 다시 뜨지 않게 한다.
        public void DetachAllForShutdown()
        {
            foreach (var s in _slots.Values)
            {
                if (s.Hwnd != IntPtr.Zero && IsWindow(s.Hwnd))
                {
                    try
                    {
                        SetParent(s.Hwnd, IntPtr.Zero);
                        int style = GetWindowLong(s.Hwnd, GWL_STYLE);
                        style &= ~WS_CHILD;
                        style |= WS_OVERLAPPEDWINDOW;
                        SetWindowLong(s.Hwnd, GWL_STYLE, style);
                        ShowWindow(s.Hwnd, SW_HIDE);
                    }
                    catch { }
                }
                s.Embedded = false;
            }
        }

        // ── 내부 구현 ──

        // 매니저 콘솔을 (exe 이름 → 콘솔 HWND)로 매핑.
        // 콘솔 창은 conhost가 소유하고, 매니저 exe는 그 conhost의 자식이다.
        // 이 "자식 프로세스 이름" 기준 매칭은, 우리가 직접 띄운 conhost(host.Id)가 콘솔을 직접
        // 소유하지 않는 매니저(재호스팅으로 소유 conhost PID가 달라지는 경우)도 견고하게 찾는다.
        // allowedConhostPids != null이면 그 conhost(= 우리가 띄운 것)만 대상으로 좁힌다(재부착용).
        // null이면 이름만으로 매칭한다(신규 실행 캡처용 — 외부 인스턴스는 중복 실행 가드로 이미 배제됨).
        private static Dictionary<string, IntPtr> FindExistingManagerConsoles(
            IEnumerable<string> exeNames, HashSet<uint>? allowedConhostPids)
        {
            // 프로세스 이미지명과 비교하므로 파일명만 소문자로 추린다(경로/중복 제거).
            var wanted = new HashSet<string>(exeNames.Select(n => (Path.GetFileName(n) ?? "").ToLowerInvariant()));
            var result = new Dictionary<string, IntPtr>();
            if (wanted.Count == 0) return result;

            var procs = SnapshotProcesses(); // pid → (이름소문자, 부모pid)

            // ConsoleWindowClass 창을 conhost PID로 수집. allowed가 주어지면 그 집합으로 제한.
            var consoleByPid = new Dictionary<uint, IntPtr>();
            EnumWindows((h, _) =>
            {
                if (GetClassNameStr(h) == ConsoleWindowClass)
                {
                    GetWindowThreadProcessId(h, out uint wp);
                    if (wp != 0 && (allowedConhostPids == null || allowedConhostPids.Contains(wp))
                        && !consoleByPid.ContainsKey(wp))
                        consoleByPid[wp] = h;
                }
                return true;
            }, IntPtr.Zero);

            // 각 conhost의 자식 중 원하는 매니저가 있으면 그 콘솔을 해당 이름에 매핑.
            foreach (var (conhostPid, hwnd) in consoleByPid)
            {
                foreach (var p in procs.Values)
                {
                    if (p.ParentPid == conhostPid && wanted.Contains(p.NameLower) && !result.ContainsKey(p.NameLower))
                        result[p.NameLower] = hwnd;
                }
            }
            return result;
        }

        private readonly struct ProcInfo
        {
            public ProcInfo(string nameLower, uint parentPid) { NameLower = nameLower; ParentPid = parentPid; }
            public string NameLower { get; }
            public uint ParentPid { get; }
        }

        // Toolhelp 스냅샷으로 pid → (이미지명 소문자, 부모 pid) 맵 구성. (WMI 의존 회피)
        private static Dictionary<uint, ProcInfo> SnapshotProcesses()
        {
            var map = new Dictionary<uint, ProcInfo>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) return map;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snap, ref entry))
                {
                    do
                    {
                        map[entry.th32ProcessID] = new ProcInfo(
                            (entry.szExeFile ?? "").ToLowerInvariant(), entry.th32ParentProcessID);
                    } while (Process32NextW(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // 콘솔 창을 패널의 자식으로 만들고 패널을 꽉 채운다.
        private static void Embed(IntPtr hwnd, WinFormsPanel panel)
        {
            ShowWindow(hwnd, SW_HIDE);

            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME);
            style |= WS_CHILD;
            SetWindowLong(hwnd, GWL_STYLE, style);

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex &= ~WS_EX_APPWINDOW; // 작업표시줄 버튼 재획득 방지
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);

            SetParent(hwnd, panel.Handle);

            var size = panel.ClientSize;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, Math.Max(size.Width, 1), Math.Max(size.Height, 1),
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            ShowWindow(hwnd, SW_SHOW);
        }

        // conhost.exe는 System32에만 있다. 이 앱은 x86이라 64비트 OS에서 System32 접근이
        // SysWOW64로 리다이렉트되는데 거기엔 conhost가 없으므로, 32비트 프로세스에선
        // Sysnative 가상 경로로 실제 System32에 접근한다.
        private static string ResolveConhostPath()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDir = (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
                ? Path.Combine(windir, "Sysnative")
                : Path.Combine(windir, "System32");
            return Path.Combine(sysDir, "conhost.exe");
        }

        private static bool IsManagerRunning(string exeName)
        {
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            if (string.IsNullOrEmpty(baseName)) return false;
            var procs = Process.GetProcessesByName(baseName);
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }

        // ── 세션 추적: "게이트웨이가 띄운 conhost PID"만 파일에 기록 ──
        private void SaveSession()
        {
            try
            {
                var pids = _slots.Values
                    .Where(s => s.Embedded && s.ConhostPid != 0)
                    .Select(s => s.ConhostPid)
                    .Distinct()
                    .Select(p => p.ToString());
                File.WriteAllLines(SessionFilePath, pids);
            }
            catch { }
        }

        private static HashSet<uint> LoadSessionPids()
        {
            var set = new HashSet<uint>();
            try
            {
                if (File.Exists(SessionFilePath))
                    foreach (var line in File.ReadAllLines(SessionFilePath))
                        if (uint.TryParse(line.Trim(), out uint pid) && pid != 0)
                            set.Add(pid);
            }
            catch { }
            return set;
        }

        private static void ClearSession()
        {
            try { if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath); }
            catch { }
        }

        private static void TryKillTree(uint pid)
        {
            if (pid == 0) return;
            try { using var p = Process.GetProcessById((int)pid); p.Kill(entireProcessTree: true); }
            catch { }
        }

        private static void KillByImageName(string exeName)
        {
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            if (string.IsNullOrEmpty(baseName)) return;
            foreach (var p in Process.GetProcessesByName(baseName))
            {
                try { p.Kill(entireProcessTree: true); } catch { } finally { p.Dispose(); }
            }
        }

        private void DisposeHosts()
        {
            foreach (var s in _slots.Values)
            {
                try { s.Host?.Dispose(); } catch { }
            }
        }

        private static string GetClassNameStr(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : "";
        }

        // ── Win32 P/Invoke (x86: LONG는 32비트 → SetWindowLongPtr 대신 32비트 변형 사용) ──

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight,
            [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = unchecked((int)0x40000000);
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
    }
}
