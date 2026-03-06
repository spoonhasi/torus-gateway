using Microsoft.VisualBasic;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml.Linq;
using TorusGateway.Torus;
using TorusGateway.WebServer;

namespace TorusGateway
{
    //TORUS App 등록정보
    static class Constants
    {
        public const string TorusAppGuid = "0C1016A3-45BF-4DE9-B0B8-1ECBB948177D";
        public const string TorusAppName = "TorusGateway";
        public const string ConfigFileName = "config.ini";
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private WebServer.WebServer? _webServer;

        //TORUS machineList.xml 설정 메뉴 온오프(true이면 보임)
        private bool isExpanded = false;

        //TORUS machineList.xml 설정 메뉴가 보이지 않을시의 창 크기
        private const double CollapsedHeight = 150; // 기본 높이
        private const double CollapsedWidth = 600; // 기본 너비

        //TORUS machineList.xml 설정 메뉴가 보일시의 창 크기
        private const double ExpandedHeight = 600; // 확장할 높이
        private const double ExpandedWidth = 1050; // 확장할 너비

        public MainWindow()
        {
            TorusInit(Constants.TorusAppName, Constants.TorusAppGuid);//TORUS App 등록정보를 입력합니다.
            InitializeComponent();
            this.Height = CollapsedHeight;
            this.Width = CollapsedWidth;
            ButtonWebServerOpen.IsEnabled = false;
            ButtonWebServerClose.IsEnabled = false;
        }

        //TORUS에 연결하기 전에 App 등록정보를 TORUS 운용 클래스에 입력합니다.
        public static void TorusInit(string appName, string appGuid)
        {
            Torus.Torus.Initialize(appName, appGuid);
        }

        //TORUS에 연결합니다.
        public static int TorusConnect()
        {
            try
            {
                return Torus.Torus.Instance.Connect();
            }
            catch (System.IO.FileNotFoundException ex)
            {
                MessageBox.Show(ex.ToString(), "Error");
                return -1;
            }
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
                _webServer = null;
                ButtonWebServerOpen.IsEnabled = true;
                ButtonWebServerClose.IsEnabled = false;
                TextBoxWebServerHost.IsReadOnly = false;
                TextBoxWebServerPort.IsReadOnly = false;
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender == ButtonTorusConnect)//TORUS 연결
            {
                if (TorusConnect() == 0)
                {
                    ButtonTorusConnect.IsEnabled = false;
                    ButtonWebServerOpen.IsEnabled = true;
                }
                else
                {
                    MessageBox.Show("Torus connection failed", "Error");
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
                if (!int.TryParse(TextBoxWebServerPort.Text, out int port))
                {
                    MessageBox.Show("Invalid Port", "Error");
                    return;
                }
                WebServerOpen(host, port);
                ButtonWebServerOpen.IsEnabled = false;
                ButtonWebServerClose.IsEnabled = true;
                TextBoxWebServerHost.IsReadOnly = true;
                TextBoxWebServerPort.IsReadOnly = true;
            }
            else if (sender == ButtonWebServerClose)//게이트웨이 서버 종료
            {
                _ = WebServerCloseAsync();
            }
            else if (sender == ButtonExpand)//TORUS MachineList.xml 설정 메뉴 확장/축소
            {
                if (!isExpanded)
                {
                    ButtonExpand.Content = "▲";
                    this.Height = ExpandedHeight;
                    this.Width = ExpandedWidth;
                }
                else
                {
                    ButtonExpand.Content = "▼";
                    this.Height = CollapsedHeight;
                    this.Width = CollapsedWidth;
                }
                isExpanded = !isExpanded;
            }
        }
    }
}