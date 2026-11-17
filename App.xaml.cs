using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PrintNest;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { MessageBox.Show(a.Exception.Message, "印巢 · 遇到问题", MessageBoxButton.OK, MessageBoxImage.Warning); a.Handled = true; };
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(Color.FromRgb(15, 118, 110));
        if (e.Args.Contains("--self-test"))
        {
            try { await SelfTest.Run(e.Args.Last()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args.Last(), "test-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (e.Args.Length > 0) await window.ImportAsync(e.Args.Where(File.Exists));
        }
        catch (Exception ex)
        {
            string log = Path.Combine(Path.GetTempPath(), "PrintNest-startup-error.txt");
            File.WriteAllText(log, ex.ToString());
            MessageBox.Show("启动失败：" + ex.Message + "\n详细信息：" + log, "印巢", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
