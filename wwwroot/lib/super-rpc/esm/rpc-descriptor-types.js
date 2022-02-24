/**
 * The descriptors are used to describe what properties/functions to expose on an object
 * and what are the function return behaviors.
 * @module
 */
// util functions
export function getPropName(descriptor) {
    return typeof descriptor === 'string' ? descriptor : descriptor.name || '';
}
export function getArgumentDescriptor(descriptor, idx) {
    return typeof descriptor === 'object' ? descriptor.arguments?.find(arg => arg.idx == null || arg.idx === idx) : undefined;
}
export function getFunctionDescriptor(descriptor, funcName) {
    return descriptor?.functions?.find(func => typeof func === 'object' && func.name === funcName);
}
export function getPropertyDescriptor(descriptor, propName) {
    return descriptor?.proxiedProperties?.find(prop => typeof prop === 'object' && prop.name === propName);
}
export function isFunctionDescriptor(descriptor) {
    return descriptor?.type === 'function';
}
export function processFunctionDescriptor(descriptor, func) {
    if (typeof descriptor === 'string')
        descriptor = { name: descriptor, type: 'function' };
    descriptor ??= { type: 'function' };
    descriptor.argCount = func.length;
    descriptor.name ??= func.name;
    return descriptor;
}
export function processObjectDescriptor(descriptor, obj) {
    descriptor ??= { type: 'object' };
    if (obj && descriptor.functions) {
        for (const [idx, fdescr] of descriptor.functions.entries()) {
            descriptor.functions[idx] = processFunctionDescriptor(fdescr, obj[getPropName(fdescr)]);
        }
    }
    return descriptor;
}
//# sourceMappingURL=rpc-descriptor-types.js.map