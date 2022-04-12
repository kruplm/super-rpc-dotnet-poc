"use strict";
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
Object.defineProperty(exports, "__esModule", { value: true });
exports.ProxyObjectRegistry = exports.rpc_disposeFunc = exports.rpc_disposed = void 0;
exports.rpc_disposed = Symbol('rpc_disposed');
exports.rpc_disposeFunc = Symbol('rpc_dispose');
class ProxyObjectRegistry {
    registry = new Map();
    objectFinalized = new FinalizationRegistry((rpc_dispose) => rpc_dispose());
    /**
     * Register an object.
     * @param dispose Called when the object is removed from the registry (either explicitly or by the GC)
     */
    register(objId, obj, dispose) {
        const unregToken = {};
        obj[exports.rpc_disposed] = false;
        obj[exports.rpc_disposeFunc] = () => {
            this.remoteObjectDisposed(objId, unregToken);
            obj[exports.rpc_disposed] = true;
            dispose?.();
        };
        this.objectFinalized.register(obj, obj[exports.rpc_disposeFunc], unregToken);
        this.registry.set(objId, new WeakRef(obj));
    }
    has(objId) {
        return this.registry.has(objId);
    }
    delete(objId) {
        this.registry.delete(objId);
    }
    get(objId) {
        return this.registry.get(objId)?.deref();
    }
    remoteObjectDisposed(objId, uregToken) {
        this.objectFinalized.unregister(uregToken);
        this.registry.delete(objId);
    }
}
exports.ProxyObjectRegistry = ProxyObjectRegistry;
//# sourceMappingURL=proxy-object-registry.js.map