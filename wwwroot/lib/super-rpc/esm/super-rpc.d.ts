import { AnyConstructor, AnyFunction, ClassDescriptor, FunctionDescriptor, ObjectDescriptor } from './rpc-descriptor-types';
import type { RPC_Message } from './rpc-message-types';
/**
 * The channel used for the communication.
 * Can support synchronous and/or asynchronous messages.
 *
 * Note: if sync/async is not supported, make sure to use the correct return type for functions: [[FunctionReturnBehavior]].
 */
export interface RPCChannel {
    /**
     * Sends a message and returns the response synchronously.
     */
    sendSync?: (message: RPC_Message) => any;
    /**
     * Sends a message asnychronously. The response will come via the `receive` callback function.
     */
    sendAsync?: (message: RPC_Message) => void;
    /**
     * Register a callback for when an async message arrives.
     * Note: The "context" is exposed during function calls via the [[SuperRPC.currentContext]] property.
     */
    receive?: (callback: (message: RPC_Message, replyChannel?: RPCChannel, context?: any) => void) => void;
}
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
export declare class SuperRPC {
    private objectIdGenerator;
    private channel;
    private remoteObjectDescriptors?;
    private remoteFunctionDescriptors?;
    private remoteClassDescriptors?;
    private remoteDescriptorsCallbacks?;
    private asyncCallbacks;
    private callId;
    private readonly proxyObjectRegistry;
    private readonly proxyClassRegistry;
    private readonly hostObjectRegistry;
    private readonly hostFunctionRegistry;
    private readonly hostClassRegistry;
    /**
     * @param objectIdGenerator A function to generate a unique ID for an object.
     *
     * When sending an object to the other side that can not be serialized, we
     * generate an ID and send that instead. The other side creates a proxy object
     * that represents the remote object.
     */
    constructor(objectIdGenerator: () => string);
    /**
     * Stores the current "context" object that is passed to the callback of the [[RPCChannel.receive]] function.
     */
    currentContext: any;
    /**
     * Connect the service to a channel.
     */
    connect(channel: RPCChannel): void;
    /**
     * Register an object in the service to be called remotely.
     * @param objId An ID that the "client" side uses to identify this object.
     * @param target The target object
     * @param descriptor Describes which functions/properties to expose
     */
    registerHostObject(objId: string, target: object, descriptor: ObjectDescriptor): void;
    /**
     * Register a function in the service to be called remotely.
     * @param objId An ID that the "client" side uses to identify this function.
     * @param target The target function
     * @param descriptor Describes arguments and return behavior ([[FunctionReturnBehavior]])
     */
    registerHostFunction(objId: string, target: AnyFunction, descriptor: FunctionDescriptor): void;
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
    registerHostClass(classId: string, classCtor: AnyConstructor, descriptor: ClassDescriptor): void;
    /**
     * Send a request to get the descriptors for the registered host objects from the other side.
     * Uses synchronous communication if possible and returns `true`/`false` based on if the descriptors were received.
     * If sync is not available, it uses async messaging and returns a Promise.
     */
    requestRemoteDescriptors(): boolean | Promise<void>;
    private setRemoteDescriptors;
    /**
     * Send the descriptors for the registered host objects to the other side.
     * If possible, the message is sent synchronously.
     * This is a "push" style message, for "pull" see [[requestRemoteDescriptors]].
     */
    sendRemoteDescriptors(replyChannel?: RPCChannel): void;
    private getLocalDescriptors;
    private sendSync;
    private sendAsync;
    private sendSyncIfPossible;
    private sendAsyncIfPossible;
    private addMarker;
    private checkMarker;
    private callTargetFunction;
    private messageReceived;
    private serializeFunctionArgs;
    private deserializeFunctionArgs;
    private createVoidProxyFunction;
    private createSyncProxyFunction;
    private createAsyncProxyFunction;
    private createProxyFunction;
    /**
     * Gets or creates a proxy object that represents a host object from the other side.
     *
     * This side must have the descriptor for the object.
     * See [[sendRemoteDescriptors]], [[requestRemoteDescriptors]].
     */
    getProxyObject(objId: string): any;
    /**
     * Gets or creates a proxy function that represents a host object from the other side.
     *
     * This side must have the descriptor for the function.
     * See [[sendRemoteDescriptors]], [[requestRemoteDescriptors]].
     */
    getProxyFunction(objId: string): any;
    /**
     * Gets or creates a proxy "class" that will serve multiple purposes.
     * - Static functions/properties on the class are proxied the same way as on a regular "host" object
     * - If specified the constructor actually constructs an instance of the registered host class on the other side
     * and the returned instance will represent the remote instance, with the specified functions/properties working
     * on its prototype as expected.
     * - If an instance of the registered host class is being sent from the other side,
     * an instance of this proxy class will be created and passed on this side.
     */
    getProxyClass(classId: string): AnyConstructor;
    private createProxyObject;
    private registerLocalObj;
    private registerLocalFunc;
    private processBeforeSerialization;
    private processAfterSerialization;
    private sendObjectDied;
    private getOrCreateProxyInstance;
    private getOrCreateProxyFunction;
}
