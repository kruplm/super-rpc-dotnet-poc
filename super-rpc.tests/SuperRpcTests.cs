using System.Diagnostics;
using System;
using System.Threading.Tasks;
using Xunit;
using Moq;

namespace Super.RPC.Tests;

public class SuperRpcTests
{
    RPCSendSyncAsyncReceiveChannel channel1;
    RPCSendSyncAsyncReceiveChannel channel2;

    SuperRPC rpc1;
    SuperRPC rpc2;

    public SuperRpcTests()
    {
        RPC_Message? channel1SyncReplyMessage = null;
        RPC_Message? channel2SyncReplyMessage = null;

        Func<RPC_Message, object> sendSync1 = (msg) => {
            channel2.Received(msg, new RPCSendSyncAndReceiveChannel(reply => channel1SyncReplyMessage = reply));
            return channel1SyncReplyMessage;
        };

        Func<RPC_Message, object> sendSync2 = (msg) => {
            channel1.Received(msg, new RPCSendSyncAndReceiveChannel(reply => channel2SyncReplyMessage = reply));
            return channel2SyncReplyMessage;
        };

        Action<RPC_Message> sendAsync1 = (msg) => Task.Run(() => channel2.Received(msg));
        Action<RPC_Message> sendAsync2 = (msg) => Task.Run(() => channel1.Received(msg));

        channel1 = new RPCSendSyncAsyncReceiveChannel(sendSync1, sendAsync1);
        channel2 = new RPCSendSyncAsyncReceiveChannel(sendSync2, sendAsync2);

        rpc1 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC1");
        rpc2 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC2");

        rpc1.Connect(channel1);
        rpc2.Connect(channel2);
    }

    [Fact]
    void MockChannel_SyncWorks() {
        var testMsg = new RPC_GetDescriptorsMessage();
        var testReply = new RPC_DescriptorsResultMessage();

        channel1.MessageReceived += (sender, evtArgs) => {
            Assert.Equal(testMsg, evtArgs.message);
            (evtArgs.replyChannel as IRPCSendSyncChannel).SendSync(testReply);
        };

        var reply = channel2.SendSync(testMsg);
        Assert.Equal(testReply, reply);
    }

    // [Fact]
    Task MockChannel_AsyncWorks() {
        var taskSource = new TaskCompletionSource();

        var testMsg = new RPC_GetDescriptorsMessage();
        var testReply = new RPC_DescriptorsResultMessage();

        channel2.MessageReceived += (sender, evtArgs) => {
            Assert.Equal(testReply, evtArgs.message);
            taskSource.SetResult();
        };

        channel1.MessageReceived += (sender, evtArgs) => {
            Assert.Equal(testMsg, evtArgs.message);
            (evtArgs.replyChannel as IRPCSendAsyncChannel).SendAsync(testReply);
        };

        channel2.SendAsync(testMsg);

        return taskSource.Task;
    }

    public class HostObjectTests : SuperRpcTests
    {
        public interface IHostObject {
            int SyncFunc(int a, int b);
            void FailSyncFunc();
            Task<string> AsyncFunc(string ping);
            Task<string> FailAsyncFunc(string ping);
            string roID { get; }
            int Counter { get; set; }

            event Action CounterChanged;
        }

        public class HostObject : IHostObject
        {
            public string roID => "readonly";

            private int counter = 1;

            public int Counter { 
                get => counter; 
                set {
                    counter++;
                    CounterChanged?.Invoke();
                }
            }

            public event Action? CounterChanged;

            public async Task<string> AsyncFunc(string ping)
            {
                await Task.Delay(10);
                return ping + " pong";
            }

            public async Task<string> FailAsyncFunc(string ping)
            {
                await Task.Delay(10);
                throw new InvalidOperationException(ping + "ooops");
            }

            public void FailSyncFunc()
            {
                throw new InvalidOperationException("No");
            }

            public int SyncFunc(int a, int b)
            {
                return a + b;
            }
            
        }

        HostObject hostObj;
        IHostObject proxyObj;

        public HostObjectTests()
        {
            hostObj = new HostObject();

            rpc1.RegisterHostObject("host_obj", hostObj, new ObjectDescriptor {
                ReadonlyProperties = new [] { "roID" },
                ProxiedProperties = new PropertyDescriptor[] { "Counter" },
                Functions = new [] {
                    new FunctionDescriptor { Name = "SyncFunc", Returns = FunctionReturnBehavior.Sync },
                    new FunctionDescriptor { Name = "FailSyncFunc", Returns = FunctionReturnBehavior.Sync },
                    new FunctionDescriptor { Name = "AsyncFunc", Returns = FunctionReturnBehavior.Async },
                    new FunctionDescriptor { Name = "FailAsyncFunc", Returns = FunctionReturnBehavior.Async },
                },
                Events = new FunctionDescriptor[] { "CounterChanged" }
            });

            rpc1.SendRemoteDescriptors();

            proxyObj = rpc2.GetProxyObject<IHostObject>("host_obj");
        }

        [Fact]
        void SyncFuncSuccess() {
            var actual = proxyObj.SyncFunc(2, 3);
            Assert.Equal(5, actual);
        }

        [Fact]
        void SyncFuncFail() {
            Assert.ThrowsAny<Exception>(() => proxyObj.FailSyncFunc());
        }

        [Fact]
        async Task AsyncFuncSuccess() {
            var result = await proxyObj.AsyncFunc("ping");
            Assert.Equal("ping pong", result);
        }

        [Fact]
        async Task AsyncFuncFail() {
            await Assert.ThrowsAsync<ArgumentException>(() => proxyObj.FailAsyncFunc("ping"));
        }

        [Fact]
        void ReadonlyProperty() {
            Assert.Equal("readonly", proxyObj.roID);
        }

        [Fact]
        void ProxiedProperty() {
            Assert.Equal(1, hostObj.Counter);
            Assert.Equal(1, proxyObj.Counter);

            proxyObj.Counter++;

            Assert.Equal(2, hostObj.Counter);
            Assert.Equal(2, proxyObj.Counter);
        }

        [Fact]
        void Events() {
            var mockListener = new Mock<Action>();

            proxyObj.CounterChanged += mockListener.Object;
            hostObj.Counter = 5;

            mockListener.Verify(listener => listener(), Times.Once);
        }
    }
}
