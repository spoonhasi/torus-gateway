using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TorusGateway.WebServer;

namespace TorusGateway
{
    // TORUS NC 파일 탐색기 (읽기 전용, 1단계).
    // 연결된 설비 목록을 콤보박스로 제공하고, 설비 선택 시 NC 메모리 루트 경로를 조회한다.
    // 웹서버 Open 여부와 무관하게 동작해야 하므로 HTTP 대신 Torus 싱글턴을 직접 호출하고,
    // REST 응답과 동일한 포맷을 위해 TorusController의 내부 헬퍼를 재사용한다.
    // 네이티브 API 호출은 블로킹될 수 있어 Task.Run으로 UI 스레드를 막지 않는다.
    // TODO: TORUS 목록 조회 API 연동 시 루트 경로 하위의 파일/폴더 목록 ListView 추가.
    public partial class NcExplorerWindow : Window
    {
        // 콤보박스 항목. Id < 1 은 "(Not selected)" 센티넬.
        private sealed class MachineItem
        {
            public int Id { get; init; }
            public string Display { get; init; } = "";
        }

        private readonly ObservableCollection<MachineItem> _machines = new();

        public NcExplorerWindow()
        {
            InitializeComponent();
            ComboMachine.ItemsSource = _machines;
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

        // 설비 선택 시 루트 경로 조회. "(Not selected)"이면 표시를 비운다.
        private async void ComboMachine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TextBoxRootPath == null) return; // 초기화 중 이벤트 대비
            if (ComboMachine.SelectedItem is not MachineItem m || m.Id < 1)
            {
                TextBoxRootPath.Text = "";
                return;
            }
            await LoadRootPathAsync(m.Id);
        }

        // data://machine/ncMemory/rootPath 조회 (GetNcMemoryRootPath 엔드포인트와 동일 경로).
        private async Task LoadRootPathAsync(int machineId)
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
                    TextBoxRootPath.Text = v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : v.GetRawText();
                }
                else
                {
                    string err = doc.RootElement.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                        ? er.GetString() ?? "" : "Unknown error";
                    TextBoxRootPath.Text = $"[status {status}] {err}";
                }
            }
            catch (Exception ex)
            {
                TextBoxRootPath.Text = $"Request failed: {ex.Message}";
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
