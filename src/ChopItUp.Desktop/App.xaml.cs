namespace ChopItUp.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Row 12 T2: parse only, for now. Task 5 replaces this body with the hub
            // start-or-attach flow and the window that follows it.
            ShellArgs.Parse(Environment.GetCommandLineArgs()[1..], baseDir: AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "ChopItUp", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(2);
        }
    }
}
