using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SuperRPC;

public class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
    public void ConfigureServices(IServiceCollection services)
    {
        var rpc = new SuperRPC(() => Guid.NewGuid().ToString("N"));
        services.AddSingleton(rpc);
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseWebSockets();

        app.UseMiddleware<SuperRpcWebSocketMiddleware>();
    }
}
