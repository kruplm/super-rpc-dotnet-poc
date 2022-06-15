using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Super.RPC.Example
{
    public class WebSocketService
    {
        private WebViewJSBridge jsBridge;

        public WebSocketService(WebViewJSBridge jsBridge)
        {
            this.jsBridge = jsBridge;
        }

        public Task StartAsync()
        {
            return Task.Run(() => CreateHostBuilder(Array.Empty<string>()).Build().Run());
        }

        public IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices(serviceCollection => {
                    serviceCollection.AddSingleton(jsBridge);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup(_ => new Startup()).UseUrls("http://localhost:5050");
                });
    }
}
