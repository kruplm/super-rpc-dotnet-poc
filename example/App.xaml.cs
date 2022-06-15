
using System.Windows;

namespace Super.RPC.Example;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    WebSocketService wsService;
    WebViewJSBridge jsBridge = new WebViewJSBridge();

    public App()
    {
        wsService = new WebSocketService(jsBridge);
        wsService.StartAsync();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var mainWindow = new MainWindow(jsBridge);
        jsBridge.AddWindow(mainWindow);
        mainWindow.Show();
    }
}
