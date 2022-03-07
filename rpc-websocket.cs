using System;
using System.IO;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nerdbank.Streams;

namespace SuperRPC;

public record SuperRPCWebSocket(WebSocket webSocket, object? context)
{
    public RPCReceiveChannel ReceiveChannel;

    // This is for the websocket client case. You need to call StartReceivingAsync()
    // after connecting the SuperRPC instance to SuperRPCWebSocket.ReceiveChannel
    public static SuperRPCWebSocket CreateHandler(WebSocket webSocket, object? context = null) {
        var rpcWebSocket = new SuperRPCWebSocket(webSocket, context);
        var sendAndReceiveChannel = new RPCSendAsyncAndReceiveChannel(rpcWebSocket.SendMessage);

        rpcWebSocket.sendChannel = sendAndReceiveChannel;
        rpcWebSocket.ReceiveChannel = sendAndReceiveChannel;

        return rpcWebSocket;
    }

    // This is useful when handling a server-side WebSocket connection.
    // The replyChannel will be passed to the message event automatically.
    public static Task HandleWebsocketConnectionAsync(WebSocket webSocket, RPCReceiveChannel receiveChannel, object? context = null) {
        var rpcWebSocket = new SuperRPCWebSocket(webSocket, context);
        rpcWebSocket.ReceiveChannel = receiveChannel;
        return rpcWebSocket.StartReceivingAsync();
    }

    private IRPCSendAsyncChannel? sendChannel;

    private const int ReceiveBufferSize = 4 * 1024;
    private JsonSerializer jsonSerializer = new JsonSerializer();
    private ArrayBufferWriter<byte> responseBuffer = new ArrayBufferWriter<byte>();

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

    public async Task StartReceivingAsync() {
        Debug.WriteLine("WebSocket connected");

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var messageLength = 0;

        var replyChannel = sendChannel ?? new RPCSendAsyncChannel(SendMessage);

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
                            ReceiveChannel.Received(message, replyChannel, context ?? replyChannel);
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