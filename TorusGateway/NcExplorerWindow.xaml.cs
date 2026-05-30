using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TorusGateway
{
    // TORUS NC 파일 탐색기.
    // 연결된 설비 목록을 콤보박스로 제공하고, 설비 선택 시 NC 메모리 루트 경로를 조회한 뒤
    // 해당 경로의 파일/폴더 목록을 보여준다. 폴더 더블클릭으로 진입, "..."으로 상위 복귀.
    // 선택한 파일은 Download(로컬 저장), 현재 폴더로 Upload(로컬 파일 올리기) 가능.
    //
    // 모든 기능은 게이트웨이 자신의 HTTP 엔드포인트를 통해 수행한다(Torus 싱글턴 직접 호출 안 함).
    // 따라서 이 창은 웹서버가 가동 중일 때만 열린다(MainWindow에서 버튼 활성화 제어).
    // 접속 대상은 메인 창의 Host/Port이며, 와일드카드 Host는 127.0.0.1로 대체한다.
    // loopback은 IP 허용목록과 무관하게 항상 허용되므로 자기 자신에 대한 요청은 차단되지 않는다.
    public partial class NcExplorerWindow : Window
    {
        // 콤보박스 항목. Id < 1 은 "(Not selected)" 센티넬.
        private sealed class MachineItem
        {
            public int Id { get; init; }
            public string Display { get; init; } = "";
        }

        // ListView 표시용 항목.
        private sealed class EntryItem
        {
            public string Name { get; init; } = "";
            public bool IsDir { get; init; }
            public long Size { get; init; }
            public string Modified { get; init; } = "";
            // 상위 폴더 이동용 가상 항목("..."). 실제 폴더가 아니라 네비게이션 전용.
            public bool IsParent { get; init; }

            // 폴더/파일 아이콘 + 이름 (상위 이동 항목은 아이콘 없이 이름만)
            public string DisplayName => IsParent ? Name : (IsDir ? "\U0001F4C1 " : "\U0001F4C4 ") + Name;
            // 상위 이동/폴더는 크기를 비우고, 파일은 천 단위 구분 + " B"
            public string SizeDisplay => (IsParent || IsDir) ? "" : Size.ToString("N0") + " B";
        }

        // 상위 폴더 이동용 목록 항목 표기.
        private const string ParentEntryName = "...";

        // 게이트웨이 웹서버에 접속하는 공용 HttpClient. 파일 전송을 고려해 타임아웃을 넉넉히 둔다.
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly string _baseUrl;

        private readonly ObservableCollection<MachineItem> _machines = new();
        private readonly ObservableCollection<EntryItem> _entries = new();

        // 네비게이션 상태. 상위 이동은 경로 문자열 파싱 대신 방문 스택으로 처리(경로 포맷에 비의존).
        private readonly Stack<string> _pathStack = new();
        private int _machineId = -1;
        private string _currentPath = "";
        private bool _busy;

        public NcExplorerWindow(string host, string port)
        {
            InitializeComponent();
            // Host가 0.0.0.0 등 와일드카드면 클라이언트 접속용으로 127.0.0.1로 대체 (TestRequestWindow와 동일)
            string clientHost = (host == "0.0.0.0" || string.IsNullOrWhiteSpace(host) || host == "*")
                ? "127.0.0.1" : host;
            _baseUrl = $"http://{clientHost}:{port}";
            ComboMachine.ItemsSource = _machines;
            ListViewEntries.ItemsSource = _entries;
            Loaded += async (s, e) => await LoadMachinesAsync();
        }

        // ── HTTP 헬퍼 ──────────────────────────────────────────────

        private async Task<string> HttpGetAsync(string pathAndQuery)
        {
            var resp = await _http.GetAsync(_baseUrl + pathAndQuery);
            return await resp.Content.ReadAsStringAsync();
        }

        private async Task<string> HttpPostJsonAsync(string pathAndQuery, string json)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_baseUrl + pathAndQuery, content);
            return await resp.Content.ReadAsStringAsync();
        }

        // 응답 JSON의 status 정수. 없거나 비정상이면 -1.
        private static int GetStatus(JsonDocument doc) =>
            doc.RootElement.TryGetProperty("status", out var st)
            && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int code)
                ? code : -1;

        // 응답 JSON의 error 메시지를 "[status N] msg" 형태로.
        private static string FormatError(JsonDocument doc, int status)
        {
            string err = doc.RootElement.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                ? er.GetString() ?? "" : "Unknown error";
            return $"[status {status}] {err}";
        }

        // NC 경로를 쿼리스트링에 안전하게 싣기 위해 URL 인코딩.
        private static string EncodePath(string ncPath) => Uri.EscapeDataString(ncPath);

        // ── 머신 목록 ──────────────────────────────────────────────

        // GET /machineList 로 설비 목록을 가져와 콤보박스를 채운다. 첫 항목은 "(Not selected)".
        private async Task LoadMachinesAsync()
        {
            ComboMachine.IsEnabled = false;
            try
            {
                string body = await HttpGetAsync("/machineList");

                _machines.Clear();
                _machines.Add(new MachineItem { Id = -1, Display = "(Not selected)" });
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("value", out var value))
                    foreach (var m in ParseMachines(value))
                        _machines.Add(m);
                ComboMachine.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load machine list: {ex.Message}", "Error");
            }
            finally
            {
                ComboMachine.IsEnabled = true;
            }
        }

        // value(설비 배열/객체)에서 id/name을 추출.
        private static List<MachineItem> ParseMachines(JsonElement value)
        {
            var result = new List<MachineItem>();
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in value.EnumerateArray())
                {
                    var item = ToMachineItem(elem);
                    if (item != null) result.Add(item);
                }
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                var item = ToMachineItem(value);
                if (item != null) result.Add(item);
            }
            return result;
        }

        private static MachineItem? ToMachineItem(JsonElement elem)
        {
            if (elem.ValueKind != JsonValueKind.Object) return null;

            int id = -1;
            if (elem.TryGetProperty("id", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number) idProp.TryGetInt32(out id);
                else if (idProp.ValueKind == JsonValueKind.String) int.TryParse(idProp.GetString(), out id);
            }
            if (id < 1) return null;

            string name = elem.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString() ?? "" : "";
            string display = string.IsNullOrEmpty(name) ? $"ID {id}" : $"{name} (ID: {id})";
            return new MachineItem { Id = id, Display = display };
        }

        // ── 네비게이션 ─────────────────────────────────────────────

        // 설비 선택 시: 네비게이션 초기화 → 루트 경로 조회 → 루트 목록 표시.
        private async void ComboMachine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TextBoxRootPath == null) return; // 초기화 중 이벤트 대비

            _pathStack.Clear();
            _entries.Clear();
            _currentPath = "";
            TextBoxRootPath.Text = "";
            TextBoxCurrentPath.Text = "";
            TextBlockStatus.Text = "";

            if (ComboMachine.SelectedItem is not MachineItem m || m.Id < 1)
            {
                _machineId = -1;
                TextBoxRootPath.Text = "";
                UpdateButtons();
                return;
            }

            _machineId = m.Id;
            string? root = await GetRootPathAsync(m.Id);
            if (root == null) return; // 오류는 TextBoxRootPath에 이미 표시됨

            TextBoxRootPath.Text = root;
            await EnterDirectoryAsync(root);
        }

        // GET /machine/ncMemoryRootPath 로 루트 경로 조회. 성공 시 경로, 실패 시 null(메시지 표시).
        private async Task<string?> GetRootPathAsync(int machineId)
        {
            TextBoxRootPath.Text = "Loading...";
            try
            {
                string body = await HttpGetAsync($"/machine/ncMemoryRootPath?machine={machineId}");
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);

                if (status == 0 && doc.RootElement.TryGetProperty("value", out var v))
                {
                    // 엔드포인트가 이미 끝의 '/'를 제거하지만 방어적으로 한 번 더.
                    string path = (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).TrimEnd('/');
                    if (string.IsNullOrEmpty(path))
                    {
                        TextBoxRootPath.Text = "(empty root path)";
                        return null;
                    }
                    return path;
                }

                TextBoxRootPath.Text = FormatError(doc, status);
                return null;
            }
            catch (Exception ex)
            {
                TextBoxRootPath.Text = $"Request failed: {ex.Message}";
                return null;
            }
        }

        // 현재 경로를 지정 디렉토리로 바꾸고 목록을 다시 읽는다.
        private async Task EnterDirectoryAsync(string path)
        {
            _currentPath = path;
            TextBoxCurrentPath.Text = path;
            await ListDirectoryAsync(path);
            UpdateButtons();
        }

        // GET /machine/ncMemoryPath/directory 로 목록 조회 (busy 가드 + 표시). 핵심 로직은 Core에 위임.
        private async Task ListDirectoryAsync(string path)
        {
            if (_busy) return;
            SetBusy(true);
            try { await ListDirectoryCoreAsync(path); }
            finally { SetBusy(false); }
        }

        // 목록 조회 본체. busy 관리를 호출부가 이미 했다고 가정(업로드 후 갱신 등 재진입 대응).
        private async Task ListDirectoryCoreAsync(string path)
        {
            TextBlockStatus.Text = "Loading...";
            try
            {
                string body = await HttpGetAsync(
                    $"/machine/ncMemoryPath/directory?machine={_machineId}&ncMemoryPath={EncodePath(path)}");
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);
                if (status != 0 || !doc.RootElement.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.Array)
                {
                    _entries.Clear();
                    TextBlockStatus.Text = FormatError(doc, status);
                    return;
                }

                var items = new List<EntryItem>();
                foreach (var f in value.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    string name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    bool isDir = f.TryGetProperty("isDir", out var d) && d.ValueKind == JsonValueKind.True;
                    long size = f.TryGetProperty("sizeBytes", out var s)
                        && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long sv) ? sv : 0;
                    string modified = f.TryGetProperty("modifiedAt", out var mo) && mo.ValueKind == JsonValueKind.String
                        ? mo.GetString() ?? "" : "";
                    items.Add(new EntryItem { Name = name.TrimEnd('/'), IsDir = isDir, Size = size, Modified = modified });
                }
                // 엔드포인트가 이미 폴더 우선 → 이름순 정렬하지만 UI에서도 동일 보장.
                items = items.OrderByDescending(it => it.IsDir).ThenBy(it => it.Name).ToList();

                _entries.Clear();
                // 루트가 아니면(상위 경로가 있으면) 첫 항목에 "..." 추가.
                if (_pathStack.Count > 0)
                    _entries.Add(new EntryItem { Name = ParentEntryName, IsDir = true, IsParent = true });
                foreach (var it in items) _entries.Add(it);

                int dirs = items.Count(it => it.IsDir);
                int fileCount = items.Count - dirs;
                TextBlockStatus.Text = $"{dirs} folders, {fileCount} files";
            }
            catch (Exception ex)
            {
                _entries.Clear();
                TextBlockStatus.Text = $"Failed: {ex.Message}";
            }
        }

        // 항목 더블클릭. "..."은 상위로 복귀(방문 스택 pop), 폴더는 진입, 파일은 동작 없음.
        private async void ListViewEntries_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_busy) return;
            if (ListViewEntries.SelectedItem is not EntryItem item) return;

            if (item.IsParent)
            {
                if (_pathStack.Count == 0) return;
                string parent = _pathStack.Pop();
                await EnterDirectoryAsync(parent);
                return;
            }

            if (!item.IsDir) return;

            // 목록 응답엔 진입 가능한 절대 경로가 없으므로 현재 경로에 폴더 이름을 이어붙인다.
            string childPath = _currentPath.TrimEnd('/') + "/" + item.Name;
            _pathStack.Push(_currentPath);
            await EnterDirectoryAsync(childPath);
        }

        // ── 업로드 / 다운로드 ──────────────────────────────────────

        // 선택한 파일을 GET /machine/ncMemoryPath/content 로 받아 로컬에 저장.
        private async void ButtonDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _machineId < 1) return;
            if (ListViewEntries.SelectedItem is not EntryItem item || item.IsParent || item.IsDir) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save NC file",
                FileName = item.Name,
                Filter = "All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string ncPath = _currentPath.TrimEnd('/') + "/" + item.Name;
            string localPath = dlg.FileName;
            SetBusy(true);
            TextBlockStatus.Text = "Downloading...";
            try
            {
                string body = await HttpGetAsync(
                    $"/machine/ncMemoryPath/content?machine={_machineId}&ncMemoryPath={EncodePath(ncPath)}");
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);
                if (status != 0 || !doc.RootElement.TryGetProperty("value", out var v))
                {
                    TextBlockStatus.Text = FormatError(doc, status);
                    return;
                }
                string content = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                await File.WriteAllTextAsync(localPath, content);
                TextBlockStatus.Text = $"Downloaded: {item.Name}";
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = $"Download failed: {ex.Message}";
                MessageBox.Show($"Download failed: {ex.Message}", "Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // 로컬 파일을 POST /machine/ncMemoryPath/content 로 현재 폴더에 업로드.
        private async void ButtonUpload_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _machineId < 1 || string.IsNullOrEmpty(_currentPath)) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a file to upload",
                Filter = "All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string localPath = dlg.FileName;
            string fileName = Path.GetFileName(localPath);
            string ncPath = _currentPath.TrimEnd('/') + "/" + fileName;
            SetBusy(true);
            TextBlockStatus.Text = "Uploading...";
            try
            {
                string content = await File.ReadAllTextAsync(localPath);
                string jsonBody = JsonSerializer.Serialize(new { value = content });
                string body = await HttpPostJsonAsync(
                    $"/machine/ncMemoryPath/content?machine={_machineId}&ncMemoryPath={EncodePath(ncPath)}", jsonBody);
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);
                if (status != 0)
                {
                    TextBlockStatus.Text = FormatError(doc, status);
                    return;
                }
                // busy를 이 핸들러가 잡고 있으므로 Core를 직접 호출해 목록을 갱신(업로드한 파일이 보이도록).
                await ListDirectoryCoreAsync(_currentPath);
                // 방금 올린 파일을 선택/스크롤하여 눈에 띄게 한다.
                var uploaded = _entries.FirstOrDefault(it => !it.IsParent && !it.IsDir && it.Name == fileName);
                if (uploaded != null)
                {
                    ListViewEntries.SelectedItem = uploaded;
                    ListViewEntries.ScrollIntoView(uploaded);
                }
                // 갱신이 status를 폴더/파일 수로 덮으므로 업로드 확인 메시지를 마지막에 다시 표시.
                TextBlockStatus.Text = $"Uploaded: {fileName}";
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = $"Upload failed: {ex.Message}";
                MessageBox.Show($"Upload failed: {ex.Message}", "Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // 현재 폴더 아래에 새 폴더를 생성. POST /machine/ncMemoryPath/directory ({"value":[]}).
        // ncMemoryPath = 현재경로 + "/" + 입력한 폴더명. 엔드포인트가 마지막 세그먼트를 폴더명으로 분리한다.
        private async void ButtonNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _machineId < 1 || string.IsNullOrEmpty(_currentPath)) return;

            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter a name for the new folder:", "New Folder", "").Trim();
            if (string.IsNullOrEmpty(name)) return; // 취소 또는 빈 입력
            if (name.Contains('/') || name.Contains('\\'))
            {
                MessageBox.Show("Folder name cannot contain '/' or '\\'.", "Error");
                return;
            }

            string ncPath = _currentPath.TrimEnd('/') + "/" + name;
            SetBusy(true);
            TextBlockStatus.Text = "Creating folder...";
            try
            {
                string jsonBody = JsonSerializer.Serialize(new { value = Array.Empty<string>() });
                string body = await HttpPostJsonAsync(
                    $"/machine/ncMemoryPath/directory?machine={_machineId}&ncMemoryPath={EncodePath(ncPath)}", jsonBody);
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);
                if (status != 0)
                {
                    TextBlockStatus.Text = FormatError(doc, status);
                    return;
                }
                // busy를 이 핸들러가 잡고 있으므로 Core를 직접 호출해 목록을 갱신.
                await ListDirectoryCoreAsync(_currentPath);
                // 방금 만든 폴더를 선택/스크롤하여 눈에 띄게 한다.
                var created = _entries.FirstOrDefault(it => !it.IsParent && it.IsDir && it.Name == name);
                if (created != null)
                {
                    ListViewEntries.SelectedItem = created;
                    ListViewEntries.ScrollIntoView(created);
                }
                // 갱신이 status를 폴더/파일 수로 덮으므로 생성 확인 메시지를 마지막에 다시 표시.
                TextBlockStatus.Text = $"Created folder: {name}";
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = $"Folder creation failed: {ex.Message}";
                MessageBox.Show($"Folder creation failed: {ex.Message}", "Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // 선택한 파일/폴더를 POST /machine/ncMemoryPath/exist ({"value":false}) 로 삭제. 삭제 전 확인.
        // 엔드포인트(CNCFileDelete)가 폴더 삭제도 지원하므로 "..." 외의 폴더/파일 모두 대상.
        private async void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _machineId < 1) return;
            if (ListViewEntries.SelectedItem is not EntryItem item || item.IsParent) return;

            var confirm = MessageBox.Show(
                $"Delete '{item.Name}' from the machine?\nThis cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            string ncPath = _currentPath.TrimEnd('/') + "/" + item.Name;
            SetBusy(true);
            TextBlockStatus.Text = "Deleting...";
            try
            {
                string jsonBody = JsonSerializer.Serialize(new { value = false });
                string body = await HttpPostJsonAsync(
                    $"/machine/ncMemoryPath/exist?machine={_machineId}&ncMemoryPath={EncodePath(ncPath)}", jsonBody);
                using var doc = JsonDocument.Parse(body);
                int status = GetStatus(doc);
                if (status != 0)
                {
                    TextBlockStatus.Text = FormatError(doc, status);
                    return;
                }
                string deletedName = item.Name;
                // busy를 이 핸들러가 잡고 있으므로 Core를 직접 호출해 목록을 갱신.
                await ListDirectoryCoreAsync(_currentPath);
                // 갱신이 status를 폴더/파일 수로 덮으므로 삭제 확인 메시지를 마지막에 다시 표시.
                TextBlockStatus.Text = $"Deleted: {deletedName}";
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = $"Delete failed: {ex.Message}";
                MessageBox.Show($"Delete failed: {ex.Message}", "Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── 상태 / 버튼 제어 ───────────────────────────────────────

        private void ListViewEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ComboMachine.IsEnabled = !busy;
            ListViewEntries.IsEnabled = !busy;
            UpdateButtons();
        }

        // Download: 파일(폴더/"..." 아님) 선택 시. Delete: 파일/폴더("..." 아님) 선택 시.
        // Upload/New Folder: 설비 선택+현재 경로 존재 시. 작업 중엔 모두 비활성.
        private void UpdateButtons()
        {
            if (ButtonDownload == null || ButtonUpload == null || ButtonDelete == null || ButtonNewFolder == null) return;
            bool active = !_busy && _machineId >= 1 && !string.IsNullOrEmpty(_currentPath);
            ButtonUpload.IsEnabled = active;
            ButtonNewFolder.IsEnabled = active;
            var selected = ListViewEntries.SelectedItem as EntryItem;
            bool fileSelected = selected != null && !selected.IsParent && !selected.IsDir;
            bool entrySelected = selected != null && !selected.IsParent; // 파일 또는 폴더
            ButtonDownload.IsEnabled = active && fileSelected;
            ButtonDelete.IsEnabled = active && entrySelected;
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
