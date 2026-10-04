using System.Windows;

namespace SPAdminStudio.Views
{
    public partial class NewFolderDialog : Window
    {
        public string FolderName { get; private set; } = string.Empty;

        public NewFolderDialog()
        {
            InitializeComponent();
            TxtFolderName.Focus();
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            var name = TxtFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter a valid folder name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FolderName = name;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
