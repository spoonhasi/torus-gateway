using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace TorusGateway
{
    public partial class TestRequestWindow : Window
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public TestRequestWindow(string host, string port)
        {
            InitializeComponent();
            // Host가 0.0.0.0 등 와일드카드면 클라이언트 접속용으로 127.0.0.1로 대체
            string clientHost = (host == "0.0.0.0" || string.IsNullOrWhiteSpace(host) || host == "*")
                ? "127.0.0.1" : host;
            TextBoxBaseUrl.Text = $"http://{clientHost}:{port}";
            TextBoxPath.Text = "/";
            UpdateBodyVisibility();
        }

        private void ComboMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBodyVisibility();
        }

        private string SelectedMethod =>
            (ComboMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        private void UpdateBodyVisibility()
        {
            if (!IsLoaded && TextBoxBody == null) return;
            string method = SelectedMethod;
            bool isPut = method == "PUT";
            TextBoxBody.IsEnabled = isPut;
            LabelBody.Opacity = isPut ? 1.0 : 0.4;
            LabelBody.Text = isPut
                ? "Request Body (JSON) - TORUS Item, value is an array, e.g., {\"value\": [25.5]}"
                : $"Request Body (JSON) - disabled for {method}";

            // PUT 선택 시 body가 비어있으면 템플릿을 제공하고 커서를 배열 안으로 이동
            if (isPut && string.IsNullOrWhiteSpace(TextBoxBody.Text))
            {
                const string template = "{\"value\": []}";
                TextBoxBody.Text = template;
                TextBoxBody.CaretIndex = template.Length - 2; // '[' 와 ']' 사이
                TextBoxBody.Focus();
            }
        }

        private async void ButtonSend_Click(object sender, RoutedEventArgs e)
        {
            string method = SelectedMethod;
            string baseUrl = TextBoxBaseUrl.Text.TrimEnd('/');
            string path = TextBoxPath.Text.Trim();
            if (!path.StartsWith("/")) path = "/" + path;
            string url = baseUrl + path;

            ButtonSend.IsEnabled = false;
            TextBoxResponse.Text = "Sending...";
            try
            {
                HttpResponseMessage resp;
                if (method == "PUT")
                {
                    var content = new StringContent(TextBoxBody.Text ?? "", Encoding.UTF8, "application/json");
                    resp = await _http.PutAsync(url, content);
                }
                else if (method == "DELETE")
                {
                    resp = await _http.DeleteAsync(url);
                }
                else
                {
                    resp = await _http.GetAsync(url);
                }
                string body = await resp.Content.ReadAsStringAsync();
                TextBoxResponse.Text = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\r\n\r\n{body}";
            }
            catch (Exception ex)
            {
                TextBoxResponse.Text = $"Request failed: {ex.Message}";
            }
            finally
            {
                ButtonSend.IsEnabled = true;
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
