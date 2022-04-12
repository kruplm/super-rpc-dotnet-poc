# SuperRPC

This is a library that allows you to call functions (even constructors) across different JavaScript contexts. 
The communication channel is configurable, it has to be injected explicitly. 
It could be `window.postMessage()` or a WebSocket connection.

## Why another RPC library?
I looked at Electron's "remote" module for inspiration, but [it has been removed](https://www.electronjs.org/docs/latest/breaking-changes#removed-remote-module) and considered too vulnerable, so I thought I could create something like that, but more general.

I have found many libraries that implement RPC (remote procedure call),
but most of them provide a messaging-like API. Something like:
```ts
  rpc.invoke({ method: "addNumbers", params: [2, 3], id: 5 })
```

I don't like this API, I wanted something more like [.NET Remoting](https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-netod/bfd49902-36d7-4479-bf75-a2431bd99039), where you expose a "host" side object and generate a "proxy" object with the same interface. 
When a method is called on the proxy object, it calls through to the host object and sends the return value back.

There are some JavaScript libraries that can do this on NPM, however, I have some special requirements.
I wanted to make it possible to write code on the proxy side as if it was written on the host side.
This means:
  * Proxy property calls (getter/setter), not just function calls
  * The ability to pass functions to proxied functions
  * When sending an instance of a specific class, a similar (proxy) instance is created on the client side.
  * A constructor proxy should create the real object on the host side
  * Should work with any messaging channel, whether it supports synchronous or asynchronous communication or both

I wanted to avoid exposing too much to the client side in order to avoid security vulnerabilities. 
Therefore, the host side has to explicitly specify which functions to expose on the other side.

## Simple Usage
When you want to expose an object with functions on it, you can call `registerHostObject`.

```ts
// ---- Host side ----
const rpc = new SuperRPC(nanoid); // needs a function to generate unique IDs
rpc.connect(channel);             // see channel description later

const serviceObject = {
    addNumbers: (a: number, b: number) => a + b,
    getCustomerName: (customerId: string) => service.getCustomer(customerId).then(customer => customer.name);
};

rpc.registerHostObject('service12', serviceObj, { 
    // this ObjectDescriptor specifies which functions to expose
    functions: [
        'getCustomerName', // function return behavior is 'async' by default
        {
            name: 'addNumbers', returns: 'sync'
        }
    ]
});

// ---- Client side ----
const rpc = new SuperRPC(nanoid);
rpc.connect(channel);

// need to request the descriptors to be able to build proxy objects on this side
rpc.requestRemoteDescriptors(); 

const serviceProxy = rpc.getProxyObject('service12');

const result = serviceProxy.addNumbers(2, 3);   // sync call
const customerName = await serviceProxy.getCustomerName('12345'); // async call
```

## Terminology
SuperRPC needs a two-way channel and it connects the two ends. Many times I refer to those as **host** side and 
**client (or proxy)** side. However, the communication is symmetric (depending on the channel), so in most cases, 
either side can be the host or the client. Sometimes both sides act as both. It depends on which side *hosts* the 
object/function/class and which side connects to it by creating a proxy.

## The Channel
The channel can be any means of sending messages to the other side. Here's the interface without comments.

```ts
interface RPCChannel {
    sendSync?: (message: RPC_Message) => any;
    sendAsync?: (message: RPC_Message) => void;
    receive?: (callback: (message: RPC_Message, replyChannel?: RPCChannel, context?: any) => void) => void;
}
```
The user must provide an object with these functions to the `connect()` method.
All functions are optional, the library will use sync/async communication based on the availability.

Notice the `replyChannel` argument. This provides a way to reply to a received message back to the sender.

## Advanced Usage
SuperRPC provides the ability to proxy entire classes (or rather instances of clsses). 
This is done by registering the class as a "proxy class".

Here's a more refined example that exposes Electron's `BrowserWindow` to the web application.
```ts
// ---- Host side - main/Node process:
// For setting up the channel, please see the electron example.
rpc.registerHostClass('BrowserWindow', BrowserWindow, {
    ctor: { returns: 'sync' },
    static: {
        functions: [
            { name: 'fromId', returns: 'sync' }, 
            'getAllWindows'
        ],
    },
    instance: {
        readonlyProperties: ['id'],
        proxiedProperties: ['fullScreen', 'title'],
        functions: [
            'close', 'focus', 'blur', 'show', 'hide', 
            'setBounds', 'getBounds', 'getParentWindow', 'setParentWindow', 
            'loadURL',
            { name: 'addListener', returns: 'void', arguments: [
                { idx: 1, type: 'function', returns: 'void' }
            ]},
            { name: 'removeListener', returns: 'void', arguments: [
                { idx: 1, type: 'function', returns: 'void' }
            ]}
        ]
    }
});


// ---- Client side - web app:
export const BrowserWindow = rpc.getProxyClass('BrowserWindow');

const mainWindow = BrowserWindow.fromId(1); // Electron starts numbering from 1

// The constructor works as expected, returning a proxy object that represents the real BrowserWindow that is created in the main process
const popupWindow = new BrowserWindow({ 
    width: 1200, 
    height: 850
});

popupWindow.title = 'Example Popup';

await popupWindow.setParentWindow(mainWindow);

await popupWindow.loadURL('https://github.com');
popupWindow.addListener('move', () => console.log('window moved'));
```

## Object Lifecycle
One feature of this library is how it manages object lifecycle.
Imagine that you pass an event listener function to a proxied function:

```ts
    proxyWindow.addListener('move', () => console.log('window moved'));
```

In this example, the user code does not keep a reference to the function. Because we can not send the function, 
but only an ID to the other side, the library needs to store the function on this (client) side, so we can actually 
call it when we get a message from the host side. 

The question is: *How long do we keep the reference to this function?*

To avoid a memory leak we need to remove the refence eventually. Ideally, we want to keep it as long as the host side 
keeps the reference to the generated proxy function. For example, if we pass a function to an `addListener()` then it 
will keep the reference until we remove the function with `removeListener()`, but in case we pass a function to 
`setTimeout()` it will drop the reference after the first call. The library is not able to identify these behaviors. 
However, there is a way to detect when the host side releases a reference.

The key is in a relatively new feature set: [`WeakRef`](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/WeakRef)
and [`FinalizationRegistry`](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/FinalizationRegistry).

The `ProxyObjectRegistry` class stores proxy objects wrapped in WeakRef objects. This makes sure we don't hold a 
strong reference to these objects. When the host code releases the reference and the Garbage Collector decides to 
collect it, we get a callback through the `FinalizationRegistry`. Then we send a message to the client side to release 
the reference to the original object/function.

This mechanism does not immediately release the original object as soon as the proxy object is released, 
but if the user code keeps adding and removing objects/functions, then eventually the GC will kick in and collect them, 
and at that point we also release the corresponding original objects/functions.

