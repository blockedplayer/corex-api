namespace CoreX.Loader;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"CoreX Loader encountered an error:\n\n{args.Exception.Message}\n\nPlease report this to the developer.",
                "CORE X — Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is System.Exception ex)
                System.Windows.MessageBox.Show(
                    $"CoreX Loader crashed:\n\n{ex.Message}\n\nPlease report this to the developer.",
                    "CORE X — Fatal", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };
        base.OnStartup(e);
    }
}
