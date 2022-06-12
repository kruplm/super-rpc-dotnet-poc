using System.Net.WebSockets;
using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using System.Threading;
using Castle.DynamicProxy;
using System.Collections.Generic;

namespace Super.RPC.Tests;

// Don't want to add "Async" suffix to all async test methods, the names actually represent what the test does
#pragma warning disable VSTHRD200 

public class SuperRpcWebSocketTests
{
    class WebSocketInterceptor : IInterceptor
    {
        public WebSocketInterceptor OtherInterceptor;
        public bool IsClosed { get; private set; } = false;

        public void Intercept(Castle.DynamicProxy.IInvocation invocation) {
            switch (invocation.Method.Name) {
                case "SendAsync":
                    invocation.ReturnValue = SendAsync(
                        (ReadOnlyMemory<byte>)invocation.Arguments[0],
                        (WebSocketMessageType)invocation.Arguments[1],
                        (bool)invocation.Arguments[2],
                        (CancellationToken)invocation.Arguments[3]);
                    break;
                case "ReceiveAsync":
                    invocation.ReturnValue = ReceiveAsync(
                        (Memory<byte>)invocation.Arguments[0],
                        (CancellationToken)invocation.Arguments[1]);
                    break;
                case "get_CloseStatus":
                    invocation.ReturnValue = IsClosed ? WebSocketCloseStatus.NormalClosure : null;
                    break;
            }
        }

        TaskCompletionSource<ValueWebSocketReceiveResult>? receiveCompletionSource;
        Memory<byte> receiveBuffer;
        int receiveBufferWritten;

        private void WriteToBuffer(ReadOnlyMemory<byte> buffer, bool endOfMessage) {
            buffer.CopyTo(receiveBuffer.Slice(receiveBufferWritten));
            receiveBufferWritten += buffer.Length;
            receiveCompletionSource?.SetResult(new ValueWebSocketReceiveResult(receiveBufferWritten, WebSocketMessageType.Text, endOfMessage));
            receiveCompletionSource = null;
        }

        private ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType msgType, bool endOfMessage, CancellationToken token) {
            OtherInterceptor.WriteToBuffer(buffer, endOfMessage);
            return ValueTask.CompletedTask;
        }

        private ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
            receiveCompletionSource = new TaskCompletionSource<ValueWebSocketReceiveResult>();
            receiveBuffer = buffer;
            receiveBufferWritten = 0;
            return new ValueTask<ValueWebSocketReceiveResult>(receiveCompletionSource.Task);
        }

        public void Close() {
            IsClosed = true;
            OtherInterceptor.receiveCompletionSource?.SetResult(new ValueWebSocketReceiveResult(OtherInterceptor.receiveBufferWritten, WebSocketMessageType.Close, true));
        }
    }

    static ProxyGenerator proxyGenerator = new ProxyGenerator();

    WebSocketInterceptor interceptor1;
    WebSocketInterceptor interceptor2;
    SuperRPC rpc1;
    SuperRPC rpc2;
    Task receiveTask1;
    Task receiveTask2;

    public SuperRpcWebSocketTests()
    {
        interceptor1 = new WebSocketInterceptor();
        interceptor2 = new WebSocketInterceptor();
        interceptor1.OtherInterceptor = interceptor2;
        interceptor2.OtherInterceptor = interceptor1;
        var mockWebsocket1 = proxyGenerator.CreateClassProxy<WebSocket>(interceptor1);
        var mockWebsocket2 = proxyGenerator.CreateClassProxy<WebSocket>(interceptor2);

        var rpcWs1 = SuperRPCWebSocket.CreateHandler(mockWebsocket1);
        var rpcWs2 = SuperRPCWebSocket.CreateHandler(mockWebsocket2);

        rpc1 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC1");
        rpc2 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC2");

        rpc1.Connect(rpcWs1.ReceiveChannel);
        rpc2.Connect(rpcWs2.ReceiveChannel);

        receiveTask1 = rpcWs1.StartReceivingAsync();
        receiveTask2 = rpcWs2.StartReceivingAsync();
    }

    Task WaitForClosing() {
        interceptor1.Close();
        interceptor2.Close();
        return Task.WhenAll(receiveTask1, receiveTask2);
    }
    
    [Fact]
    async Task AsyncProxyFunctionWorks() {
        var mockFunc = new Mock<Func<string, Task>>();
        mockFunc.Setup(x => x(It.IsAny<string>())).Returns(Task.CompletedTask);

        rpc1.RegisterHostFunction("func1", mockFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Async });
        await rpc2.RequestRemoteDescriptors();

        var proxyFunc = rpc2.GetProxyFunction<Func<string, Task>>("func1");
        await proxyFunc("hello");

        mockFunc.Verify(f => f("hello"), Times.Once);

        await WaitForClosing();
    }

    record CustomObject (string Name);

    async Task TestCustomObjectDeserialization() {
        var wasCalled = false;

        rpc1.RegisterHostFunction("func2", (CustomObject obj) => {
            Assert.Equal("TestObject", obj.Name);
            wasCalled = true;
        });
        rpc1.RegisterHostClass<CustomObject>("customObject", new ClassDescriptor {
            Instance = new ObjectDescriptor {
                ReadonlyProperties = new [] { "Name" }
            }
        });

        await rpc2.RequestRemoteDescriptors();

        var proxyFunc = rpc2.GetProxyFunction<Func<CustomObject, Task>>("func2");

        await proxyFunc(new CustomObject("TestObject"));

        Assert.True(wasCalled);
    }

    [Fact]
    async Task RegisterCustomDeserializerWorks() {
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc1);
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc2);

        await TestCustomObjectDeserialization();
    }

    [Fact]
    async Task CustomObjectDeserializationFails() {
        await Assert.ThrowsAnyAsync<ArgumentException>(TestCustomObjectDeserialization);
    }

    [Fact]
    async Task ArrayDeserialization() {
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc1);
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc2);

        var wasCalled = false;

        rpc1.RegisterHostFunction("func3", (string[] names) => {
            Assert.Equal(2, names.Length);
            Assert.Equal("John", names[0]);
            Assert.Equal("Sarah", names[1]);
            wasCalled = true;
        });

        await rpc2.RequestRemoteDescriptors();

        var proxyFunc = rpc2.GetProxyFunction<Func<string[], Task>>("func3");

        await proxyFunc(new [] { "John", "Sarah" });

        Assert.True(wasCalled);
    }

    [Fact]
    async Task ListDeserialization() {
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc1);
        SuperRPCWebSocket.RegisterCustomDeserializer(rpc2);
        
        var wasCalled = false;

        rpc1.RegisterHostFunction("func3", (List<string> names) => {
            Assert.Equal(2, names.Count);
            Assert.Equal("John", names[0]);
            Assert.Equal("Sarah", names[1]);
            wasCalled = true;
        });

        await rpc2.RequestRemoteDescriptors();

        var proxyFunc = rpc2.GetProxyFunction<Func<List<string>, Task>>("func3");

        await proxyFunc(new List<string> { "John", "Sarah" });

        Assert.True(wasCalled);
    }
}
