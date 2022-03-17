using System.Threading;
using System.Security;
using System.Diagnostics;
using System;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Collections.Generic;

using ObjectDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ObjectDescriptor>;
using FunctionDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.FunctionDescriptor>;
using ClassDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ClassDescriptor>;

using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Castle.DynamicProxy;

namespace SuperRPC;

record AsyncCallbackEntry(TaskCompletionSource<object?> complete, Type? type);

public class SuperRPC
{
    private AsyncLocal<object?> _currentContextAsyncLocal = new AsyncLocal<object?>();
    public object? CurrentContext {
        get => _currentContextAsyncLocal.Value;
        set => _currentContextAsyncLocal.Value = value;
    }

    protected readonly Func<string> ObjectIDGenerator;
    protected IRPCChannel? Channel;

    private ObjectDescriptors? remoteObjectDescriptors;
    private FunctionDescriptors? remoteFunctionDescriptors;
    private ClassDescriptors? remoteClassDescriptors;
    private TaskCompletionSource<bool>? remoteDescriptorsReceived = null;

    // private readonly proxyObjectRegistry = new ProxyObjectRegistry();
    private readonly ObjectIdDictionary<string, Type, Func<string, object>?> proxyClassRegistry = new ObjectIdDictionary<string, Type, Func<string, object>?>();
    
    private readonly ObjectIdDictionary<string, object, ObjectDescriptor> hostObjectRegistry = new ObjectIdDictionary<string, object, ObjectDescriptor>();
    private readonly ObjectIdDictionary<string, Delegate, FunctionDescriptor> hostFunctionRegistry = new ObjectIdDictionary<string, Delegate, FunctionDescriptor>();
    private readonly ObjectIdDictionary<string, Type, ClassDescriptor> hostClassRegistry = new ObjectIdDictionary<string, Type, ClassDescriptor>();

    private readonly ProxyGenerator proxyGenerator = new ProxyGenerator();

    private int callId = 0;
    private readonly Dictionary<string, AsyncCallbackEntry> asyncCallbacks = new Dictionary<string, AsyncCallbackEntry>();

    public SuperRPC(Func<string> objectIdGenerator) {
        ObjectIDGenerator = objectIdGenerator;
    }

    public void Connect(IRPCChannel channel) {
        Channel = channel;
        if (channel is IRPCReceiveChannel receiveChannel) {
            receiveChannel.MessageReceived += MessageReceived;
        }
    }

    public void RegisterHostObject(string objId, object target, ObjectDescriptor descriptor) {
        hostObjectRegistry.Add(objId, target, descriptor);
    }

    public void RegisterHostFunction(string objId, Delegate target, FunctionDescriptor? descriptor = null) {
        hostFunctionRegistry.Add(objId, target, descriptor ?? new FunctionDescriptor());
    }

    public void RegisterHostClass(string classId, Type clazz, ClassDescriptor descriptor) {
        descriptor.ClassId = classId;
        if (descriptor.Static is not null) {
            RegisterHostObject(classId, clazz, descriptor.Static);
        }

        hostClassRegistry.Add(classId, clazz, descriptor);
    }

    public void RegisterHostClass<TClass>(string classId, ClassDescriptor descriptor) {
        RegisterHostClass(classId, typeof(TClass), descriptor);
    }

    public void RegisterProxyClass<TInterface>(string classId) {
        proxyClassRegistry.Add(classId, typeof(TInterface), null);
    }

    private TaskCompletionSource replySent;

    protected void MessageReceived(object? sender, MessageReceivedEventArgs eventArgs) {
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
                hostObjectRegistry.RemoveById(objectDied.objId);
                break;
            case RPC_FnResultMessageBase fnResult: {
                if (fnResult.callType == FunctionReturnBehavior.Async) {
                    if (asyncCallbacks.TryGetValue(fnResult.callId, out var entry)) {
                        if (fnResult.success) {
                            var result = ProcessAfterDeserialization(fnResult.result, replyChannel, entry.type);
                            entry.complete.SetResult(result);
                        } else {
                            entry.complete.SetException(new ArgumentException(fnResult.result?.ToString()));
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

    protected void CallTargetFunction(RPC_AnyCallTypeFnCallMessage message, IRPCChannel replyChannel) {
        replySent = new TaskCompletionSource();

        object? result = null;
        bool success = true;

        try {
            switch (message) {
                case RPC_PropGetMessage propGet: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    result = (entry.obj as Type ?? entry.obj.GetType()).GetProperty(propGet.prop)?.GetValue(entry.obj);
                    break;
                }
                case RPC_PropSetMessage propSet: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    var propInfo = (entry.obj as Type ?? entry.obj.GetType()).GetProperty(propSet.prop);
                    if (propInfo is null) {
                        throw new ArgumentException($"Could not find property '{propSet.prop}' on object '{propSet.objId}'.");
                    }
                    var value = ProcessAfterDeserialization(propSet.args[0], replyChannel, propInfo.PropertyType);
                    propInfo.SetValue(entry.obj, value);
                    break;
                }
                case RPC_RpcCallMessage methodCall: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    var method = (entry.obj as Type ?? entry.obj.GetType()).GetMethod(methodCall.prop);
                    // var d = method.CreateDelegate<Delegate>();
                    if (method is null) {
                        throw new ArgumentException($"Method '{methodCall.prop}' not found on object '{methodCall.objId}'.");
                    }
                    var args = ProcessArgumentsAfterDeserialization(methodCall.args, replyChannel, method.GetParameters().Select(param => param.ParameterType).ToArray());
                    result = method.Invoke(entry.obj, args);
                    break;
                }
                case RPC_FnCallMessage fnCall: {
                    var entry = GetHostObject(message.objId, hostFunctionRegistry.ById);
                    var method = entry.obj.Method;
                    var args = ProcessArgumentsAfterDeserialization(fnCall.args, replyChannel, method.GetParameters().Select(param => param.ParameterType).ToArray());
                    result = entry.obj.DynamicInvoke(args);
                    break;
                }
                case RPC_CtorCallMessage ctorCall: {
                    var classId = message.objId;
                    if (!hostClassRegistry.ById.TryGetValue(classId, out var entry)) {
                        throw new ArgumentException($"No class found with ID '{classId}'.");
                    }
                    var method = entry.obj.GetConstructors()[0];
                    var args = ProcessArgumentsAfterDeserialization(ctorCall.args, replyChannel, method.GetParameters().Select(param => param.ParameterType).ToArray());
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
                result = ProcessBeforeSerialization(result, replyChannel),
                callId = message.callId
            }, replyChannel);
        } else if (message.callType == FunctionReturnBehavior.Sync) {
            SendSyncIfPossible(new RPC_SyncFnResultMessage {
                success = success,
                result = ProcessBeforeSerialization(result, replyChannel),
            }, replyChannel);
        }

        replySent.SetResult();
    }

    /**
    * Send a request to get the descriptors for the registered host objects from the other side.
    * Uses synchronous communication if possible and returns `true`/`false` based on if the descriptors were received.
    * If sync is not available, it uses async messaging and returns a Task.
    */
    public ValueTask<bool> RequestRemoteDescriptors() {
        if (Channel is IRPCSendSyncChannel syncChannel) {
            var response = syncChannel.SendSync(new RPC_GetDescriptorsMessage());
            if (response is RPC_DescriptorsResultMessage descriptors) {
                SetRemoteDescriptors(descriptors);
                return new ValueTask<bool>(true);
            }
        }

        if (Channel is IRPCSendAsyncChannel asyncChannel) {
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
    public void SendRemoteDescriptors(IRPCChannel? replyChannel) {
        replyChannel ??= Channel;
        SendSyncIfPossible(new RPC_DescriptorsResultMessage {
            objects = GetLocalObjectDescriptors(),
            functions = hostFunctionRegistry.ById.ToDictionary(x => x.Key, x => x.Value.value),
            classes = hostClassRegistry.ById.ToDictionary(x => x.Key, x => x.Value.value),
        }, replyChannel);
    }

    private ObjectDescriptors GetLocalObjectDescriptors() {
        var descriptors = new ObjectDescriptors();

        foreach (var (key, entry) in hostObjectRegistry.ById) {
            if (entry.value is ObjectDescriptor objectDescriptor) {
                var props = new Dictionary<string, object>();
                if (objectDescriptor.ReadonlyProperties is not null) {
                    foreach (var prop in objectDescriptor.ReadonlyProperties) {
                        var value = entry.obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry.obj);
                        if (value is not null) props.Add(prop, value);
                    }
                }
                descriptors.Add(key, new ObjectDescriptorWithProps(objectDescriptor, props));
            }
        }

        return descriptors;
    }

    private object? SendSyncIfPossible(RPC_Message message, IRPCChannel? channel = null) {
        channel ??= Channel;

        if (channel is IRPCSendSyncChannel syncChannel) {
            return syncChannel.SendSync(message);
        } else if (channel is IRPCSendAsyncChannel asyncChannel) {
            asyncChannel.SendAsync(message);
        }
        return null;
    }

    
    private object? SendAsyncIfPossible(RPC_Message message, IRPCChannel? channel = null) {
        channel ??= Channel;

        if (channel is IRPCSendAsyncChannel asyncChannel) {
            asyncChannel.SendAsync(message);
        } else if (channel is IRPCSendSyncChannel syncChannel) {
            return syncChannel.SendSync(message);
        }
        return null;
    }

    private string RegisterLocalObj(object obj, ObjectDescriptor? descriptor = null) {
        descriptor ??= new ObjectDescriptor();

        if (hostObjectRegistry.ByObj.TryGetValue(obj, out var entry)) {
            return entry.id;
        }
        var objId = ObjectIDGenerator();
        hostObjectRegistry.Add(objId, obj, descriptor);
        return objId;
    }

    private string RegisterLocalFunc(Delegate obj, FunctionDescriptor descriptor) {
        if (hostFunctionRegistry.ByObj.TryGetValue(obj, out var entry)) {
            return entry.id;
        }
        var objId = ObjectIDGenerator();
        hostFunctionRegistry.Add(objId, obj, descriptor);
        return objId;
    }

    private Dictionary<Type, Func<object, Type, object>> deserializers = new Dictionary<Type, Func<object, Type, object>>();

    public void RegisterDeserializer(Type type, Func<object, Type, object> deserializer) {
        deserializers.Add(type, deserializer);
    }

    private object? ProcessAfterDeserialization(object? obj, IRPCChannel replyChannel, Type? type = null) {
        if (obj is null) {
            if (type?.IsValueType == true) {
                throw new ArgumentException("null cannot be passed as a value type");
            }
        } else {
            if (type is not null) {
                var objType = obj.GetType();

                // special cases for _rpc_type=object/function
                if (proxyClassRegistry.ByObj.TryGetValue(type, out var proxyClassEntry)) {
                    var factory = proxyClassEntry.value;
                    if (factory is null) {
                        factory = GetProxyClassFactory(proxyClassEntry.id, replyChannel);
                        proxyClassRegistry.Add(proxyClassEntry.id, proxyClassEntry.obj, factory);
                    }
                    string objId = ((dynamic)obj)["objId"].ToString();
                    obj = factory(objId);

                    objType = obj.GetType();
                }

                // custom deserializers
                if (deserializers.TryGetValue(type, out var deserializer)) {
                    obj = deserializer(obj, type);
                } else if (deserializers.TryGetValue(typeof(object), out deserializer)) {
                    obj = deserializer(obj, type);
                }

                if (!objType.IsAssignableTo(type)) {
                    obj = Convert.ChangeType(obj, type);
                }
            }
            
            // recursive call for Dictionary
            if (obj is IDictionary<string, object?> dict) {
                foreach (var (key, value) in dict) {
                    dict[key] = ProcessAfterDeserialization(value, replyChannel);
                }
            }
        }
        return obj;
    }


    private object?[] ProcessArgumentsAfterDeserialization(object?[] args, IRPCChannel replyChannel, Type[] parameterTypes) {
        if (args.Length != parameterTypes.Length) {
            throw new ArgumentException($"Method argument number mismatch. Expected {parameterTypes.Length} and got {args.Length}.");
        }

        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            var type = parameterTypes[i];
            args[i] = ProcessAfterDeserialization(args[i], replyChannel, parameterTypes[i]);
        }

        return args;
    }

    private bool ProcessPropertyValuesBeforeSerialization(object obj, PropertyInfo[] properties, Dictionary<string, object?> propertyBag, IRPCChannel replyChannel) {
        var needToConvert = false;
        foreach (var propInfo in properties) {
            if (!propInfo.CanRead || propInfo.GetIndexParameters().Length > 0) continue;

            var value = propInfo.GetValue(obj);
            var newValue = ProcessBeforeSerialization(value, replyChannel);
            propertyBag.Add(propInfo.Name, newValue);

            if (value is null ? newValue is not null : !value.Equals(newValue)) {
                needToConvert = true;
            }
        }
        return needToConvert;
    }

    private object? ProcessBeforeSerialization(object? obj, IRPCChannel replyChannel) {
        if (obj is null) return obj;

        var objType = obj.GetType();
        const BindingFlags PropBindFlags = BindingFlags.Public | BindingFlags.Instance;

        if (obj is Task task) {
            string? objId = null;
            if (!hostObjectRegistry.ByObj.ContainsKey(obj)) {

                void SendResult(bool success, object? result) {
                    SendAsyncIfPossible(new RPC_AsyncFnResultMessage {
                        success = success,
                        result = result,
                        callId = objId
                    }, replyChannel);
                }

                if (objType.IsGenericType) {
                    replySent.Task.ContinueWith(_ => {
                        task.ContinueWith(t => SendResult(!t.IsFaulted, 
                            t.IsFaulted ? t.Exception?.ToString() :
                            ProcessBeforeSerialization(((dynamic)t).Result, replyChannel)
                        ));
                    });
                } else {
                    replySent.Task.ContinueWith(_ => {
                        task.ContinueWith(t => SendResult(!t.IsFaulted, null));
                    });
                }
            }
            objId = RegisterLocalObj(obj);
            return new RPC_Object(objId, null, "Promise");
        }

        if (hostClassRegistry.ByObj.TryGetValue(objType, out var entry)) {
            var descriptor = entry.value;
            var objId = RegisterLocalObj(obj, descriptor.Instance);
            var propertyBag = new Dictionary<string, object?>();

            if (descriptor.Instance?.ReadonlyProperties is not null) {
                var propertyInfos = descriptor.Instance.ReadonlyProperties.Select(prop => objType.GetProperty(prop, PropBindFlags)).ToArray();
                ProcessPropertyValuesBeforeSerialization(obj, propertyInfos, propertyBag, replyChannel);
            }
            return new RPC_Object(objId, propertyBag, entry.id);
        }

        if (obj is Delegate func) {
            var objId = RegisterLocalFunc(func, new FunctionDescriptor());
            return new RPC_Function(objId);
        }

        if (objType.IsClass && objType != typeof(string)) {
            var propertyInfos = objType.GetProperties(PropBindFlags);
            var propertyBag = new Dictionary<string, object?>();

            if (ProcessPropertyValuesBeforeSerialization(obj, propertyInfos, propertyBag, replyChannel)) {
                var objId = RegisterLocalObj(obj);
                return new RPC_Object(objId, propertyBag);
            }
        }

        return obj;
    }

    private object?[] ProcessArgumentsBeforeSerialization(object?[] args/* , Type[] parameterTypes */, FunctionDescriptor func, IRPCChannel replyChannel) {
        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            // var type = parameterTypes[i];
            args[i] = ProcessBeforeSerialization(arg, replyChannel);
        }
        return args;
    }

    private Delegate CreateVoidProxyFunction<TReturn>(string? objId, FunctionDescriptor func, string action, IRPCChannel replyChannel) {

        TReturn? ProxyFunction(string instanceObjId, object?[] args) {
            SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Void,
                objId = objId ?? instanceObjId,
                prop = func.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            return default;
        }

        return ProxyFunction;
    }

    private Delegate CreateSyncProxyFunction<TReturn>(string? objId, FunctionDescriptor func, string action, IRPCChannel replyChannel) {
        
        TReturn? ProxyFunction(string instanceObjId, object?[] args) {
            var response = (RPC_SyncFnResultMessage?)SendSyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Sync,
                objId = objId ?? instanceObjId,
                prop = func.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            if (response is null) {
                throw new ArgumentException($"No response received");
            }
            if (!response.success) {
                throw new ArgumentException(response.result?.ToString());
            }
            return (TReturn?)ProcessAfterDeserialization(response.result, replyChannel);
        }

        return ProxyFunction;
    }

    private Delegate CreateAsyncProxyFunction<TReturn>(string? objId, FunctionDescriptor func, string action, IRPCChannel replyChannel) {
        
        Task<TReturn?> ProxyFunction(string instanceObjId, object?[] args) {
            callId++;

            SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Async,
                callId = callId.ToString(),
                objId = objId ?? instanceObjId,
                prop = func.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            
            var completionSource = new TaskCompletionSource<object?>();
            asyncCallbacks.Add(callId.ToString(), new AsyncCallbackEntry(completionSource, UnwrapTaskReturnType(typeof(TReturn))));

            return completionSource.Task.ContinueWith(t => (TReturn?)t.Result);
        }

        return ProxyFunction;
    }

    private Delegate CreateProxyFunction<TReturn>(
        string objId,
        FunctionDescriptor descriptor,
        string action,
        FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async,
        IRPCChannel? replyChannel = null)
    {
        replyChannel ??= Channel;
        var callType = descriptor?.Returns ?? defaultCallType;

        if (callType == FunctionReturnBehavior.Async && replyChannel is not IRPCSendAsyncChannel) callType = FunctionReturnBehavior.Sync;
        if (callType == FunctionReturnBehavior.Sync && replyChannel is not IRPCSendSyncChannel) callType = FunctionReturnBehavior.Async;

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
        IRPCChannel? replyChannel = null,
        FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async)
    {
        if (returnType == typeof(void)) {
            returnType = typeof(object);
        }
        return (Delegate)GetType()
            .GetMethod("CreateProxyFunction", BindingFlags.NonPublic | BindingFlags.Instance)
            .MakeGenericMethod(returnType)
            .Invoke(this, new object[] { objId, descriptor, action, defaultCallType, replyChannel });
    }

    private Delegate CreateDynamicWrapperMethod(string methodName, Delegate proxyFunction, Type[] paramTypes) {
        var dmethod = new DynamicMethod(methodName,
            proxyFunction.Method.ReturnType,
            paramTypes.Prepend(proxyFunction.Target.GetType()).ToArray(),
            proxyFunction.Target.GetType(), true);

        var il = dmethod.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);       // "this" (ref of the instance of the class generated for proxyFunction)

        il.Emit(OpCodes.Ldnull);        // "null" to the stack -> instanceObjId

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

        il.Emit(OpCodes.Call, proxyFunction.Method);
        il.Emit(OpCodes.Ret);

        var delegateTypes = Expression.GetDelegateType(paramTypes.Append(proxyFunction.Method.ReturnType).ToArray());

        return dmethod.CreateDelegate(delegateTypes, proxyFunction.Target);
    }

    private static Type UnwrapTaskReturnType(Type type) {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
            type = type.GetGenericArguments()[0];
        }
        return type;
    }

    public T GetProxyFunction<T> (string objId, IRPCChannel? channel = null) where T: Delegate {
        channel ??= Channel;
        // get it from a registry

        if (!remoteFunctionDescriptors.TryGetValue(objId, out var descriptor)) {
            throw new ArgumentException($"No object descriptor found with ID '{objId}'.");
        }

        var method = typeof(T).GetMethod("Invoke");
        var returnType = UnwrapTaskReturnType(method.ReturnType);
        var funcParamTypes = method.GetParameters().Select(pi => pi.ParameterType).ToArray();
        
        var proxyFunc = CreateProxyFunctionWithType(returnType, objId, descriptor, "fn_call", channel);
        
        // put it in registry
        return (T)CreateDynamicWrapperMethod(objId + "_" + descriptor.Name, proxyFunc, funcParamTypes);
    }

    public T GetProxyObject<T>(string objId, IRPCChannel? channel = null) {
        if (!remoteObjectDescriptors.TryGetValue(objId, out var descriptor)) {
            throw new ArgumentException($"No descriptor found for object ID {objId}.");
        }
        return (T)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(T), 
            new ProxyObjectInterceptor(this, objId, descriptor, "method_call", channel ?? Channel)
        );
    }

    // public T GetProxyObject2<T>(string objId, IRPCChannel? channel = null) {
    //     if (!remoteObjectDescriptors.TryGetValue(objId, out var descriptor)) {
    //         throw new ArgumentException($"No descriptor found for object ID {objId}.");
    //     }
    //     // return (T)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(T), 
    //     //     new ProxyObjectInterceptor(this, objId, descriptor, "method_call", channel ?? Channel)
    //     // );
    //     CreateProxyClass
    // }

    public record ProxyObjectInterceptor(SuperRPC rpc, string objId, ObjectDescriptor descriptor, string action, IRPCChannel channel) : IInterceptor
    {
        private readonly Dictionary<string, Delegate> proxyFunctions = new Dictionary<string, Delegate>();

        public void Intercept(IInvocation invocation) {
            var methodName = invocation.Method.Name;

            if (!proxyFunctions.TryGetValue(methodName, out var proxyDelegate)) {
                var funcDescriptor = descriptor.Functions?.FirstOrDefault(descr => descr.Name == methodName);
                if (funcDescriptor is null) {
                    throw new ArgumentException($"No function descriptor found for '{methodName}'; objID={objId}");
                }
                proxyDelegate = rpc.CreateProxyFunctionWithType(
                    UnwrapTaskReturnType(invocation.Method.ReturnType),
                    objId, funcDescriptor, action, channel
                );
                proxyFunctions.Add(methodName, proxyDelegate);
            }

            invocation.ReturnValue = proxyDelegate.DynamicInvoke(new object[] { objId, invocation.Arguments });
        }
    }

    private ObjectIdDictionary<string, Type, Func<string, object>?>.Entry GetProxyClassEntry(string classId) {
        if (!proxyClassRegistry.ById.TryGetValue(classId, out var proxyClassEntry)) {
            throw new ArgumentException($"No proxy class interface registered with ID '{classId}'.");
        }
        return proxyClassEntry;
    }

    private Func<string, object> GetProxyClassFactory(string classId, IRPCChannel? channel = null) {
        var entry = GetProxyClassEntry(classId);
        if (entry.value is not null) {
            return entry.value;
        }
        
        var factory = CreateProxyClass(classId, channel);
        proxyClassRegistry.Add(classId, entry.obj, factory);
        return factory;
    }

    private Func<string, object> CreateProxyClass(string classId, IRPCChannel? channel = null) {
        channel ??= Channel;

        if (!remoteClassDescriptors.TryGetValue(classId, out var descriptor)) {
            throw new ArgumentException($"No class descriptor found with ID '{classId}'.");
        }

        var ifType = GetProxyClassEntry(classId).obj;

        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString()), AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
        var typeBuilder = moduleBuilder.DefineType(classId,
                TypeAttributes.Public |
                TypeAttributes.Class |
                TypeAttributes.AutoClass |
                TypeAttributes.AnsiClass |
                TypeAttributes.BeforeFieldInit |
                TypeAttributes.AutoLayout,
                null, new[] { ifType });

        var objIdField = typeBuilder.DefineField("objId", typeof(string), FieldAttributes.Public | FieldAttributes.InitOnly);
        var proxyFunctionsField = typeBuilder.DefineField("proxyFunctions", typeof(Delegate[]), FieldAttributes.Public | FieldAttributes.InitOnly);

        var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new [] { typeof(string), typeof(Delegate[]) });
        var ctorIL = constructorBuilder.GetILGenerator();

        // call base()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ConstructorInfo superConstructor = typeof(Object).GetConstructor(Type.EmptyTypes);
        ctorIL.Emit(OpCodes.Call, superConstructor);

        ctorIL.Emit(OpCodes.Ldarg_0);   // this
        ctorIL.Emit(OpCodes.Ldarg_1);   // objId ref
        ctorIL.Emit(OpCodes.Stfld, objIdField); // this.objId = arg1

        ctorIL.Emit(OpCodes.Ldarg_0);   // this
        ctorIL.Emit(OpCodes.Ldarg_2);   // proxyFunctions ref
        ctorIL.Emit(OpCodes.Stfld, proxyFunctionsField); // this.proxyFunctions = arg2
        
        ctorIL.Emit(OpCodes.Ret);

        var methods = ifType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var proxyFunctions = new Delegate[methods.Length];

        for (var midx = 0; midx < methods.Length; midx++) {
            var methodInfoToImpl = methods[midx];

            var funcDescriptor = descriptor.Instance?.Functions?.FirstOrDefault(desc => desc.Name == methodInfoToImpl.Name);    // TODO: camelCase <-> PascalCase ?
            if (funcDescriptor is null) {
                throw new ArgumentException($"No function descriptor found for method '{methodInfoToImpl.Name}' in class '{classId}'.");
            }

            var paramInfos = methodInfoToImpl.GetParameters();
            var paramTypes = paramInfos.Select(pi => pi.ParameterType).ToArray();

            var methodBuilder = typeBuilder.DefineMethod(methodInfoToImpl.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                CallingConventions.HasThis,
                methodInfoToImpl.ReturnType,
                methodInfoToImpl.ReturnParameter.GetRequiredCustomModifiers(),
                methodInfoToImpl.ReturnParameter.GetOptionalCustomModifiers(),
                paramTypes,
                paramInfos.Select(pi => pi.GetRequiredCustomModifiers()).ToArray(),
                paramInfos.Select(pi => pi.GetOptionalCustomModifiers()).ToArray()
            );

            var proxyFunction = CreateProxyFunctionWithType(UnwrapTaskReturnType(methodInfoToImpl.ReturnType), null, funcDescriptor, "method_call", channel);
            proxyFunctions[midx] = proxyFunction;

            var il = methodBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);                   // "this" (ref of this generated class)
            il.Emit(OpCodes.Ldfld, proxyFunctionsField);  // "this" (ref of object[] containing proxy function targets)
            il.Emit(OpCodes.Ldc_I4, midx);
            il.Emit(OpCodes.Ldelem_Ref);                // proxyFunction [Delegate] is on the stack now

            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, typeof(object));    //arr2 = new object[2]
            
            il.Emit(OpCodes.Dup);               // arr2
            il.Emit(OpCodes.Ldc_I4_0);          // 0
            il.Emit(OpCodes.Ldarg_0);           // "this" 
            il.Emit(OpCodes.Ldfld, objIdField); // push(this.objId)
            il.Emit(OpCodes.Stelem_Ref);        // arr2[0] = objId
            
            il.Emit(OpCodes.Dup);               // arr2
            il.Emit(OpCodes.Ldc_I4_1);          // 1
            
            il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(object));    //arr1 = new object[paramTypes.Length]

            for (var i = 0; i < paramTypes.Length; i++) {
                il.Emit(OpCodes.Dup);               // arr ref
                il.Emit(OpCodes.Ldc_I4, i);         // int32: idx
                il.Emit(OpCodes.Ldarg, i + 1);      // arg(i+1)
                if (paramTypes[i].IsValueType) {
                    il.Emit(OpCodes.Box, paramTypes[i]);
                }
                il.Emit(OpCodes.Stelem_Ref);        // arr1[idx] = arg
            }

            il.Emit(OpCodes.Stelem_Ref);    // arr2[1] = arr1

            il.Emit(OpCodes.Callvirt, typeof(Delegate).GetMethod("DynamicInvoke"));

            if (methodInfoToImpl.ReturnType == typeof(void)) {
                il.Emit(OpCodes.Pop);
            }

            il.Emit(OpCodes.Ret);
        }

        var type = typeBuilder.CreateType();
        object CreateInstance(string objId) {
            return Activator.CreateInstance(type, objId, proxyFunctions);
        }
        return CreateInstance;
    }
}

