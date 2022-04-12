/**
 * Types describing the message object format that the library sends over the channel.
 *
 * These are for internal type safety, not to be used by the user.
 * @module
 * @internal
 */
import type { ClassDescriptors, ObjectDescriptors } from './rpc-descriptor-types';
import { FunctionDescriptors } from './rpc-descriptor-types';
export declare type RPC_Marker = {
    rpc_marker?: 'srpc';
};
export declare type RPC_GetDescriptorsMessage = RPC_Marker & {
    action: 'get_descriptors';
};
export declare type RPC_DescriptorsResultMessage = RPC_Marker & {
    action: 'descriptors';
    objects: ObjectDescriptors;
    functions: FunctionDescriptors;
    classes: ClassDescriptors;
};
export declare type RPC_FnCallMessageBase = RPC_Marker & {
    objId: string;
    args: any[];
};
export declare type RPC_VoidFnCallMessage = RPC_FnCallMessageBase & {
    callType: 'void';
};
export declare type RPC_SyncFnCallMessage = RPC_FnCallMessageBase & {
    callType: 'sync';
};
export declare type RPC_AsyncFnCallMessage = RPC_FnCallMessageBase & {
    callType: 'async';
    callId: string;
};
export declare type RPC_AnyCallTypeFnCallMessage = RPC_VoidFnCallMessage | RPC_SyncFnCallMessage | RPC_AsyncFnCallMessage;
export declare type RPC_FnCallMessage = {
    action: 'fn_call';
} & RPC_AnyCallTypeFnCallMessage;
export declare type RPC_CtorCallMessage = {
    action: 'ctor_call';
} & (RPC_SyncFnCallMessage | RPC_AsyncFnCallMessage);
export declare type RPC_PropGetMessage = {
    action: 'prop_get';
    prop: string;
} & (RPC_SyncFnCallMessage | RPC_AsyncFnCallMessage);
export declare type RPC_PropSetMessage = {
    action: 'prop_set';
    prop: string;
} & (RPC_VoidFnCallMessage | RPC_SyncFnCallMessage);
export declare type RPC_RpcCallMessage = {
    action: 'method_call';
    prop: string;
} & RPC_AnyCallTypeFnCallMessage;
export declare type RPC_AnyCallMessage = RPC_FnCallMessage | RPC_CtorCallMessage | RPC_PropGetMessage | RPC_PropSetMessage | RPC_RpcCallMessage;
export declare type RPC_AnyCallAction = RPC_AnyCallMessage['action'];
export declare type RPC_VoidCallAction = (RPC_AnyCallMessage & {
    callType: 'void';
})['action'];
export declare type RPC_SyncCallAction = (RPC_AnyCallMessage & {
    callType: 'sync';
})['action'];
export declare type RPC_AsyncCallAction = (RPC_AnyCallMessage & {
    callType: 'async';
})['action'];
export declare type RPC_FnResultMessageBase = RPC_Marker & {
    action: 'fn_reply';
    success: boolean;
    result: any;
};
export declare type RPC_SyncFnResultMessage = RPC_FnResultMessageBase & {
    callType: 'sync';
};
export declare type RPC_AsyncFnResultMessage = RPC_FnResultMessageBase & {
    callType: 'async';
    callId: string;
};
export declare type RPC_FnResultMessage = RPC_SyncFnResultMessage | RPC_AsyncFnResultMessage;
export declare type RPC_ObjectDiedMessage = RPC_Marker & {
    action: 'obj_died';
    objId: string;
};
export declare type RPC_AsyncCallbackCallMessage = RPC_Marker & {
    action: 'async_fn';
    objId: string;
    args: any[];
};
export declare type RPC_Message = RPC_GetDescriptorsMessage | RPC_DescriptorsResultMessage | RPC_AnyCallMessage | RPC_FnResultMessage | RPC_AsyncCallbackCallMessage | RPC_ObjectDiedMessage;
