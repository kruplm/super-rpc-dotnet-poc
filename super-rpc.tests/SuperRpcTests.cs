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

        Func<RPC_Message, object> sendSync1 = (msg) => {
            RPC_Message? replyMessage = null;
            channel2.Received(msg, new RPCSendSyncAndReceiveChannel(reply => replyMessage = reply));
            return replyMessage;
        };

        Func<RPC_Message, object> sendSync2 = (msg) => {
            RPC_Message? replyMessage = null;
            channel1.Received(msg, new RPCSendSyncAndReceiveChannel(reply => replyMessage = reply));
            return replyMessage;
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
                    counter = value;
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
        async Task Events() {
            var mockListener = new Mock<Action>();
            var completed = new TaskCompletionSource();
            mockListener.Setup(listener => listener()).Callback(() => { completed.SetResult(); });

            proxyObj.CounterChanged += mockListener.Object;
            hostObj.Counter = 5;

            await completed.Task;

            mockListener.Verify(listener => listener(), Times.Once);
        }
    }

    public class HostFunctionTests : SuperRpcTests
    {
        [Fact]
        void SyncSuccess() {
            var hostFunc = new Mock<Func<int, int>>();

            hostFunc.Setup(func => func(7)).Returns(14);

            rpc1.RegisterHostFunction("host_func", hostFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Sync });
            rpc1.SendRemoteDescriptors();

            var proxyFunc = rpc2.GetProxyFunction<Func<int, int>>("host_func");

            var result = proxyFunc(7);

            Assert.Equal(14, result);
            hostFunc.Verify(func => func(7), Times.Once);
        }

        [Fact]
        void SyncFailure() {
            var hostFunc = new Mock<Func<int, int>>();

            hostFunc.Setup(func => func(13)).Throws(new ArgumentException());

            rpc1.RegisterHostFunction("host_func", hostFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Sync });
            rpc1.SendRemoteDescriptors();

            var proxyFunc = rpc2.GetProxyFunction<Func<int, int>>("host_func");

            Assert.Throws<ArgumentException>(() => proxyFunc(13));
            hostFunc.Verify(func => func(13), Times.Once);
        }

        [Fact]
        async Task AsyncSuccess() {
            var hostFunc = new Mock<Func<int, Task<int>>>();

            hostFunc.Setup(func => func(7)).Returns(Task.FromResult(14));

            rpc1.RegisterHostFunction("host_func", hostFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Async });
            rpc1.SendRemoteDescriptors();

            var proxyFunc = rpc2.GetProxyFunction<Func<int, Task<int>>>("host_func");

            var result = await proxyFunc(7);

            Assert.Equal(14, result);
            hostFunc.Verify(func => func(7), Times.Once);
        }

        [Fact]
        async Task AsyncFailure() {
            var hostFunc = new Mock<Func<int, Task<int>>>();

            hostFunc.Setup(func => func(13)).Returns(Task.FromException<int>(new ArgumentException()));

            rpc1.RegisterHostFunction("host_func", hostFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Async });
            rpc1.SendRemoteDescriptors();

            var proxyFunc = rpc2.GetProxyFunction<Func<int, Task<int>>>("host_func");

            await Assert.ThrowsAsync<ArgumentException>(() => proxyFunc(13));
            hostFunc.Verify(func => func(13), Times.Once);
        }

        // [Fact]
        async Task PassingATask() {
            // var giveMeATask = (Func<Task<string>, Task<string>> func) => func(Task.FromException<string>(new InvalidOperationException("BOOM")));
            var giveMeATask = (Func<Task<string>, Task<string>> func) => { 
                Debug.WriteLine($"called lambda");
                return func(Task.FromException<string>(new InvalidOperationException("BOOM")));
            };
            rpc1.RegisterHostFunction("fTask", giveMeATask);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeATask = rpc2.GetProxyFunction<Func<Func<Task<string>, Task<string>>, Task<string>>>("fTask");

            await Assert.ThrowsAsync<ArgumentException>(() => proxyGiveMeATask(async (t) => "well" + await t));
        }

    }

    public class HostClassTests: SuperRpcTests 
    {
        public interface ITestClass 
        {
            // public static readonly string CONSTANT;
            // public static int Counter;

            // public static abstract TestClass CreateInstance(string name);

            public string? Name { get; set; }

            public string Color { get; set; }
            public string GetDescription();
        }
        public class TestClass : ITestClass
        {
            public static readonly string CONSTANT = "foo";
            public static int Counter = 0;

            public static TestClass CreateInstance(string name) {
                return new TestClass() { Name = name };
            }

            public string? Name { get; set; } = null;

            public TestClass()
            {
                Counter++;
            }

            public string Color { get; set; } = "blue";

            public string GetDescription() {
                return Color + " " + Name;
            }
        }

        Func<string, ITestClass> proxyClassFactory;
        TestClass testInstance;

        public HostClassTests() {
            rpc1.RegisterHostClass<TestClass>("testClass", new ClassDescriptor {
                Ctor = new FunctionDescriptor { Returns = FunctionReturnBehavior.Sync },
                // Static = new ObjectDescriptor {
                //     ReadonlyProperties = new [] { "CONSTANT" },
                //     ProxiedProperties = new PropertyDescriptor[] { "Counter" },
                //     Functions = new FunctionDescriptor[] { "CreateInstance" }
                // },
                Instance = new ObjectDescriptor {
                    ReadonlyProperties = new [] { "Name" },
                    ProxiedProperties = new PropertyDescriptor[] { "Color" },
                    Functions = new [] { new FunctionDescriptor { Name = "GetDescription", Returns = FunctionReturnBehavior.Sync } }
                }
            });
            testInstance = new TestClass { Name = "Test1" };

            rpc1.RegisterHostFunction("getInstance", () => testInstance, new FunctionDescriptor {
                Returns = FunctionReturnBehavior.Async
            });

            rpc1.SendRemoteDescriptors();
            rpc2.RegisterProxyClass<ITestClass>("testClass");

            proxyClassFactory = rpc2.GetProxyClass<ITestClass>("testClass");
        }

        [Fact]
        void Ctor() {
            TestClass.Counter = 0;

            var proxyObj = proxyClassFactory("test");
            Assert.Equal(1, TestClass.Counter);

            var proxyObj2 = proxyClassFactory("test2");
            Assert.Equal(2, TestClass.Counter);
        }

        [Fact]
        async Task ReturningAnInstance() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var instance = await getInstance();

            Assert.Equal("Test1", instance.Name);
            Assert.Equal("blue", instance.Color);
        }
        
        [Fact]
        async Task ProxiedProperty() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var instance = await getInstance();

            Assert.Equal("blue", instance.Color);

            instance.Color = "green";

            Assert.Equal("green Test1", instance.GetDescription());
        }


    }

    public class Errors : SuperRpcTests {

        [Fact]
        void NoObjectRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyObject<object>("fake"));
        }

        [Fact]
        void NoClassRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyClass<object>("fake"));
        }

        [Fact]
        void NoFunctionRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyFunction<Action>("fake"));
        }
    }
}
