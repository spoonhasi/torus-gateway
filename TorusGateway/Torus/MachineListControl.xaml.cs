using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

namespace TorusGateway.Torus
{
    public class NCmachine
    {
        public bool activate { get; set; } = false;
        public string name { get; set; } = "";
        public int id { get; set; } = 0;
        public string vendorCode { get; set; } = "";
        public string address { get; set; } = "";
        public int port { get; set; } = 0;
        public string exDllPath { get; set; } = "";
        public string connectCode { get; set; } = "";
        public string ncVersionCode { get; set; } = "";
        public string toolSystem { get; set; } = "";
        public string username { get; set; } = "";
        public string password { get; set; } = "";
    }

    /// <summary>
    /// MachineListControl.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MachineListControl : UserControl
    {
        private ObservableCollection<NCmachine> ncMachineList_ { get; set; } = [];

        public string ConfigFilePath { get; set; } = "config.ini";

        // TORUS Run/Exit 버튼은 더 이상 자체적으로 콘솔을 띄우지 않고, 호스트(MainWindow)가
        // 콘솔을 메인 창에 임베드하도록 이벤트만 발행한다. (콘솔 임베드 패널은 MainWindow에 있음)
        public event EventHandler? TorusRunRequested;
        public event EventHandler? TorusExitRequested;

        public MachineListControl()
        {
            InitializeComponent();
            TextBlockSaveStatus.Visibility = Visibility.Hidden;
            ListViewMachineList.ItemsSource = ncMachineList_;
            LoadPath();
            // MachineList.xml 경로가 실제 파일을 가리키면 자동으로 Read.
            if (!string.IsNullOrWhiteSpace(TextBoxTorusMachineListPath.Text)
                && System.IO.File.Exists(TextBoxTorusMachineListPath.Text))
            {
                MachineListXmlRead(TextBoxTorusMachineListPath.Text);
            }
        }

        private static void SelectComboBoxItem(ComboBox comboBox, string targetValue)
        {
            foreach (ComboBoxItem item in comboBox.Items)
                if (string.Equals(item.Content.ToString(), targetValue, StringComparison.OrdinalIgnoreCase))
                    { comboBox.SelectedItem = item; break; }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button)
            {
                if (sender == ButtonTorusRunPath)
                {
                    if (FileSelector(TextBoxTorusRunPath.Text, out string newPath))
                    {
                        ConfigManager.Write(ConfigFilePath, "Main", "TorusRunPath", newPath);
                        TextBoxTorusRunPath.Text = newPath;
                    }
                }
                else if (sender == ButtonTorusExitPath)
                {
                    if (FileSelector(TextBoxTorusExitPath.Text, out string newPath))
                    {
                        ConfigManager.Write(ConfigFilePath, "Main", "TorusExitPath", newPath);
                        TextBoxTorusExitPath.Text = newPath;
                    }
                }
                else if (sender == ButtonTorusMachineListPath)
                {
                    if (FileSelector(TextBoxTorusMachineListPath.Text, out string newPath))
                    {
                        ConfigManager.Write(ConfigFilePath, "Main", "TorusMachineListPath", newPath);
                        TextBoxTorusMachineListPath.Text = newPath;
                    }
                }
                else if (sender == ButtonTorusPathSave)
                {
                    SavePath();
                }
                else if (sender == ButtonTorusPathLoad)
                {
                    LoadPath();
                }
                else if (sender == ButtonTorusRunExecute)
                {
                    // 콘솔 임베드는 아직 다듬는 중이라 예전처럼 bat를 외부 실행만 한다.
                    // (임베드 위임용 TorusRunRequested 이벤트는 기능 완성 시 재활성화 예정)
                    RunProcess(TextBoxTorusRunPath.Text);
                }
                else if (sender == ButtonTorusExitExecute)
                {
                    RunProcess(TextBoxTorusExitPath.Text);
                }
                else if (sender == ButtonTorusMachineListRead)
                {
                    MachineListXmlRead(TextBoxTorusMachineListPath.Text);
                }
                else if (sender == ButtonTorusMachineListWrite)
                {
                    MachineListXmlWrite(TextBoxTorusMachineListPath.Text);
                }
                else if (sender == ButtonTorusMachineListItemAdd)
                {
                    AddMachineListItem();
                }
                else if (sender == ButtonTorusMachineListItemRemove)
                {
                    RemoveMachineListItem();
                }
                else if (sender == ButtonTorusMachineListArrange)
                {
                    ArrangeMachineListItem();
                }
                else if (sender == ButtonTorusMachineListMoveUp)
                {
                    MoveSelectedMachine(-1);
                }
                else if (sender == ButtonTorusMachineListMoveDown)
                {
                    MoveSelectedMachine(+1);
                }
            }
        }

        private void LoadPath()
        {
            TextBoxTorusRunPath.Text = ConfigManager.Read(ConfigFilePath, "Main", "TorusRunPath", "");
            TextBoxTorusExitPath.Text = ConfigManager.Read(ConfigFilePath, "Main", "TorusExitPath", "");
            TextBoxTorusMachineListPath.Text = ConfigManager.Read(ConfigFilePath, "Main", "TorusMachineListPath", "");
        }

        private void SavePath()
        {
            ConfigManager.Write(ConfigFilePath, "Main", "TorusRunPath", TextBoxTorusRunPath.Text);
            ConfigManager.Write(ConfigFilePath, "Main", "TorusExitPath", TextBoxTorusExitPath.Text);
            ConfigManager.Write(ConfigFilePath, "Main", "TorusMachineListPath", TextBoxTorusMachineListPath.Text);
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender == ListViewMachineList)
            {
                if (ListViewMachineList.SelectedItem is not NCmachine)
                {
                    ClearMachineListItem();
                }
                else
                {
                    LoadMachineListItem();
                }
            }
        }

        private bool FileSelector(string oldPath, out string newPath)
        {
            OpenFileDialog openFileDialog = new()
            {
                Title = "Open File",
                Filter = "All Files (*.*)|*.*",
            };
            if (oldPath != "")
            {
                string absolutePath = System.IO.Path.GetFullPath(oldPath);
                string dirPath = System.IO.Path.GetDirectoryName(absolutePath);
                if (dirPath != null)
                {
                    openFileDialog.InitialDirectory = dirPath;
                }
            }
            if (openFileDialog.ShowDialog() == true)
            {
                newPath = openFileDialog.FileName;
                return true;
            }
            newPath = "";
            return false;
        }

        public static void RunProcess(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                MessageBox.Show("Executable not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 배치/실행파일을 띄우기만 하고 종료를 기다리지 않는다(UI 스레드 차단 방지).
            // UseShellExecute=true로 독립 프로세스로 실행되므로 핸들을 잡아둘 필요 없음.
            var processStartInfo = new ProcessStartInfo
            {
                FileName = filePath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(filePath),
                UseShellExecute = true
            };
            try
            {
                Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MachineListXmlRead(string filePath)
        {
            ClearMachineListItem();
            ncMachineList_.Clear();
            ListViewMachineList.Items.Refresh();
            TextBlockSaveStatus.Visibility = Visibility.Hidden;
            if (!System.IO.File.Exists(filePath))
            {
                MessageBox.Show("MachineList.xml not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!filePath.EndsWith("MachineList.xml"))
            {
                MessageBox.Show("The file name must be \"MachineList.xml\".", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            XDocument doc;
            try
            {
                doc = XDocument.Load(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to read MachineList.xml: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (doc.Root != null && doc.Root.Name.ToString().Equals("machinelist", StringComparison.InvariantCultureIgnoreCase))
            {
                foreach (var element in doc.Root.Elements())
                {
                    if (element.Name.ToString().Equals("ncmachine", StringComparison.InvariantCultureIgnoreCase))
                    {
                        bool isPass = false;
                        NCmachine ncMachine = new();
                        foreach (var attribute in element.Attributes())
                        {
                            string attributeName = attribute.Name.ToString().ToLowerInvariant();
                            string attributeValue = attribute.Value.ToString();
                            if (attributeName == "activate")
                            {
                                if (attributeValue.ToLowerInvariant() == "true")
                                {
                                    ncMachine.activate = true;
                                }
                                else
                                {
                                    ncMachine.activate = false;
                                }
                            }
                            else if (attributeName == "name")
                            {
                                ncMachine.name = attributeValue;
                            }
                            else if (attributeName == "id")
                            {
                                if (int.TryParse(attributeValue, out int id))
                                {
                                    ncMachine.id = id;
                                }
                                else
                                {
                                    isPass = true;
                                    break;
                                }
                            }
                            else if (attributeName == "vendorcode")
                            {
                                ncMachine.vendorCode = attributeValue;
                            }
                            else if (attributeName == "address")
                            {
                                ncMachine.address = attributeValue;
                            }
                            else if (attributeName == "port")
                            {
                                if (int.TryParse(attributeValue, out int port))
                                {
                                    ncMachine.port = port;
                                }
                                else
                                {
                                    isPass = true;
                                    break;
                                }
                            }
                            else if (attributeName == "exdllpath")
                            {
                                ncMachine.exDllPath = attributeValue;
                            }
                            else if (attributeName == "connectcode")
                            {
                                ncMachine.connectCode = attributeValue;
                            }
                            else if (attributeName == "ncversioncode")
                            {
                                ncMachine.ncVersionCode = attributeValue;
                            }
                            else if (attributeName == "toolsystem")
                            {
                                ncMachine.toolSystem = attributeValue;
                            }
                            else if (attributeName == "username")
                            {
                                ncMachine.username = attributeValue;
                            }
                            else if (attributeName == "password")
                            {
                                ncMachine.password = attributeValue;
                            }
                        }
                        if (isPass)
                        {
                            continue;
                        }
                        ncMachineList_.Add(ncMachine);
                    }
                }
            }
            ListViewMachineList.Items.Refresh();
        }

        private void MachineListXmlWrite(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                MessageBox.Show("MachineList.xml not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!filePath.EndsWith("MachineList.xml"))
            {
                MessageBox.Show("The file name must be \"MachineList.xml\".", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var activeIds = ncMachineList_
                .Where(m => m.activate)
                .Select(m => m.id)
                .ToList();

            var duplicateId = activeIds
                .GroupBy(id => id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .FirstOrDefault();

            if (duplicateId != 0)
            {
                MessageBox.Show($"Duplicate id ({duplicateId}) found among activated machines.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            XElement rootElement = new XElement("MachineList");
            foreach (var ncMachine in ncMachineList_)
            {
                XElement machineElement = new XElement("NCmachine",
                    new XAttribute("activate", ncMachine.activate.ToString().ToLowerInvariant()),
                    new XAttribute("name", ncMachine.name),
                    new XAttribute("id", ncMachine.id),
                    new XAttribute("vendorCode", ncMachine.vendorCode),
                    new XAttribute("address", ncMachine.address),
                    new XAttribute("port", ncMachine.port),
                    new XAttribute("exDllPath", ncMachine.exDllPath),
                    new XAttribute("connectCode", ncMachine.connectCode),
                    new XAttribute("ncVersionCode", ncMachine.ncVersionCode),
                    new XAttribute("toolSystem", ncMachine.toolSystem),
                    new XAttribute("username", ncMachine.username),
                    new XAttribute("password", ncMachine.password)
                );
                rootElement.Add(machineElement);
            }
            XDocument doc = new(rootElement);
            doc.Save(filePath);
            TextBlockSaveStatus.Visibility = Visibility.Hidden;
            MessageBox.Show("MachineList.xml updated.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddMachineListItem()
        {
            NCmachine machine = new();
            machine.name = "Machine";
            machine.id = 1;
            machine.vendorCode = "KCNC";
            machine.connectCode = "Default";
            machine.ncVersionCode = "Default";
            machine.toolSystem = "Default";
            ncMachineList_.Add(machine);
            ListViewMachineList.Items.Refresh();
            ListViewMachineList.SelectedItem = machine;
            ListViewMachineList.ScrollIntoView(machine);
            TextBlockSaveStatus.Visibility = Visibility.Visible;
        }

        private void RemoveMachineListItem()
        {
            if (ListViewMachineList.SelectedItem is not NCmachine ncMachine)
            {
                return;
            }
            ncMachineList_.Remove(ncMachine);
            ListViewMachineList.Items.Refresh();
            ClearMachineListItem();
            TextBlockSaveStatus.Visibility = Visibility.Visible;
        }

        private void ArrangeMachineListItem()
        {
            if (ListViewMachineList.Items.Count < 1)
            {
                return;
            }
            var sortedList = new ObservableCollection<NCmachine>(ncMachineList_.OrderBy(x => x.id));
            ncMachineList_ = sortedList;
            ListViewMachineList.ItemsSource = ncMachineList_;
            ListViewMachineList.Items.Refresh();
            TextBlockSaveStatus.Visibility = Visibility.Visible;
        }

        // delta: -1 = up, +1 = down
        private void MoveSelectedMachine(int delta)
        {
            if (ListViewMachineList.SelectedItem is not NCmachine ncMachine) return;
            int oldIndex = ncMachineList_.IndexOf(ncMachine);
            int newIndex = oldIndex + delta;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= ncMachineList_.Count) return;
            ncMachineList_.Move(oldIndex, newIndex);
            ListViewMachineList.SelectedItem = ncMachine;
            ListViewMachineList.ScrollIntoView(ncMachine);
            TextBlockSaveStatus.Visibility = Visibility.Visible;
        }

        private void ClearMachineListItem()
        {
            ComboBoxTorusNcMachineActivate.SelectedIndex = 1;
            TextBoxTorusNcMachineName.Text = "";
            TextBoxTorusNcMachineId.Text = "";
            ComboBoxTorusNcMachineVendorCode.SelectedIndex = 4;
            TextBoxTorusNcMachineAddress.Text = "";
            TextBoxTorusNcMachinePort.Text = "";
            TextBoxTorusNcMachineExDllPath.Text = "";
            ComboBoxTorusNcMachineConnectCode.SelectedIndex = 0;
            ComboBoxTorusNcMachineNcVersionCode.SelectedIndex = 0;
            ComboBoxTorusNcMachineToolSystem.SelectedIndex = 0;
            TextBoxTorusNcMachineUsername.Text = "";
            TextBoxTorusNcMachinePassword.Text = "";
            TextBlockSaveStatus.Visibility = Visibility.Hidden;
        }

        private void LoadMachineListItem()
        {
            if (ListViewMachineList.SelectedItem is not NCmachine ncMachine)
            {
                return;
            }
            bool isHidden = TextBlockSaveStatus.Visibility == Visibility.Hidden;

            SelectComboBoxItem(ComboBoxTorusNcMachineActivate, ncMachine.activate.ToString());
            TextBoxTorusNcMachineName.Text = ncMachine.name;
            TextBoxTorusNcMachineId.Text = ncMachine.id.ToString();
            SelectComboBoxItem(ComboBoxTorusNcMachineVendorCode, ncMachine.vendorCode);
            TextBoxTorusNcMachineAddress.Text = ncMachine.address;
            TextBoxTorusNcMachinePort.Text = ncMachine.port.ToString();
            TextBoxTorusNcMachineExDllPath.Text = ncMachine.exDllPath;

            string connectCode = ncMachine.connectCode;
            if (connectCode.Trim() == "") connectCode = "Default";
            SelectComboBoxItem(ComboBoxTorusNcMachineConnectCode, connectCode);

            string ncVersionCode = ncMachine.ncVersionCode;
            if (ncVersionCode.Trim() == "") ncVersionCode = "Default";
            SelectComboBoxItem(ComboBoxTorusNcMachineNcVersionCode, ncVersionCode);

            string toolSystem = ncMachine.toolSystem;
            if (toolSystem.Trim() == "") toolSystem = "Default";
            SelectComboBoxItem(ComboBoxTorusNcMachineToolSystem, toolSystem);

            TextBoxTorusNcMachineUsername.Text = ncMachine.username;
            TextBoxTorusNcMachinePassword.Text = ncMachine.password;

            if (isHidden)
            {
                TextBlockSaveStatus.Visibility = Visibility.Hidden;
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListViewMachineList.SelectedItem is not NCmachine ncMachine)
            {
                return;
            }
            TextBlockSaveStatus.Visibility = Visibility.Visible;
            if (sender == ComboBoxTorusNcMachineActivate)
            {
                string activate = ((ComboBoxItem)ComboBoxTorusNcMachineActivate.SelectedItem)?.Content.ToString().ToLowerInvariant();
                if (activate == "true")
                {
                    ncMachine.activate = true;
                }
                else
                {
                    ncMachine.activate = false;
                }
            }
            else if (sender == ComboBoxTorusNcMachineVendorCode)
            {
                ncMachine.vendorCode = ((ComboBoxItem)ComboBoxTorusNcMachineVendorCode.SelectedItem)?.Content.ToString();
            }
            else if (sender == ComboBoxTorusNcMachineConnectCode)
            {
                ncMachine.connectCode = ((ComboBoxItem)ComboBoxTorusNcMachineConnectCode.SelectedItem)?.Content.ToString();
            }
            else if (sender == ComboBoxTorusNcMachineNcVersionCode)
            {
                ncMachine.ncVersionCode = ((ComboBoxItem)ComboBoxTorusNcMachineNcVersionCode.SelectedItem)?.Content.ToString();
            }
            else if (sender == ComboBoxTorusNcMachineToolSystem)
            {
                ncMachine.toolSystem = ((ComboBoxItem)ComboBoxTorusNcMachineToolSystem.SelectedItem)?.Content.ToString();
            }
            ListViewMachineList.Items.Refresh();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ListViewMachineList.SelectedItem is not NCmachine ncMachine)
            {
                return;
            }
            TextBlockSaveStatus.Visibility = Visibility.Visible;
            if (sender == TextBoxTorusNcMachineName)
            {
                ncMachine.name = TextBoxTorusNcMachineName.Text.Trim();
            }
            else if (sender == TextBoxTorusNcMachineId)
            {
                if (!int.TryParse(TextBoxTorusNcMachineId.Text, out int id) || id < 1)
                {
                    ncMachine.id = 1;
                }
                else
                {
                    ncMachine.id = id;
                }
            }
            else if (sender == TextBoxTorusNcMachineAddress)
            {
                ncMachine.address = TextBoxTorusNcMachineAddress.Text.Trim();
            }
            else if (sender == TextBoxTorusNcMachinePort)
            {
                if (!int.TryParse(TextBoxTorusNcMachinePort.Text, out int port) || port < 0 || port > 65535)
                {
                    ncMachine.port = 0;
                }
                else
                {
                    ncMachine.port = port;
                }
            }
            else if (sender == TextBoxTorusNcMachineExDllPath)
            {
                ncMachine.exDllPath = TextBoxTorusNcMachineExDllPath.Text.Trim();
            }
            else if (sender == TextBoxTorusNcMachineUsername)
            {
                ncMachine.username = TextBoxTorusNcMachineUsername.Text.Trim();
            }
            else if (sender == TextBoxTorusNcMachinePassword)
            {
                ncMachine.password = TextBoxTorusNcMachinePassword.Text.Trim();
            }
            ListViewMachineList.Items.Refresh();
        }
    }
}
