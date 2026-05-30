using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TorusGateway.WebServer;

namespace TorusGateway
{
    // TORUS NC 파일 탐색기 (읽기 전용).
    // 연결된 설비 목록을 콤보박스로 제공하고, 설비 선택 시 NC 메모리 루트 경로를 조회한 뒤
    // 해당 경로의 파일/폴더 목록을 보여준다. 폴더 더블클릭으로 진입, Up 버튼으로 상위 복귀.
    // 웹서버 Open 여부와 무관하게 동작해야 하므로 HTTP 대신 Torus 싱글턴을 직접 호출하고,
    // REST 응답과 동일한 포맷/모델(TorusController, WebServer.FilesObject)을 재사용한다.
    // 네이티브 API 호출은 블로킹될 수 있어 Task.Run으로 UI 스레드를 막지 않는다.
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
            public string Path { get; init; } = "";
            public bool IsDir { get; init; }
            public long Size { get; init; }
            public string Modified { get; init; } = "";

            // 폴더/파일 아이콘 + 이름
            public string DisplayName => (IsDir ? "\U0001F4C1 " : "\U0001F4C4 ") + Name;
            // 폴더는 크기를 비우고, 파일은 천 단위 구분 + " B"
            public string SizeDisplay => IsDir ? "" : Size.ToString("N0") + " B";
        }

        private readonly ObservableCollection<MachineItem> _machines = new();
        private readonly ObservableCollection<EntryItem> _entries = new();

        // 네비게이션 상태. 상위 이동은 경로 문자열 파싱 대신 방문 스택으로 처리(경로 포맷에 비의존).
        private readonly Stack<string> _pathStack = new();
        private int _machineId = -1;
        private string _currentPath = "";
        private bool _busy;

        public NcExplorerWindow()
        {
            InitializeComponent();
            ComboMachine.ItemsSource = _machines;
            ListViewEntries.ItemsSource = _entries;
            Loaded += async (s, e) => await LoadMachinesAsync();
        }

        // 연결된 설비 목록을 가져와 콤보박스를 채운다. 첫 항목은 "(Not selected)".
        private async Task LoadMachinesAsync()
        {
            ComboMachine.IsEnabled = false;
            try
            {
                string json = await Task.Run(() =>
                {
                    string raw = Torus.Torus.Instance.GetMachinesInfo();
                    // /machineList와 동일하게 단일 설비도 배열로 유지.
                    return TorusController.FilterStatusAndValue(raw, unwrapSingletonArray: false);
                });

                _machines.Clear();
                _machines.Add(new MachineItem { Id = -1, Display = "(Not selected)" });
                foreach (var m in ParseMachines(json))
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

        // FilterStatusAndValue 결과 JSON의 value(설비 배열/객체)에서 id/name을 추출.
        private static List<MachineItem> ParseMachines(string json)
        {
            var result = new List<MachineItem>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var value))
                return result;

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

        // 설비 선택 시: 네비게이션 초기화 → 루트 경로 조회 → 루트 목록 표시.
        private async void ComboMachine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TextBoxRootPath == null) return; // 초기화 중 이벤트 대비

            _pathStack.Clear();
            _entries.Clear();
            _currentPath = "";
            TextBoxCurrentPath.Text = "";
            TextBlockStatus.Text = "";
            UpdateUpButton();

            if (ComboMachine.SelectedItem is not MachineItem m || m.Id < 1)
            {
                _machineId = -1;
                TextBoxRootPath.Text = "";
                return;
            }

            _machineId = m.Id;
            string? root = await GetRootPathAsync(m.Id);
            if (root == null) return; // 오류는 TextBoxRootPath에 이미 표시됨

            TextBoxRootPath.Text = root;
            await EnterDirectoryAsync(root);
        }

        // data://machine/ncMemory/rootPath 조회. 성공 시 경로 문자열, 실패 시 null(메시지는 표시).
        private async Task<string?> GetRootPathAsync(int machineId)
        {
            TextBoxRootPath.Text = "Loading...";
            try
            {
                string json = await Task.Run(() =>
                {
                    string raw = Torus.Torus.Instance.GetData("data://machine/ncMemory/rootPath", $"machine={machineId}");
                    return TorusController.FilterStatusAndValue(raw);
                });

                using var doc = JsonDocument.Parse(json);
                int status = doc.RootElement.TryGetProperty("status", out var st)
                             && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int code)
                             ? code : -1;

                if (status == 0 && doc.RootElement.TryGetProperty("value", out var v))
                {
                    string path = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                    if (string.IsNullOrEmpty(path))
                    {
                        TextBoxRootPath.Text = "(empty root path)";
                        return null;
                    }
                    return path;
                }

                string err = doc.RootElement.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                    ? er.GetString() ?? "" : "Unknown error";
                TextBoxRootPath.Text = $"[status {status}] {err}";
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
            UpdateUpButton();
            await ListDirectoryAsync(path);
        }

        // getFileListEx 래퍼(Torus.GetFileList) 호출 → 폴더 우선 정렬 → ListView 갱신.
        private async Task ListDirectoryAsync(string path)
        {
            if (_busy) return;
            SetBusy(true);
            TextBlockStatus.Text = "Loading...";
            try
            {
                string json = await Task.Run(() => Torus.Torus.Instance.GetFileList(path, _machineId));

                using (var doc = JsonDocument.Parse(json))
                {
                    int status = doc.RootElement.TryGetProperty("status", out var st)
                                 && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int code)
                                 ? code : -1;
                    if (status != 0)
                    {
                        _entries.Clear();
                        TextBlockStatus.Text = $"[status {status}] {TorusStatusCodes.GetDescription(status)}";
                        return;
                    }
                }

                var files = JsonSerializer.Deserialize<FilesObject>(json);
                var items = (files?.Value ?? new List<FileObject>())
                    .Where(f => f is not null)
                    .OrderByDescending(f => f.IsDir)
                    .ThenBy(f => f.Name)
                    .Select(f => new EntryItem
                    {
                        Name = f.Name,
                        Path = f.Path,
                        IsDir = f.IsDir,
                        Size = f.Size,
                        Modified = f.Datetime
                    });

                _entries.Clear();
                foreach (var it in items) _entries.Add(it);

                int dirs = _entries.Count(e => e.IsDir);
                int fileCount = _entries.Count - dirs;
                TextBlockStatus.Text = $"{dirs} folders, {fileCount} files";
            }
            catch (Exception ex)
            {
                _entries.Clear();
                TextBlockStatus.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        // 폴더 더블클릭 시 진입. 파일은 읽기 전용 1단계에선 동작 없음.
        private async void ListViewEntries_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_busy) return;
            if (ListViewEntries.SelectedItem is not EntryItem item) return;
            if (!item.IsDir || string.IsNullOrEmpty(item.Path)) return;

            _pathStack.Push(_currentPath);
            await EnterDirectoryAsync(item.Path);
        }

        // 상위 디렉토리로 복귀 (방문 스택 pop).
        private async void ButtonUp_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _pathStack.Count == 0) return;
            string parent = _pathStack.Pop();
            await EnterDirectoryAsync(parent);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ComboMachine.IsEnabled = !busy;
            ListViewEntries.IsEnabled = !busy;
            UpdateUpButton();
        }

        private void UpdateUpButton()
        {
            if (ButtonUp == null) return;
            ButtonUp.IsEnabled = !_busy && _pathStack.Count > 0;
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
