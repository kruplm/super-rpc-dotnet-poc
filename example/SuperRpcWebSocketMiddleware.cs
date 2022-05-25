using System;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Super.RPC;

namespace Super.RPC.Example;

public class SuperRpcWebSocketMiddleware 
{
    private readonly RequestDelegate next;

    MySerive service = new MySerive();


    public interface IService {
       Task<int> Add(int a, int b);
    }

    public class CustomDTO {
        public string Name { get; set; }
    }

    public interface ITestProxyService {
        Task<int> Counter { get; set; }
        void Increment();
        event Action<int> CounterChanged;
    }

    public SuperRpcWebSocketMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    private void SetupRPC(RPCReceiveChannel channel) {
        var rpc = new SuperRPC(() => Guid.NewGuid().ToString("N"));

        SuperRPCWebSocket.RegisterCustomDeserializer(rpc);

        rpc.Connect(channel);

        // register host objects here

        rpc.RegisterHostObject("service", service, new ObjectDescriptor {
            Functions = new FunctionDescriptor[] {
                "Add", "Increment", "GetName",
                new FunctionDescriptor { Name = "TakeAList", Returns = FunctionReturnBehavior.Void },
                new FunctionDescriptor { Name = "TakeArray", Returns = FunctionReturnBehavior.Void },
                new FunctionDescriptor { Name = "TakeADictionary", Returns = FunctionReturnBehavior.Void },
                new FunctionDescriptor { Name = "LogMsgLater", Returns = FunctionReturnBehavior.Void },
                new FunctionDescriptor {
                    Name = "CallMeLater",
                    Arguments = new [] {
                        new ArgumentDescriptor { idx = 0, Returns = FunctionReturnBehavior.Void }
                    } 
                }
            },
            ProxiedProperties = new PropertyDescriptor[] { "Counter" },
            Events = new FunctionDescriptor[] { "CounterChanged" }
        });

        rpc.RegisterHostFunction("squareIt", (int x) => "Hey, can you see me?");

        rpc.RegisterHostFunction("testDTO", (CustomDTO x) => Debug.WriteLine($"Custom DTO name: {x.Name}"));

        rpc.RegisterProxyClass<ITestProxyService>("TestService");
        rpc.RegisterHostFunction("testJsHost", () => {
            var jsFunc = rpc.GetProxyFunction<Func<string, string, Task<string>>>("jsFunc", rpc.CurrentContext);
            var rs = jsFunc("hello", "world");
            rs.ContinueWith( t => Console.WriteLine("JS func call: {0}", t.Result));

            var jsObj = rpc.GetProxyObject<IService>("jsObj", rpc.CurrentContext.replyChannel);
            var result = jsObj.Add(5, 6);
            result.ContinueWith( t => Console.WriteLine("JS object method: {0}", t.Result));


            rpc.RegisterProxyClass<IService>("JsService");
            
            var getJsService = rpc.GetProxyFunction<Func<Task<IService>>>("getJsService", rpc.CurrentContext);
            getJsService().ContinueWith(jsService => {
                jsService.Result.Add(7, 8).ContinueWith( t => Console.WriteLine("JS class: {0}", t.Result));
            });


            var getTestService = rpc.GetProxyFunction<Func<Task<ITestProxyService>>>("getTestService", rpc.CurrentContext);
            getTestService().ContinueWith(async (testServiceTask) => {
                var service = testServiceTask.Result;
                var listener = (int counterValue) => {
                    System.Console.WriteLine($"CounterChanged: {counterValue}");
                };
                service.CounterChanged += listener;
                Console.WriteLine($"TestService counter={await service.Counter}");
                service.Increment();
                Console.WriteLine($"TestService counter={await service.Counter}");
                service.Counter = Task.FromResult(8);
                service.CounterChanged -= listener;
            });
        });

        rpc.RegisterHostClass<MySerive>("MyService", new ClassDescriptor {
            Ctor = new FunctionDescriptor {},
            Static = new ObjectDescriptor {
                Functions = new FunctionDescriptor[] { "Mul" },
                ProxiedProperties = new PropertyDescriptor[] { "StaticCounter" }
            },
            Instance = new ObjectDescriptor {
                Functions = new FunctionDescriptor[] { "Add", "Increment", "GetName", "LogMsgLater" },
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
                    var rpcWebSocketHandler = SuperRPCWebSocket.CreateHandler(webSocket);
                    SetupRPC(rpcWebSocketHandler.ReceiveChannel);
                    await rpcWebSocketHandler.StartReceivingAsync();
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
