using Avalonia;
using Avalonia.Markup.Xaml;

namespace ChromeAutomation.CpmsExport.Ui;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        new MainWindow().Show();
        base.OnFrameworkInitializationCompleted();
    }
}
