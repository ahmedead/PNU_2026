using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SEOAuditStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Lets pages declared as windows-1256, iso-8859-x, shift_jis... decode correctly
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
