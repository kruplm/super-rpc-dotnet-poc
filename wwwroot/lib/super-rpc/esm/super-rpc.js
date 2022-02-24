import { ProxyObjectRegistry, rpc_disposed } from './proxy-object-registry';
import { processObjectDescriptor, processFunctionDescriptor } from './rpc-descriptor-types';
import { getArgumentDescriptor, getFunctionDescriptor, getPropertyDescriptor, getPropName, isFunctionDescriptor } from './rpc-descriptor-types';
const hostObjectId = Symbol('hostObjectId');
const proxyObjectId = Symbol('proxyObjectId');
const classIdSym = Symbol('classId');
/**
 * The SuperRPC is the central piece. An instance must be created on both sides.
 *
 * Objects, functions or classes can be registered on the "host" side
 * (see [[registerHostObject]], [[registerHostClass]]) and then functions/properties can be
 * called from the "client" side (see [[getProxyObject]], [[getProxyClass]]).
 *
 * The RPC service is symmetric, so depending on the use-case (and the channel),
 * both side can be "host" and "client" at the same time.
 *
 * The constructor needs a function to generate unique IDs for objects.
 * In order to have no dependencies this needs to be passed in.
 * For convenience the examples use [nanoid](https://www.npmjs.com/package/nanoid).
 */
export class SuperRPC {
    objectIdGenerator;
    channel;
    remoteObjectDescriptors;
    remoteFunctionDescriptors;
    remoteClassDescriptors;
    remoteDescriptorsCallbacks;
    asyncCallbacks = new Map();
    callId = 0;
    proxyObjectRegistry = new ProxyObjectRegistry();
    proxyClassRegistry = new Map();
    hostObjectRegistry = new Map();
    hostFunctionRegistry = new Map();
    hostClassRegistry = new Map();
    /**
     * @param objectIdGenerator A function to generate a unique ID for an object.
     *
     * When sending an object to the other side that can not be serialized, we
     * generate an ID and send that instead. The other side creates a proxy object
     * that represents the remote object.
     */
    constructor(objectIdGenerator) {
        this.objectIdGenerator = objectIdGenerator;
    }
    /**
     * Stores the current "context" object that is passed to the callback of the [[RPCChannel.receive]] function.
     */
    currentContext;
    /**
     * Connect the service to a channel.
     */
    connect(channel) {
        this.channel = channel;
        channel.receive?.(this.messageReceived.bind(this));
    }
    /**
     * Register an object in the service to be called remotely.
     * @param objId An ID that the "client" side uses to identify this object.
     * @param target The target object
     * @param descriptor Describes which functions/properties to expose
     */
    registerHostObject(objId, target, descriptor) {
        descriptor.type = 'object';
        target[hostObjectId] = objId;
        this.hostObjectRegistry.set(objId, { target, descriptor });
    }
    /**
     * Register a function in the service to be called remotely.
     * @param objId An ID that the "client" side uses to identify this function.
     * @param target The target function
     * @param descriptor Describes arguments and return behavior ([[FunctionReturnBehavior]])
     */
    registerHostFunction(objId, target, descriptor) {
        descriptor.type = 'function';
        target[hostObjectId] = objId;
        this.hostFunctionRegistry.set(objId, { target, descriptor });
    }
    /**
     * Register a class in the service.
     *
     * When an instance of this class is passed to the other side, only the "readonlyProperties" are sent (see [[ClassDescriptor]]).
     * Functions and proxied properties are generated there and those call back to the original object.
     *
     * Even the constructor can be proxied.
     *
     * Note: static functions/properties act as if the class was a normal host object.
     *
     * @param classId An ID to identify the class on the client side.
     * @param classCtor The class itself (its constructor function)
     * @param descriptor What properties/functions to expose
     */
    registerHostClass(classId, classCtor, descriptor) {
        descriptor.type = 'class';
        descriptor.classId = classId;
        if (descriptor.static) {
            this.registerHostObject(classId, classCtor, descriptor.static);
        }
        if (descriptor.ctor) {
            this.registerHostFunction(classId, classCtor, descriptor.ctor);
        }
        classCtor[classIdSym] = classId;
        this.hostClassRegistry.set(classId, { target: classCtor, descriptor });
    }
    /**
     * Send a request to get the descriptors for the registered host objects from the other side.
     * Uses synchronous communication if possible and returns `true`/`false` based on if the descriptors were received.
     * If sync is not available, it uses async messaging and returns a Promise.
     */
    requestRemoteDescriptors() {
        if (this.channel?.sendSync) {
            const response = this.sendSync({ action: 'get_descriptors' });
            return this.setRemoteDescriptors(response);
        }
        return new Promise((resolve, reject) => {
            this.sendAsync({ action: 'get_descriptors' });
            this.remoteDescriptorsCallbacks = { resolve, reject };
        });
    }
    setRemoteDescriptors(response) {
        if (typeof response !== 'object')
            return false;
        if (response.classes) {
            this.remoteClassDescriptors = response.classes;
        }
        if (response.objects) {
            this.remoteObjectDescriptors = response.objects;
        }
        if (response.functions) {
            this.remoteFunctionDescriptors = response.functions;
        }
        return true;
    }
    /**
     * Send the descriptors for the registered host objects to the other side.
     * If possible, the message is sent synchronously.
     * This is a "push" style message, for "pull" see [[requestRemoteDescriptors]].
     */
    sendRemoteDescriptors(replyChannel = this.channel) {
        this.sendSyncIfPossible({
            action: 'descriptors',
            objects: this.getLocalDescriptors(this.hostObjectRegistry, processObjectDescriptor),
            functions: this.getLocalDescriptors(this.hostFunctionRegistry, processFunctionDescriptor),
            classes: this.getLocalDescriptors(this.hostClassRegistry),
        }, replyChannel);
    }
    getLocalDescriptors(registry, processFn) {
        const descriptors = {};
        for (const key of registry.keys()) {
            // .get() could return undefined, but we know it will never do that, since we iterate over existing keys
            // therefore it is safe to cast it to the entry types
            const entry = registry.get(key);
            if (!entry.descriptor)
                continue;
            let descr = { ...entry.descriptor };
            descr = processFn?.(descr, entry.target) ?? descr;
            descriptors[key] = descr;
            if (entry.descriptor.type === 'object' && entry.descriptor.readonlyProperties) {
                const props = {};
                for (const prop of entry.descriptor.readonlyProperties) {
                    props[prop] = entry.target[prop];
                }
                descr.props = props;
            }
        }
        return descriptors;
    }
    sendSync(message, channel = this.channel) {
        this.addMarker(message);
        return channel?.sendSync?.(message);
    }
    sendAsync(message, channel = this.channel) {
        this.addMarker(message);
        channel?.sendAsync?.(message);
    }
    sendSyncIfPossible(message, channel = this.channel) {
        return channel?.sendSync ? this.sendSync(message, channel) : this.sendAsync(message, channel);
    }
    sendAsyncIfPossible(message, channel = this.channel) {
        return channel?.sendAsync ? this.sendAsync(message, channel) : this.sendSync(message, channel);
    }
    addMarker(message) {
        message.rpc_marker = 'srpc';
    }
    checkMarker(message) {
        return typeof message === 'object' && message.rpc_marker === 'srpc';
    }
    callTargetFunction(msg, replyChannel = this.channel) {
        const entry = (msg.action === 'fn_call' || msg.action === 'ctor_call' ? this.hostFunctionRegistry : this.hostObjectRegistry).get(msg.objId);
        let result;
        let success = true;
        try {
            if (!entry)
                throw new Error(`No object found with ID '${msg.objId}'`);
            let scope = null;
            let { descriptor, target } = entry;
            switch (msg.action) {
                case 'prop_get': {
                    result = target[msg.prop];
                    break;
                }
                case 'prop_set': {
                    const descr = getPropertyDescriptor(descriptor, msg.prop);
                    target[msg.prop] = this.processAfterSerialization(msg.args[0], replyChannel, descr?.get?.arguments?.[0]);
                    break;
                }
                case 'method_call': {
                    scope = target;
                    descriptor = getFunctionDescriptor(descriptor, msg.prop);
                    target = target[msg.prop];
                    if (typeof target !== 'function')
                        throw new Error(`Property ${msg.prop} is not a function on object ${msg.objId}`);
                    // NO break here!
                }
                // eslint-disable-next-line no-fallthrough
                case 'fn_call': {
                    result = target.apply(scope, this.deserializeFunctionArgs(descriptor, msg.args, replyChannel));
                    break;
                }
                case 'ctor_call': {
                    result = new target(...this.deserializeFunctionArgs(descriptor, msg.args, replyChannel));
                    break;
                }
            }
            if (msg.callType === 'async') {
                Promise.resolve(result)
                    .then(value => result = this.processBeforeSerialization(value, replyChannel), err => { result = err?.toString?.(); success = false; })
                    .then(() => this.sendAsync({ action: 'fn_reply', callType: 'async', success, result, callId: msg.callId }, replyChannel));
            }
            else if (msg.callType === 'sync') {
                result = this.processBeforeSerialization(result, replyChannel);
            }
        }
        catch (err) {
            success = false;
            result = err?.toString?.();
        }
        if (msg.callType === 'sync') {
            this.sendSync({ action: 'fn_reply', callType: 'sync', success, result }, replyChannel);
        }
        else if (msg.callType === 'async' && !success) {
            this.sendAsync({ action: 'fn_reply', callType: 'async', success, result, callId: msg.callId }, replyChannel);
        }
    }
    messageReceived(message, replyChannel = this.channel, context) {
        this.currentContext = context;
        if (this.checkMarker(message)) {
            switch (message.action) {
                case 'get_descriptors': {
                    this.sendRemoteDescriptors(replyChannel);
                    break;
                }
                case 'descriptors': {
                    const success = this.setRemoteDescriptors(message);
                    this.remoteDescriptorsCallbacks?.[success ? 'resolve' : 'reject']();
                    this.remoteDescriptorsCallbacks = undefined;
                    break;
                }
                case 'prop_get':
                case 'prop_set':
                case 'ctor_call':
                case 'fn_call':
                case 'method_call': {
                    this.callTargetFunction(message, replyChannel);
                    break;
                }
                case 'obj_died': {
                    this.hostObjectRegistry.delete(message.objId);
                    break;
                }
                case 'fn_reply': {
                    if (message.callType === 'async') {
                        const result = this.processAfterSerialization(message.result, replyChannel);
                        const callbacks = this.asyncCallbacks.get(message.callId);
                        callbacks?.[message.success ? 'resolve' : 'reject'](result);
                        this.asyncCallbacks.delete(message.callId);
                    }
                    break;
                }
            }
        }
    }
    serializeFunctionArgs(func, args, replyChannel) {
        return args.map((arg, idx) => this.processBeforeSerialization(arg, replyChannel, getArgumentDescriptor(func, idx)));
    }
    deserializeFunctionArgs(func, args, replyChannel) {
        return args.map((arg, idx) => this.processAfterSerialization(arg, replyChannel, getArgumentDescriptor(func, idx)));
    }
    createVoidProxyFunction(objId, func, action, replyChannel) {
        // eslint-disable-next-line @typescript-eslint/no-this-alias
        const _this = this;
        const fn = function (...args) {
            if (fn[rpc_disposed])
                throw new Error('Remote function has been disposed');
            _this.sendAsyncIfPossible({
                action,
                callType: 'void',
                objId: objId ?? this[proxyObjectId],
                // eslint-disable-next-line @typescript-eslint/no-non-null-assertion
                prop: func.name,
                args: _this.serializeFunctionArgs(func, args, replyChannel)
            }, replyChannel);
        };
        return fn;
    }
    createSyncProxyFunction(objId, func, action, replyChannel) {
        // eslint-disable-next-line @typescript-eslint/no-this-alias
        const _this = this;
        const fn = function (...args) {
            if (fn[rpc_disposed])
                throw new Error('Remote function has been disposed');
            const response = _this.sendSync({
                action,
                callType: 'sync',
                objId: objId ?? this[proxyObjectId],
                // eslint-disable-next-line @typescript-eslint/no-non-null-assertion
                prop: func.name,
                args: _this.serializeFunctionArgs(func, args, replyChannel)
            }, replyChannel);
            if (!response)
                throw new Error('No response received');
            if (!_this.checkMarker(response))
                throw new Error(`Invalid response ${JSON.stringify(response)}`);
            if (!response.success)
                throw new Error(response.result);
            return _this.processAfterSerialization(response.result, replyChannel);
        };
        return fn;
    }
    createAsyncProxyFunction(objId, func, action, replyChannel) {
        // eslint-disable-next-line @typescript-eslint/no-this-alias
        const _this = this;
        const fn = function (...args) {
            return new Promise((resolve, reject) => {
                if (fn[rpc_disposed])
                    throw new Error('Remote function has been disposed');
                _this.callId++;
                _this.sendAsync({
                    action,
                    callType: 'async',
                    objId: objId ?? this[proxyObjectId],
                    callId: _this.callId.toString(),
                    // eslint-disable-next-line @typescript-eslint/no-non-null-assertion
                    prop: func.name,
                    args: _this.serializeFunctionArgs(func, args, replyChannel)
                }, replyChannel);
                _this.asyncCallbacks.set(_this.callId.toString(), { resolve, reject });
            });
        };
        return fn;
    }
    createProxyFunction(objId, prop, action, defaultCallType = 'async', replyChannel = this.channel) {
        const descriptor = (typeof prop === 'object') ? prop : { name: prop };
        let callType = descriptor?.returns || defaultCallType;
        if (callType === 'async' && !replyChannel.sendAsync)
            callType = 'sync';
        if (callType === 'sync' && !replyChannel.sendSync)
            callType = 'async';
        switch (callType) {
            case 'void': return this.createVoidProxyFunction(objId, descriptor, action, replyChannel);
            case 'sync': return this.createSyncProxyFunction(objId, descriptor, action, replyChannel);
            default: return this.createAsyncProxyFunction(objId, descriptor, action, replyChannel);
        }
    }
    /**
     * Gets or creates a proxy object that represents a host object from the other side.
     *
     * This side must have the descriptor for the object.
     * See [[sendRemoteDescriptors]], [[requestRemoteDescriptors]].
     */
    getProxyObject(objId) {
        let obj = this.proxyObjectRegistry.get(objId);
        if (obj)
            return obj;
        const descriptor = this.remoteObjectDescriptors?.[objId];
        if (!descriptor) {
            throw new Error(`No object registered with ID '${objId}'`);
        }
        obj = this.createProxyObject(objId, descriptor);
        this.proxyObjectRegistry.register(objId, obj);
        return obj;
    }
    /**
     * Gets or creates a proxy function that represents a host object from the other side.
     *
     * This side must have the descriptor for the function.
     * See [[sendRemoteDescriptors]], [[requestRemoteDescriptors]].
     */
    getProxyFunction(objId) {
        let obj = this.proxyObjectRegistry.get(objId);
        if (obj)
            return obj;
        const descriptor = this.remoteFunctionDescriptors?.[objId];
        if (!descriptor) {
            throw new Error(`No function registered with ID '${objId}'`);
        }
        obj = this.createProxyFunction(objId, descriptor, 'fn_call');
        this.proxyObjectRegistry.register(objId, obj);
        return obj;
    }
    /**
     * Gets or creates a proxy "class" that will serve multiple purposes.
     * - Static functions/properties on the class are proxied the same way as on a regular "host" object
     * - If specified the constructor actually constructs an instance of the registered host class on the other side
     * and the returned instance will represent the remote instance, with the specified functions/properties working
     * on its prototype as expected.
     * - If an instance of the registered host class is being sent from the other side,
     * an instance of this proxy class will be created and passed on this side.
     */
    getProxyClass(classId) {
        let clazz = this.proxyClassRegistry.get(classId);
        if (clazz)
            return clazz;
        const descriptor = this.remoteClassDescriptors?.[classId];
        if (!descriptor) {
            throw new Error(`No class registered with ID '${classId}'`);
        }
        clazz = (descriptor.ctor ? this.createProxyFunction(classId, descriptor.ctor, 'ctor_call', 'sync')
            : function () { throw new Error(`Constructor of class '${classId}' is not defined`); });
        // create the proxy functions/properties on the prototype with no objId, so each function will look up "proxyObjectId" on "this"
        // so the prototype will work with multiple instances
        this.createProxyObject(null, descriptor.instance, clazz.prototype);
        // add static functions/props
        const staticDescr = descriptor.static ?? {};
        const objDescr = this.remoteObjectDescriptors?.[classId];
        if (!isFunctionDescriptor(objDescr)) {
            staticDescr.props = objDescr?.props;
        }
        this.createProxyObject(classId, staticDescr, clazz);
        this.proxyClassRegistry.set(classId, clazz);
        return clazz;
    }
    createProxyObject(objId, descriptor, obj = {}) {
        Object.assign(obj, descriptor?.props);
        for (const prop of descriptor?.functions ?? []) {
            obj[getPropName(prop)] = this.createProxyFunction(objId, prop, 'method_call');
        }
        const setterCallType = this.channel.sendSync ? 'sync' : 'void';
        for (const prop of descriptor?.proxiedProperties ?? []) {
            const descr = typeof prop === 'string' ? { name: prop } : prop;
            Object.defineProperty(obj, descr.name, {
                get: this.createProxyFunction(objId, { ...descr.get, name: descr.name }, 'prop_get', 'sync'),
                set: descr.readonly ? undefined : this.createProxyFunction(objId, { ...descr.set, name: descr.name }, 'prop_set', setterCallType)
            });
        }
        obj[proxyObjectId] = objId;
        return obj;
    }
    registerLocalObj(obj, descriptor) {
        let objId = obj[hostObjectId];
        if (!this.hostObjectRegistry.has(objId)) {
            objId = this.objectIdGenerator();
            this.hostObjectRegistry.set(objId, { target: obj, descriptor });
            obj[hostObjectId] = objId;
        }
        return objId;
    }
    registerLocalFunc(obj, descriptor) {
        let objId = obj[hostObjectId];
        if (!this.hostFunctionRegistry.has(objId)) {
            objId = this.objectIdGenerator();
            this.hostFunctionRegistry.set(objId, { target: obj, descriptor });
            obj[hostObjectId] = objId;
        }
        return objId;
    }
    processBeforeSerialization(obj, replyChannel, descriptor) {
        if (obj?.[proxyObjectId]) {
            return { _rpc_type: 'hostObject', objId: obj[proxyObjectId] };
        }
        switch (typeof obj) {
            case 'object': {
                if (!obj)
                    break;
                // special case for Promise
                if (obj.constructor === Promise) {
                    if (!this.hostObjectRegistry.has(obj[hostObjectId])) {
                        let result;
                        let success;
                        obj.then((value) => { result = value; success = true; }, (value) => { result = value; success = false; }).finally(() => this.sendAsyncIfPossible({ action: 'fn_reply', callType: 'async', success, result, callId: objId }, replyChannel));
                    }
                    const objId = this.registerLocalObj(obj, {});
                    return { _rpc_type: 'object', objId, classId: 'Promise' };
                }
                const entry = this.hostClassRegistry.get(obj.constructor?.[classIdSym]);
                if (entry) {
                    const objId = this.registerLocalObj(obj, entry.descriptor.instance ?? {});
                    const props = {};
                    for (const prop of entry.descriptor.instance?.readonlyProperties ?? []) {
                        const propName = getPropName(prop);
                        props[propName] = this.processBeforeSerialization(obj[propName], replyChannel);
                    }
                    return { _rpc_type: 'object', classId: entry.descriptor.classId, props, objId };
                }
                for (const key of Object.keys(obj)) {
                    obj[key] = this.processBeforeSerialization(obj[key], replyChannel);
                }
                break;
            }
            case 'function': {
                const objId = this.registerLocalFunc(obj, descriptor);
                return { _rpc_type: 'function', objId };
            }
        }
        return obj;
    }
    processAfterSerialization(obj, replyChannel, descriptor) {
        if (typeof obj !== 'object' || !obj)
            return obj;
        switch (obj._rpc_type) {
            case 'object': {
                return this.getOrCreateProxyInstance(obj.objId, obj.classId, obj.props, replyChannel);
            }
            case 'function': {
                return this.getOrCreateProxyFunction(obj.objId, replyChannel, descriptor);
            }
            case 'hostObject': {
                return this.hostObjectRegistry.get(obj.objId)?.target;
            }
        }
        for (const key of Object.keys(obj)) {
            obj[key] = this.processAfterSerialization(obj[key], replyChannel, getPropertyDescriptor(descriptor, key));
        }
        return obj;
    }
    sendObjectDied(objId, replyChannel = this.channel) {
        this.sendAsyncIfPossible({ action: 'obj_died', objId }, replyChannel);
    }
    getOrCreateProxyInstance(objId, classId, props, replyChannel) {
        let obj = this.proxyObjectRegistry.get(objId);
        if (obj)
            return obj;
        obj = props ?? {};
        // special case for Promise
        if (classId === 'Promise') {
            obj = new Promise((resolve, reject) => this.asyncCallbacks.set(objId, { resolve, reject }));
        }
        else {
            obj[proxyObjectId] = objId;
            const clazz = this.getProxyClass(classId);
            Object.setPrototypeOf(obj, clazz.prototype);
        }
        this.proxyObjectRegistry.register(objId, obj, () => this.sendObjectDied(objId, replyChannel));
        return obj;
    }
    getOrCreateProxyFunction(objId, replyChannel, descriptor) {
        let fn = this.proxyObjectRegistry.get(objId);
        if (fn)
            return fn;
        if (descriptor)
            descriptor.type = 'function';
        fn = this.createProxyFunction(objId, descriptor, 'fn_call', 'async', replyChannel);
        fn[proxyObjectId] = objId;
        this.proxyObjectRegistry.register(objId, fn, () => this.sendObjectDied(objId, replyChannel));
        return fn;
    }
}
//# sourceMappingURL=super-rpc.js.map