using System.Windows;

namespace TorusGateway
{
    public partial class DocsWindow : Window
    {
        public DocsWindow()
        {
            InitializeComponent();
            TextBoxDocs.Text = WebServer.CustomController.LoadHelpText()
                ?? "Help document resource not found.";
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
