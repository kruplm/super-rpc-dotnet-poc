using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System;

using ObjectDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ObjectDescriptor>;
using FunctionDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.FunctionDescriptor>;
using ClassDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ClassDescriptor>;

using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Castle.DynamicProxy;

namespace SuperRPC
{
    record ClassRegistryEntry(Type classType, ClassDescriptor descriptor);
    record HostObjectRegistryEntry(object target, ObjectDescriptor? descriptor);
    record HostFunctionRegistryEntry(Delegate target, FunctionDescriptor descriptor);
    record AsyncCallbackEntry(TaskCompletionSource<object?> complete, Type? type);

    public class SuperRPC
    {
        public object? CurrentContext;

        protected readonly Func<string> ObjectIDGenerator;
        protected RPCChannel Channel;

        private ObjectDescriptors remoteObjectDescriptors;
        private FunctionDescriptors remoteFunctionDescriptors;
        private ClassDescriptors remoteClassDescriptors;
        private TaskCompletionSource<bool>? remoteDescriptorsReceived = null;

        // private readonly proxyObjectRegistry = new ProxyObjectRegistry();
        private readonly Dictionary<string, Type> proxyClassRegistry = new Dictionary<string, Type>();
        private readonly Dictionary<string, HostObjectRegistryEntry> hostObjectRegistry = new Dictionary<string, HostObjectRegistryEntry>();
        private readonly Dictionary<string, HostFunctionRegistryEntry> hostFunctionRegistry = new Dictionary<string, HostFunctionRegistryEntry>();
        private readonly Dictionary<string, ClassRegistryEntry> hostClassRegistry = new Dictionary<string, ClassRegistryEntry>();

        private readonly ProxyGenerator proxyGenerator = new ProxyGenerator();

        private int callId = 0;
        private readonly Dictionary<string, AsyncCallbackEntry> asyncCallbacks = new Dictionary<string, AsyncCallbackEntry>();

        public SuperRPC(Func<string> objectIdGenerator) {
            ObjectIDGenerator = objectIdGenerator;
        }

        public void Connect(RPCChannel channel) {
            Channel = channel;
            if (channel is RPCChannelReceive receiveChannel) {
                receiveChannel.MessageReceived += MessageReceived;
            }
        }

        public void RegisterHostObject(string objId, object target, ObjectDescriptor descriptor) {
            hostObjectRegistry.Add(objId, new HostObjectRegistryEntry(target, descriptor));
        }

        public void RegisterHostFunction(string objId, Delegate target, FunctionDescriptor? descriptor = null) {
            descriptor ??= new FunctionDescriptor();
            hostFunctionRegistry.Add(objId, new HostFunctionRegistryEntry(target, descriptor));
        }

        public void RegisterHostClass(string classId, Type clazz, ClassDescriptor descriptor) {
            descriptor.ClassId = classId;
            if (descriptor.Static is not null) {
                RegisterHostObject(classId, clazz, descriptor.Static);
            }

            hostClassRegistry.Add(classId, new ClassRegistryEntry(clazz, descriptor));
        }

        protected void MessageReceived(object sender, MessageReceivedEventArgs eventArgs) {
            var message = eventArgs.message;
            var replyChannel = eventArgs.replyChannel ?? Channel;
            CurrentContext = eventArgs.context;

            if (message.rpc_marker != "srpc") return;   // TODO: throw?

            switch (message) {
                case RPC_GetDescriptorsMessage: SendRemoteDescriptors(replyChannel);
                    break;
                case RPC_DescriptorsResultMessage descriptors:
                    SetRemoteDescriptors(descriptors);
                    if (remoteDescriptorsReceived is not null) {
                        remoteDescriptorsReceived.SetResult(true);
                        remoteDescriptorsReceived = null;
                    }
                    break;
                case RPC_AnyCallTypeFnCallMessage functionCall:
                    CallTargetFunction(functionCall, replyChannel);
                    break;
                case RPC_ObjectDiedMessage objectDied:
                    hostObjectRegistry.Remove(objectDied.objId);
                    break;
                case RPC_FnResultMessageBase fnResult: {
                    if (fnResult.callType == FunctionReturnBehavior.Async) {
                        if (asyncCallbacks.TryGetValue(fnResult.callId, out var entry)) {
                            var result = ProcessAfterDeserialization(fnResult.result, entry.type);
                            if (fnResult.success) {
                                entry.complete.SetResult(result);
                            } else {
                                entry.complete.SetException(new ArgumentException(result?.ToString()));
                            }
                            asyncCallbacks.Remove(fnResult.callId);
                        }
                    }
                    break;
                }
                default: 
                    throw new ArgumentException("Invalid message received");
            }
        }

        private T GetHostObject<T>(string objId, IDictionary<string, T> registry) {
            if (!registry.TryGetValue(objId, out var entry)) {
                throw new ArgumentException($"No object found with ID '{objId}'.");
            }
            return entry;
        }

        protected void CallTargetFunction(RPC_AnyCallTypeFnCallMessage message, RPCChannel replyChannel) {
            object? result = null;
            bool success = true;
            try {
                switch (message) {
                    case RPC_PropGetMessage propGet: {
                        var entry = GetHostObject(message.objId, hostObjectRegistry);
                        result = (entry.target as Type ?? entry.target.GetType()).GetProperty(propGet.prop)?.GetValue(entry.target);
                        break;
                    }
                    case RPC_PropSetMessage propSet: {
                        var entry = GetHostObject(message.objId, hostObjectRegistry);
                        var propInfo = (entry.target as Type ?? entry.target.GetType()).GetProperty(propSet.prop);
                        if (propInfo is null) {
                            throw new ArgumentException($"Could not find property '{propSet.prop}' on object '{propSet.objId}'.");
                        }
                        var value = ProcessAfterDeserialization(propSet.args[0], propInfo.PropertyType);
                        propInfo.SetValue(entry.target, value);
                        break;
                    }
                    case RPC_RpcCallMessage methodCall: {
                        var entry = GetHostObject(message.objId, hostObjectRegistry);
                        var method = (entry.target as Type ?? entry.target.GetType()).GetMethod(methodCall.prop);
                        // var d = method.CreateDelegate<Delegate>();
                        if (method is null) {
                            throw new ArgumentException($"Method '{methodCall.prop}' not found on object '{methodCall.objId}'.");
                        }
                        var args = ProcessArgumentsAfterDeserialization(methodCall.args, method.GetParameters().Select(param => param.ParameterType).ToArray());
                        result = method.Invoke(entry.target, args);
                        break;
                    }
                    case RPC_FnCallMessage fnCall: {
                        var entry = GetHostObject(message.objId, hostFunctionRegistry);
                        var method = entry.target.Method;
                        var args = ProcessArgumentsAfterDeserialization(fnCall.args, method.GetParameters().Select(param => param.ParameterType).ToArray());
                        result = entry.target.DynamicInvoke(args);
                        break;
                    }
                    case RPC_CtorCallMessage ctorCall: {
                        var classId = message.objId;
                        if (!hostClassRegistry.TryGetValue(classId, out var entry) || entry is null) {
                            throw new ArgumentException($"No class found with ID '{classId}'.");
                        }
                        var method = entry.classType.GetConstructors()[0];
                        var args = ProcessArgumentsAfterDeserialization(ctorCall.args, method.GetParameters().Select(param => param.ParameterType).ToArray());
                        result = method.Invoke(args);
                        break;
                    }

                    default:
                        throw new ArgumentException($"Invalid message received, action={message.action}");
                }
            } catch (Exception e) {
                success = false;
                result = e.ToString();
            }

            if (message.callType == FunctionReturnBehavior.Async) {
                SendAsyncIfPossible(new RPC_AsyncFnResultMessage {
                    success = success,
                    result = result,
                    callId = message.callId
                }, replyChannel);
            } else if (message.callType == FunctionReturnBehavior.Sync) {
                SendSyncIfPossible(new RPC_SyncFnResultMessage {
                    success = success,
                    result = result,
                }, replyChannel);
            }
        }

        /**
        * Send a request to get the descriptors for the registered host objects from the other side.
        * Uses synchronous communication if possible and returns `true`/`false` based on if the descriptors were received.
        * If sync is not available, it uses async messaging and returns a Task.
        */
        public ValueTask<bool> RequestRemoteDescriptors() {
            if (Channel is RPCChannelSendSync syncChannel) {
                var response = syncChannel.SendSync(new RPC_GetDescriptorsMessage());
                if (response is RPC_DescriptorsResultMessage descriptors) {
                    SetRemoteDescriptors(descriptors);
                    return new ValueTask<bool>(true);
                }
            }

            if (Channel is RPCChannelSendAsync asyncChannel) {
                remoteDescriptorsReceived = new TaskCompletionSource<bool>();
                asyncChannel.SendAsync(new RPC_GetDescriptorsMessage());
                return new ValueTask<bool>(remoteDescriptorsReceived.Task);
            }

            return new ValueTask<bool>(false);
        }

        private void SetRemoteDescriptors(RPC_DescriptorsResultMessage response) {
            if (response.objects is not null) {
                this.remoteObjectDescriptors = response.objects;
            }
            if (response.functions is not null) {
                this.remoteFunctionDescriptors = response.functions;
            }
            if (response.classes is not null) {
                this.remoteClassDescriptors = response.classes;
            }
        }

        /**
        * Send the descriptors for the registered host objects to the other side.
        * If possible, the message is sent synchronously.
        * This is a "push" style message, for "pull" see [[requestRemoteDescriptors]].
        */
        public void SendRemoteDescriptors(RPCChannel? replyChannel) {
            replyChannel ??= Channel;
            SendSyncIfPossible(new RPC_DescriptorsResultMessage {
                objects = GetLocalObjectDescriptors(),
                functions = hostFunctionRegistry.ToDictionary(x => x.Key, x => x.Value.descriptor),
                classes = hostClassRegistry.ToDictionary(x => x.Key, x => x.Value.descriptor),
            }, replyChannel);
        }

        private ObjectDescriptors GetLocalObjectDescriptors() {
            var descriptors = new ObjectDescriptors();

            foreach (var (key, entry) in hostObjectRegistry) {
                if (entry.descriptor is ObjectDescriptor objectDescriptor) {
                    var props = new Dictionary<string, object>();
                    if (objectDescriptor.ReadonlyProperties is not null) {
                        foreach (var prop in objectDescriptor.ReadonlyProperties) {
                            var value = entry.target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry.target);
                            if (value is not null) props.Add(prop, value);
                        }
                    }
                    descriptors.Add(key, new ObjectDescriptorWithProps(objectDescriptor, props));
                }
            }

            return descriptors;
        }

        private object? SendSyncIfPossible(RPC_Message message, RPCChannel? channel = null) {
            channel ??= Channel;

            if (channel is RPCChannelSendSync syncChannel) {
                return syncChannel.SendSync(message);
            } else if (channel is RPCChannelSendAsync asyncChannel) {
                asyncChannel.SendAsync(message);
            }
            return null;
        }

        
        private object? SendAsyncIfPossible(RPC_Message message, RPCChannel? channel = null) {
            channel ??= Channel;

            if (channel is RPCChannelSendAsync asyncChannel) {
                asyncChannel.SendAsync(message);
            } else if (channel is RPCChannelSendSync syncChannel) {
                return syncChannel.SendSync(message);
            }
            
            return null;
        }

        private object? ProcessAfterDeserialization(object? obj, Type? type = null) {
            if (type is null) return obj;

            if (obj is null) {
                if (!type.IsByRef) {
                    throw new ArgumentException($"null cannot be passed as a value type");
                }
            } else {
                var argType = obj.GetType();
                if (!argType.IsAssignableTo(type)) {
                    obj = Convert.ChangeType(obj, type);
                }
            }
            // TODO: recursive call for Dictionary?
            return obj;
        }

        private object?[] ProcessArgumentsAfterDeserialization(object?[] args, Type[] parameterTypes) {
            if (args.Length != parameterTypes.Length) {
                throw new ArgumentException($"Method argument number mismatch. Expected {parameterTypes.Length} and got {args.Length}.");
            }

            for (var i = 0; i < args.Length; i++) {
                var arg = args[i];
                var type = parameterTypes[i];
                args[i] = ProcessAfterDeserialization(args[i], parameterTypes[i]);
            }

            return args;
        }

        private object?[] ProcessArgumentsBeforeSerialization(object?[] args/* , Type[] parameterTypes */, FunctionDescriptor func, RPCChannel replyChannel) {
            return args;
        }

        private Delegate CreateVoidProxyFunction<TReturn>(string? objId, FunctionDescriptor func, string action, RPCChannel replyChannel) {

            TReturn? ProxyFunction(object?[] args) {
                SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                    action = action,
                    callType = FunctionReturnBehavior.Void,
                    objId = objId, // ?? this[proxyObjectId]
                    prop = func.Name,
                    args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
                }, replyChannel);
                return default;
            }

            return ProxyFunction;
        }

        private Delegate CreateSyncProxyFunction<TReturn>(string? objId, FunctionDescriptor func, string action, RPCChannel replyChannel) {
            
            TReturn? ProxyFunction(object?[] args) {
                var response = (RPC_SyncFnResultMessage?)SendSyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                    action = action,
                    callType = FunctionReturnBehavior.Sync,
                    objId = objId, // ?? this[proxyObjectId]
                    prop = func.Name,
                    args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
                }, replyChannel);
                if (response is null) {
                    throw new ArgumentException($"No response received");
                }
                if (!response.success) {
                    throw new ArgumentException(response.result?.ToString());
                }
                return (TReturn?)ProcessAfterDeserialization(response.result /* replyChannel */);
            }

            return ProxyFunction;
        }

        private Delegate CreateAsyncProxyFunction<TReturn>(string objId, FunctionDescriptor func, string action, RPCChannel replyChannel) {
            
            Task<TReturn?> ProxyFunction(object?[] args) {
                callId++;

                SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                    action = action,
                    callType = FunctionReturnBehavior.Async,
                    callId = callId.ToString(),
                    objId = objId, // ?? this[proxyObjectId]
                    prop = func.Name,
                    args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
                }, replyChannel);
                
                var completionSource = new TaskCompletionSource<object?>();
                asyncCallbacks.Add(callId.ToString(), new AsyncCallbackEntry(completionSource, typeof(TReturn)));

                return completionSource.Task.ContinueWith(t => (TReturn?)t.Result);
            }

            return ProxyFunction;
        }

        private Delegate CreateProxyFunction<TReturn>(
            string objId,
            FunctionDescriptor descriptor,
            string action,
            FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async,
            RPCChannel? replyChannel = null)
        {
            replyChannel ??= Channel;
            var callType = descriptor?.Returns ?? defaultCallType;

            if (callType == FunctionReturnBehavior.Async && replyChannel is not RPCChannelSendAsync) callType = FunctionReturnBehavior.Sync;
            if (callType == FunctionReturnBehavior.Sync && replyChannel is not RPCChannelSendSync) callType = FunctionReturnBehavior.Async;

            return callType switch {
                FunctionReturnBehavior.Void => CreateVoidProxyFunction<TReturn>(objId, descriptor, action, replyChannel),
                FunctionReturnBehavior.Sync => CreateSyncProxyFunction<TReturn>(objId, descriptor, action, replyChannel),
                _ => CreateAsyncProxyFunction<TReturn>(objId, descriptor, action, replyChannel)
            };
        }

        private Delegate CreateProxyFunctionWithType(
            Type returnType,
            string objId,
            FunctionDescriptor descriptor,
            string action,
            FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async,
            RPCChannel? replyChannel = null)
        {
            return (Delegate)GetType()
                .GetMethod("CreateProxyFunction", BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(returnType)
                .Invoke(this, new object[] { objId, descriptor, action, defaultCallType, replyChannel });
        }

        private Delegate CreateDynamicWrapperMethod(string methodName, Delegate proxyFuncion, Type[] paramTypes) {
            var dmethod = new DynamicMethod(methodName,
                proxyFuncion.Method.ReturnType,
                paramTypes.Prepend(proxyFuncion.Target.GetType()).ToArray(),
                proxyFuncion.Target.GetType(), true);

            var il = dmethod.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);       // "this"

            il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(object));    //arr = new object[paramTypes.Length]

            for (var i = 0; i < paramTypes.Length; i++) {
                il.Emit(OpCodes.Dup);               // arr ref
                il.Emit(OpCodes.Ldc_I4, i);         // int32: idx
                il.Emit(OpCodes.Ldarg, i + 1);      // arg(i+1)
                if (paramTypes[i].IsValueType) {
                    il.Emit(OpCodes.Box, paramTypes[i]);
                }
                il.Emit(OpCodes.Stelem_Ref);        // arr[idx] = arg
            }

            il.Emit(OpCodes.Call, proxyFuncion.Method);
            il.Emit(OpCodes.Ret);

            var delegateTypes = Expression.GetDelegateType(paramTypes.Append(proxyFuncion.Method.ReturnType).ToArray());

            return dmethod.CreateDelegate(delegateTypes, proxyFuncion.Target);
        }

        private static Type UnwrapTaskReturnType(Type type) {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
                type = type.GetGenericArguments()[0];
            }
            return type;
        }

        public T GetProxyFunction<T> (string objId) where T: Delegate {
            // get it from a registry

            if (!remoteFunctionDescriptors.TryGetValue(objId, out var descriptor)) {
                throw new ArgumentException($"No object descriptor found with ID '{objId}'.");
            }

            var method = typeof(T).GetMethod("Invoke");
            var returnType = UnwrapTaskReturnType(method.ReturnType);
            var funcParamTypes = method.GetParameters().Select(pi => pi.ParameterType).ToArray();
            
            var proxyFunc = CreateProxyFunctionWithType(returnType, objId, descriptor, "fn_call");
            
            // put it in registry
            return (T)CreateDynamicWrapperMethod(objId + "_" + descriptor.Name, proxyFunc, funcParamTypes);
        }

        public T GetProxyObject<T>(string objId) {
            if (!remoteObjectDescriptors.TryGetValue(objId, out var descriptor)) {
                throw new ArgumentException($"No descriptor found for object ID {objId}.");
            }
            return (T)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(T), new ProxyObjectInterceptor(this, objId, descriptor, "method_call"));
        }

        public record ProxyObjectInterceptor(SuperRPC rpc, string objId, ObjectDescriptor descriptor, string action) : IInterceptor
        {
            private readonly Dictionary<string, Delegate> proxyFunctions = new Dictionary<string, Delegate>();

            public void Intercept(IInvocation invocation) {
                var methodName = invocation.Method.Name;

                if (!proxyFunctions.TryGetValue(methodName, out var proxyDelegate)) {
                    var funcDescriptor = descriptor.Functions?.FirstOrDefault(descr => descr.Name == methodName);
                    if (funcDescriptor is null) {
                        throw new ArgumentException($"No function descriptor found for '{methodName}'; objID={objId}");
                    }
                    proxyDelegate = rpc.CreateProxyFunctionWithType(UnwrapTaskReturnType(invocation.Method.ReturnType), objId, funcDescriptor, action);
                    proxyFunctions.Add(methodName, proxyDelegate);
                }

                invocation.ReturnValue = proxyDelegate.DynamicInvoke(new [] { invocation.Arguments });
            }
        }
    }

}