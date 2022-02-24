/**
 * Stores proxy objects/functions that represent remote objects - **used internally**.
 *
 * On the other side the corresponding "host" object/function is held in a registry by a strong reference,
 * and in order to be able to remove it and not leak the reference, we need a way to inform the other side
 * when the proxy object is "no longer used". For this we use the WeakRef and FinalizationRegistry features.
 *
 * We hold the proxy object/function with a weak reference, and when it is garbage collected, we can be sure that
 * it will not be used (called) anymore, so we remove it from our object registry and send a message
 * to the other side to remove the corresponding local object from the hostObjectRegistry as well.
 * @module
 * @internal
 */
export declare const rpc_disposed: unique symbol;
export declare const rpc_disposeFunc: unique symbol;
export declare class ProxyObjectRegistry {
    private readonly registry;
    private readonly objectFinalized;
    /**
     * Register an object.
     * @param dispose Called when the object is removed from the registry (either explicitly or by the GC)
     */
    register(objId: string, obj: any, dispose?: () => void): void;
    has(objId: string): boolean;
    delete(objId: string): void;
    get(objId: string): any;
    private remoteObjectDisposed;
}
