using System;

using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;


namespace SuperRPC;

public class SuperRpcWebSocketMiddleware 
{
    private readonly RequestDelegate next;
    private readonly SuperRPC rpc;
    private readonly RPCReceiveChannel receiveChannel;

    MySerive service = new MySerive();


    public interface IService {
        Task<int> Add(int a, int b);
    }

    public SuperRpcWebSocketMiddleware(RequestDelegate next, SuperRPC rpc)
    {
        this.next = next;
        this.rpc = rpc;

        receiveChannel = new RPCReceiveChannel();
        rpc.Connect(receiveChannel);

          // register host objects here
        
        rpc.RegisterHostObject("service", service, new ObjectDescriptor {
            Functions = new FunctionDescriptor[] { "Add", "Increment" },
            ProxiedProperties = new PropertyDescriptor[] { "Counter" }
        });

        rpc.RegisterHostFunction("squareIt", (int x) => "Hey, can you see me?");

        rpc.RegisterHostFunction("testJsHost", () => {
            var jsFunc = rpc.GetProxyFunction<Func<string, string, Task<string>>>("jsFunc", (IRPCChannel?)rpc.CurrentContext);
            var rs = jsFunc("hello", "world");
            rs.ContinueWith( t => Console.WriteLine("JS func call: {0}", t.Result));

            var jsObj = rpc.GetProxyObject<IService>("jsObj", (IRPCChannel?)rpc.CurrentContext);
            var result = jsObj.Add(5, 6);
            result.ContinueWith( t => Console.WriteLine("JS object method: {0}", t.Result));

            // var getJsService = rpc.GetProxyFunction<Func<Task<IService>>>("getJsService", (RPCChannel?)rpc.CurrentContext);
            // getJsService().ContinueWith(jsService => {
            //     jsService.Result.Add(7, 8).ContinueWith( t => Console.WriteLine("JS class: ", t.Result));
            // });

            // var jsServiceFactory = rpc.CreateProxyClass<IService>("JsService", (RPCChannel?)rpc.CurrentContext);
            // var jsService = jsServiceFactory("JsService");
            // jsService.Add(7, 8).ContinueWith( t => Console.WriteLine("JS class: ", t.Result));
        });

        rpc.RegisterHostClass("MyService", typeof(MySerive), new ClassDescriptor {
            Ctor = new FunctionDescriptor {},
            Static = new ObjectDescriptor {
                Functions = new FunctionDescriptor[] { "Mul" },
                ProxiedProperties = new PropertyDescriptor[] { "StaticCounter" }
            },
            Instance = new ObjectDescriptor {
                Functions = new FunctionDescriptor[] { "Add", "Increment" },
                ProxiedProperties = new PropertyDescriptor[] { "Counter" }
            }
        });
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/super-rpc")
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                {
                    await SuperRPCWebSocket.HandleWebsocketClientConnection(webSocket, receiveChannel);
                }
            }
            else
            {
                context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
            }
            return;
        }
        await next(context);
    }

}
