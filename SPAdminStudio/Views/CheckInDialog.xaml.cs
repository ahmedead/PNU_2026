using System.Windows;

namespace SPAdminStudio.Views
{
    public partial class CheckInDialog : Window
    {
        public int CheckInType { get; private set; } = 1; // Default Major
        public string Comment { get; private set; } = string.Empty;

        public CheckInDialog() : this(false)
        {
        }

        /// <param name="publishMode">
        /// Publishing always produces a major version, so hide the version-type choice and only ask for a comment.
        /// </param>
        public CheckInDialog(bool publishMode)
        {
            InitializeComponent();

            if (publishMode)
            {
                Title = "Publish Major Version";
                HeaderText.Text = "Publish Major Version";
                VersionPanel.Visibility = Visibility.Collapsed;
                BtnOk.Content = "Publish";
                RbMajor.IsChecked = true;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (RbMinor.IsChecked == true) CheckInType = 0;
            else if (RbMajor.IsChecked == true) CheckInType = 1;
            else if (RbOverwrite.IsChecked == true) CheckInType = 2;

            Comment = TxtComment.Text.Trim();
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
