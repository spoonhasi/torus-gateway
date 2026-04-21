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

        private void UpdateBodyVisibility()
        {
            if (!IsLoaded && TextBoxBody == null) return;
            bool isPost = (ComboMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() == "POST";
            TextBoxBody.IsEnabled = isPost;
            LabelBody.Opacity = isPost ? 1.0 : 0.4;
            LabelBody.Text = isPost
                ? "Request Body (JSON) - TORUS expects the lowercase \"value\" key, e.g., {\"value\": 25.5}"
                : "Request Body (JSON) - disabled for GET";

            // POST 선택 시 body가 비어있으면 템플릿을 제공하고 커서를 값 위치로 이동
            if (isPost && string.IsNullOrWhiteSpace(TextBoxBody.Text))
            {
                const string template = "{\"value\": }";
                TextBoxBody.Text = template;
                // "value": 다음 공백 위치에 커서
                TextBoxBody.CaretIndex = template.Length - 1;
                TextBoxBody.Focus();
            }
        }

        private async void ButtonSend_Click(object sender, RoutedEventArgs e)
        {
            string method = (ComboMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";
            string baseUrl = TextBoxBaseUrl.Text.TrimEnd('/');
            string path = TextBoxPath.Text.Trim();
            if (!path.StartsWith("/")) path = "/" + path;
            string url = baseUrl + path;

            ButtonSend.IsEnabled = false;
            TextBoxResponse.Text = "Sending...";
            try
            {
                HttpResponseMessage resp;
                if (method == "POST")
                {
                    var content = new StringContent(TextBoxBody.Text ?? "", Encoding.UTF8, "application/json");
                    resp = await _http.PostAsync(url, content);
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
