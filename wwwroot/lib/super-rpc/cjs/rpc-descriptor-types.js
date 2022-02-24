"use strict";
/**
 * The descriptors are used to describe what properties/functions to expose on an object
 * and what are the function return behaviors.
 * @module
 */
Object.defineProperty(exports, "__esModule", { value: true });
exports.processObjectDescriptor = exports.processFunctionDescriptor = exports.isFunctionDescriptor = exports.getPropertyDescriptor = exports.getFunctionDescriptor = exports.getArgumentDescriptor = exports.getPropName = void 0;
// util functions
function getPropName(descriptor) {
    return typeof descriptor === 'string' ? descriptor : descriptor.name || '';
}
exports.getPropName = getPropName;
function getArgumentDescriptor(descriptor, idx) {
    return typeof descriptor === 'object' ? descriptor.arguments?.find(arg => arg.idx == null || arg.idx === idx) : undefined;
}
exports.getArgumentDescriptor = getArgumentDescriptor;
function getFunctionDescriptor(descriptor, funcName) {
    return descriptor?.functions?.find(func => typeof func === 'object' && func.name === funcName);
}
exports.getFunctionDescriptor = getFunctionDescriptor;
function getPropertyDescriptor(descriptor, propName) {
    return descriptor?.proxiedProperties?.find(prop => typeof prop === 'object' && prop.name === propName);
}
exports.getPropertyDescriptor = getPropertyDescriptor;
function isFunctionDescriptor(descriptor) {
    return descriptor?.type === 'function';
}
exports.isFunctionDescriptor = isFunctionDescriptor;
function processFunctionDescriptor(descriptor, func) {
    if (typeof descriptor === 'string')
        descriptor = { name: descriptor, type: 'function' };
    descriptor ??= { type: 'function' };
    descriptor.argCount = func.length;
    descriptor.name ??= func.name;
    return descriptor;
}
exports.processFunctionDescriptor = processFunctionDescriptor;
function processObjectDescriptor(descriptor, obj) {
    descriptor ??= { type: 'object' };
    if (obj && descriptor.functions) {
        for (const [idx, fdescr] of descriptor.functions.entries()) {
            descriptor.functions[idx] = processFunctionDescriptor(fdescr, obj[getPropName(fdescr)]);
        }
    }
    return descriptor;
}
exports.processObjectDescriptor = processObjectDescriptor;
//# sourceMappingURL=rpc-descriptor-types.js.map