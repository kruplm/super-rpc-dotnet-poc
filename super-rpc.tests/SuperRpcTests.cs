using System.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Xunit.Sdk;
using System.Reflection;

namespace Super.RPC.Tests;

// Don't want to add "Async" suffix to all async test methods, the names actually represent what the test does
#pragma warning disable VSTHRD200 



public class TestLogger : StringWriter
{
    static StreamWriter fileWriter = new StreamWriter($"testrun.log");

    public static void Init() {
        Console.SetOut(new TestLogger());
    }

    public override void WriteLine(string? message) {
        fileWriter.WriteLine(message);
        fileWriter.Flush();
    }
}

class DisplayTestMethodNameAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest)
    {
        Console.WriteLine("--- Setup for test '{0}.'", methodUnderTest.Name);
    }

    public override void After(MethodInfo methodUnderTest)
    {
        Console.WriteLine("--- TearDown for test '{0}.'", methodUnderTest.Name);
        Console.WriteLine();
    }
}


public class SuperRpcTests
{
    static SuperRpcTests() {
        TestLogger.Init();
    }

    RPCSendSyncAsyncReceiveChannel channel1;
    RPCSendSyncAsyncReceiveChannel channel2;

    SuperRPC rpc1;
    SuperRPC rpc2;

    public SuperRpcTests(bool connectChannels = true)
    {
        Func<RPC_Message, object?> sendSync1 = (msg) => {
            RPC_Message? replyMessage = null;
            channel2!.Received(msg, new RPCSendSyncAndReceiveChannel(reply => replyMessage = reply));
            return replyMessage;
        };

        Func<RPC_Message, object?> sendSync2 = (msg) => {
            RPC_Message? replyMessage = null;
            channel1!.Received(msg, new RPCSendSyncAndReceiveChannel(reply => replyMessage = reply));
            return replyMessage;
        };

        Action<RPC_Message> sendAsync1 = (msg) => Task.Run(() => channel2!.Received(msg, channel2));
        Action<RPC_Message> sendAsync2 = (msg) => Task.Run(() => channel1!.Received(msg, channel1));

        channel1 = new RPCSendSyncAsyncReceiveChannel(sendSync1, sendAsync1);
        channel2 = new RPCSendSyncAsyncReceiveChannel(sendSync2, sendAsync2);

        rpc1 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC1");
        rpc2 = new SuperRPC(() => Guid.NewGuid().ToString(), "RPC2");

        if (connectChannels) {
            rpc1.Connect(channel1);
            rpc2.Connect(channel2);
        }
    }

    public class MockChannelTests : SuperRpcTests {
        public MockChannelTests(): base(false) {}

        [Fact]
        [DisplayTestMethodName]
        void MockChannel_SyncWorks() {
            var testMsg = new RPC_GetDescriptorsMessage();
            var testReply = new RPC_DescriptorsResultMessage();

            channel1.MessageReceived += (sender, evtArgs) => {
                Assert.Equal(testMsg, evtArgs.message);
                (evtArgs.replyChannel as IRPCSendSyncChannel)!.SendSync(testReply);
            };

            var reply = channel2.SendSync(testMsg);
            Assert.Equal(testReply, reply);
        }

        [Fact]
        [DisplayTestMethodName]
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
                (evtArgs.replyChannel as IRPCSendAsyncChannel)!.SendAsync(testReply);
            };

            channel2.SendAsync(testMsg);

            return taskSource.Task;
        }

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
        [DisplayTestMethodName]
        void SyncFuncSuccess() {
            var actual = proxyObj.SyncFunc(2, 3);
            Assert.Equal(5, actual);
        }

        [Fact]
        [DisplayTestMethodName]
        void SyncFuncFail() {
            Assert.ThrowsAny<Exception>(() => proxyObj.FailSyncFunc());
        }

        [Fact]
        [DisplayTestMethodName]
        async Task AsyncFuncSuccess() {
            var result = await proxyObj.AsyncFunc("ping");
            Assert.Equal("ping pong", result);
        }

        [Fact]
        [DisplayTestMethodName]
        async Task AsyncFuncFail() {
            await Assert.ThrowsAsync<ArgumentException>(() => proxyObj.FailAsyncFunc("ping"));
        }

        [Fact]
        [DisplayTestMethodName]
        void ReadonlyProperty() {
            Assert.Equal("readonly", proxyObj.roID);
        }

        [Fact]
        [DisplayTestMethodName]
        void ProxiedProperty() {
            Assert.Equal(1, hostObj.Counter);
            Assert.Equal(1, proxyObj.Counter);

            proxyObj.Counter++;

            Assert.Equal(2, hostObj.Counter);
            Assert.Equal(2, proxyObj.Counter);
        }

        [Fact]
        [DisplayTestMethodName]
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
        [DisplayTestMethodName]
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
        [DisplayTestMethodName]
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
        [DisplayTestMethodName]
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
        [DisplayTestMethodName]
        async Task AsyncFailure() {
            var hostFunc = new Mock<Func<int, Task<int>>>();

            hostFunc.Setup(func => func(13)).Returns(Task.FromException<int>(new ArgumentException()));

            rpc1.RegisterHostFunction("host_func", hostFunc.Object, new FunctionDescriptor { Returns = FunctionReturnBehavior.Async });
            rpc1.SendRemoteDescriptors();

            var proxyFunc = rpc2.GetProxyFunction<Func<int, Task<int>>>("host_func");

            await Assert.ThrowsAsync<ArgumentException>(() => proxyFunc(13));
            hostFunc.Verify(func => func(13), Times.Once);
        }

        [Fact]
        [DisplayTestMethodName]
        async Task PassingATask_Completed() {
            var giveMeATask = async (Task<string> t) => "hello " + await t;
            rpc1.RegisterHostFunction("giveMeATask", giveMeATask);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeATask = rpc2.GetProxyFunction<Func<Task<string>, Task<string>>>("giveMeATask");

            Assert.Equal("hello world", await proxyGiveMeATask(Task.FromResult("world")));
        }
        
        [Fact]
        [DisplayTestMethodName]
        async Task PassingATask_Delayed() {
            var giveMeATask = async (Task<string> t) => "hi " + await t;
            rpc1.RegisterHostFunction("giveMeATask", giveMeATask);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeATask = rpc2.GetProxyFunction<Func<Task<string>, Task<string>>>("giveMeATask");

            Assert.Equal("hi world", await proxyGiveMeATask(Task.Delay(1).ContinueWith(t => "world")));
        }
        
        [Fact]
        [DisplayTestMethodName]
        async Task PassingATask_Error() {
            var giveMeATask = async (Task<string> t) => "hi " + await t;
            rpc1.RegisterHostFunction("giveMeATask", giveMeATask);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeATask = rpc2.GetProxyFunction<Func<Task<string>, Task<string>>>("giveMeATask");

            await Assert.ThrowsAnyAsync<ArgumentException>(() => proxyGiveMeATask(Task.FromException<string>(new InvalidOperationException("error"))));
        }

        [Fact]
        [DisplayTestMethodName]
        async Task PassingAnAsyncFunc_Success() {
            var giveMeAFunc = (Func<Task<string>, Task<string>> func) => func(Task.FromResult("result"));

            rpc1.RegisterHostFunction("asyncFunc", giveMeAFunc);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeAFunc = rpc2.GetProxyFunction<Func<Func<Task<string>, Task<string>>, Task<string>>>("asyncFunc");

            Assert.Equal("wellresult", await proxyGiveMeAFunc(async (t) => "well" + await t));
        }
        
        [Fact]
        [DisplayTestMethodName]
        async Task PassingAnAsyncFunc_Error() {
            var giveMeAFunc = (Func<Task<string>, Task<string>> func) => func(Task.FromException<string>(new InvalidOperationException("error")));

            rpc1.RegisterHostFunction("asyncFunc", giveMeAFunc);
            rpc1.SendRemoteDescriptors();

            var proxyGiveMeAFunc = rpc2.GetProxyFunction<Func<Func<Task<string>, Task<string>>, Task<string>>>("asyncFunc");

            await Assert.ThrowsAnyAsync<ArgumentException>(() => proxyGiveMeAFunc(async (t) => "well" + await t));
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

        public interface ITestContainer {
            ITestClass Nested {get; set;}
        }

        public record TestContainer(TestClass? Nested) {
            public TestContainer(): this((TestClass)null) {}
        }
        public record TestContainer2(ITestClass Nested);

        Func<string, ITestClass> proxyClassFactory;
        TestClass testInstance;
        TestClass? passedInstance;
        TestClass? passedNestedInstance;

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
            rpc1.RegisterHostClass<TestContainer>("testContainer", new ClassDescriptor {
                Instance = new ObjectDescriptor {
                    ReadonlyProperties = new [] { "Nested" }
                }
            });
            rpc1.RegisterProxyClass<TestContainer>("testContainer2");

            testInstance = new TestClass { Name = "Test1" };
            passedInstance = null;
            passedNestedInstance = null;

            rpc1.RegisterHostFunction("getInstance", () => testInstance, new FunctionDescriptor {
                Returns = FunctionReturnBehavior.Async
            });

            rpc1.RegisterHostFunction("setInstance", 
                (TestClass instance) => {
                    passedInstance = instance;
                }, 
                new FunctionDescriptor {
                    Returns = FunctionReturnBehavior.Async
                }
            );

            rpc1.RegisterHostFunction("setNestedInstance", 
                (TestContainer container) => {
                    passedNestedInstance = container.Nested;
                }, 
                new FunctionDescriptor {
                    Returns = FunctionReturnBehavior.Async
                }
            );

            rpc1.SendRemoteDescriptors();
            rpc2.RegisterProxyClass<ITestClass>("testClass");
            rpc2.RegisterHostClass<TestContainer2>("testContainer2", new ClassDescriptor {
                Instance = new ObjectDescriptor{
                    ReadonlyProperties = new [] { "Nested" }
                }
            });
            rpc2.SendRemoteDescriptors();

            proxyClassFactory = rpc2.GetProxyClass<ITestClass>("testClass");
        }

        [Fact]
        [DisplayTestMethodName]
        void Ctor() {
            TestClass.Counter = 0;

            var proxyObj = proxyClassFactory("test");
            Assert.Equal(1, TestClass.Counter);

            var proxyObj2 = proxyClassFactory("test2");
            Assert.Equal(2, TestClass.Counter);
        }

        [Fact]
        [DisplayTestMethodName]
        async Task ReturningAnInstance() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var instance = await getInstance();

            Assert.Equal("Test1", instance.Name);
            Assert.Equal("blue", instance.Color);
        }
        
        [Fact]
        [DisplayTestMethodName]
        async Task ProxiedProperty() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var instance = await getInstance();

            Assert.Equal("blue", instance.Color);

            instance.Color = "green";

            Assert.Equal("green Test1", instance.GetDescription());
        }

        [Fact]
        [DisplayTestMethodName]
        async Task SendingBackAProxyInstance() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var setInstance = rpc2.GetProxyFunction<Func<ITestClass, Task>>("setInstance");

            var instance = await getInstance();
            await setInstance(instance);

            Assert.Equal(testInstance, passedInstance);
        }

        [Fact]
        [DisplayTestMethodName]
        async Task SendingBackANestedProxyInstance() {
            var getInstance = rpc2.GetProxyFunction<Func<Task<ITestClass>>>("getInstance");
            var setNestedInstance = rpc2.GetProxyFunction<Func<TestContainer2, Task>>("setNestedInstance");

            var instance = await getInstance();
            await setNestedInstance(new TestContainer2(instance));

            Assert.Equal(testInstance, passedNestedInstance);
        }

    }

    public class Errors : SuperRpcTests {

        [Fact]
        [DisplayTestMethodName]
        void NoObjectRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyObject<object>("fake"));
        }

        [Fact]
        [DisplayTestMethodName]
        void NoClassRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyClass<object>("fake"));
        }

        [Fact]
        [DisplayTestMethodName]
        void NoFunctionRegisteredWithId() {
            Assert.Throws<ArgumentException>(() => rpc1.GetProxyFunction<Action>("fake"));
        }
    }
}
