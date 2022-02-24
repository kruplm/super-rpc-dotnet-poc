using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace SuperRPC
{
    public class WebSocketService
    {
        public Task Start()
        {
            return Task.Run(() => CreateHostBuilder(Array.Empty<string>()).Build().Run());
        }

        public IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup(_ => new Startup()).UseUrls("http://localhost:5050");
                });
    }
}
