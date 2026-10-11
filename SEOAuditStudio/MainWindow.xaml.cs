using System.ComponentModel;
using System.Windows;
using SEOAuditStudio.ViewModels;

namespace SEOAuditStudio;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    /// <summary>Closing during an audit offers to stop it and save the pages crawled so far (it can be continued later).</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainViewModel { IsRunning: true } vm)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (await vm.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
