using Microsoft.Web.WebView2.Core;

using System.Windows;


namespace Super.RPC.Example
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private WebViewJSBridge jsBridge;

        public MainWindow(WebViewJSBridge jsBridge)
        {
            InitializeComponent();
            InitializeAsync();
        }

        async void InitializeAsync()
        {
            await webView.EnsureCoreWebView2Async(null);
            webView.CoreWebView2.NewWindowRequested += (object sender, CoreWebView2NewWindowRequestedEventArgs args) => {
                var x = args;
            };
            // webView.CoreWebView2.AddHostObjectToScript("bridge", jsBridge);
            // webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.bridge = chrome.webview.hostObjects.bridge;");
            // webView.CoreWebView2.WebMessageReceived += MessageReceived;

        }

        public void MoveWindow(int x, int y)
        {
            Left = x;
            Top = y;
        }
    }
}