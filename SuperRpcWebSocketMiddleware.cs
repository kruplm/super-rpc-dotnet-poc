using System.IO;
using System.Dynamic;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nerdbank.Streams;
using System.Text;

namespace SuperRPC
{
    public class SuperRpcWebSocketMiddleware : RPCChannelReceive
    {
        private readonly RequestDelegate next;

        private const int ReceiveBufferSize = 4 * 1024;

        private JsonSerializer jsonSerializer = new JsonSerializer();

        MySerive service = new MySerive();

        private SuperRPC rpc;
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        public SuperRpcWebSocketMiddleware(RequestDelegate next, SuperRPC rpc)
        {
            this.next = next;
            this.rpc = rpc;
            rpc.Connect(this);

              // register host objects here
            
            rpc.RegisterHostObject("service", service, new ObjectDescriptor {
                Functions = new FunctionDescriptor[] { "Add", "Increment" },
                ProxiedProperties = new PropertyDescriptor[] { "Counter" }
            });

            rpc.RegisterHostFunction("squareIt", (int x) => "Hey, can you see me?");

            rpc.RegisterHostFunction("testJsHost", () => {
                var jsFunc = rpc.GetProxyFunction<Func<string, string, Task<string>>>("jsFunc", (RPCChannel?)rpc.CurrentContext);
                var rs = jsFunc("hello", "world");
                rs.ContinueWith( t => Console.WriteLine("JS func call: {0}", t.Result));

                var jsObj = rpc.GetProxyObject<IService>("jsObj", (RPCChannel?)rpc.CurrentContext);
                var result = jsObj.Add(5, 6);
                result.ContinueWith( t => Console.WriteLine("JS object method: {0}", t.Result));

                var jsServiceFactory = rpc.CreateProxyClass<IService>("JsService", (RPCChannel?)rpc.CurrentContext);
                var jsService = jsServiceFactory("12345");
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
                        await HandleConnection(context, webSocket);
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

        public interface IService {
            Task<int> Add(int a, int b);
        }

        private async Task HandleConnection(HttpContext context, WebSocket webSocket)
        {
            Debug.WriteLine("WebSocket connected");

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var messageLength = 0;
            var responseBuffer = new ArrayBufferWriter<byte>();

            async void SendMessage(RPC_Message message) {
                try {
                    TextWriter textWriter = new StreamWriter(responseBuffer.AsStream());
                    jsonSerializer.Serialize(textWriter, message);
                    await textWriter.FlushAsync();
                    await webSocket.SendAsync(responseBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);
                    responseBuffer.Clear();
                } catch (Exception e) {
                    Debug.WriteLine("Error during SendMessage " + e.ToString());
                }
            }

            var replyChannel = new RPCSendAsyncChannel(SendMessage);

            while (!webSocket.CloseStatus.HasValue)
            {
                var mem = pipe.Writer.GetMemory(ReceiveBufferSize);

                var receiveResult = await webSocket.ReceiveAsync(mem, CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Close) break;

                messageLength += receiveResult.Count;
                pipe.Writer.Advance(receiveResult.Count);

                if (receiveResult.EndOfMessage)
                {
                    await pipe.Writer.FlushAsync();
                    while (pipe.Reader.TryRead(out var readResult))
                    {
                        if (readResult.Buffer.Length >= messageLength)
                        {
                            var messageBuffer = readResult.Buffer.Slice(readResult.Buffer.Start, messageLength);
                            var message = ParseMessage(messageBuffer);
                            if (message != null) {
                                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message, replyChannel, replyChannel));
                            }
                            pipe.Reader.AdvanceTo(messageBuffer.End);
                            messageLength = 0;
                            break;
                        }

                        if (readResult.IsCompleted) break;
                    }
                }
            }

            Debug.WriteLine($"WebSocket closed with status {webSocket.CloseStatus} {webSocket.CloseStatusDescription}");
        }


        private RPC_Message? ParseMessage(ReadOnlySequence<byte> messageBuffer)
        {
                var jsonReader = new JsonTextReader(new SequenceTextReader(messageBuffer, Encoding.UTF8));
                var obj = jsonSerializer.Deserialize<JObject>(jsonReader);

                if (obj == null) {
                    throw new InvalidOperationException("Received data is not JSON");
                }

                var action = obj["action"]?.Value<String>();
                if (action == null) {
                    throw new ArgumentNullException("The action field is null.");
                }

                Type? messageType;
                if (RPC_Message.MessageTypesByAction.TryGetValue(action, out messageType) && messageType != null) {
                    return (RPC_Message?)obj.ToObject(messageType);
                }

                throw new ArgumentException($"Invalid action value {action}");
        }
    }
}
