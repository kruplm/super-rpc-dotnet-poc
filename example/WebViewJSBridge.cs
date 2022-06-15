using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Super.RPC.Example
{

    public record WebWindow(Window innerWindow) {
        public bool Activate() => innerWindow.Dispatcher.Invoke(() => innerWindow.Activate());
        public void Show() => innerWindow.Dispatcher.Invoke(() => innerWindow.Show());
        public void Hide() => innerWindow.Dispatcher.Invoke(() => innerWindow.Hide());

        public string Title {
            get => innerWindow.Dispatcher.Invoke(() => innerWindow.Title);
            set => innerWindow.Dispatcher.Invoke(() => innerWindow.Title = value);
        }

        public WindowState WindowState {
            get => innerWindow.Dispatcher.Invoke(() => innerWindow.WindowState);
            set => innerWindow.Dispatcher.Invoke(() => innerWindow.WindowState = value);
        }

        public double Left {
            get => innerWindow.Dispatcher.Invoke(() => innerWindow.Left);
            set => innerWindow.Dispatcher.Invoke(() => innerWindow.Left = value);
        }

        public double Top {
            get => innerWindow.Dispatcher.Invoke(() => innerWindow.Top);
            set => innerWindow.Dispatcher.Invoke(() => innerWindow.Top = value);
        }

        public void add_LocationChanged(EventHandler handler) {
            innerWindow.LocationChanged += handler;
        }

        public void remove_LocationChanged(EventHandler handler) {
            innerWindow.LocationChanged -= handler;
        }
    }
    public class WebViewJSBridge
    {

        public WebViewJSBridge()
        {
        }

        private readonly List<WebWindow> windows = new List<WebWindow>();

        public void AddWindow(Window window) {
            windows.Add(new WebWindow(window));
        }

        public List<WebWindow> GetAllWindows() => windows;
    }
}