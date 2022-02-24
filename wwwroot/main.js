const { SuperRPC } = superrpc;

let rpc, service, squareIt, MyService;

const ws = new WebSocket('ws://localhost:5050/super-rpc');
ws.addEventListener('open', async () => {
    rpc = new SuperRPC(() => (Math.random()*1e17).toString(36));

    rpc.connect({
        sendAsync: (message) => ws.send(JSON.stringify(message)),
        receive: (callback) => {
            ws.addEventListener('message', (msg) => callback(JSON.parse(msg.data)));
        }
    });

    await rpc.requestRemoteDescriptors();

    service = rpc.getProxyObject('service');
    squareIt = rpc.getProxyFunction('squareIt');
    MyService = rpc.getProxyClass('MyService');

    rpc.registerHostFunction('myfunc', (s1, s2) => {
        console.log(s1, s2);
        return s1 + s2;
    }, {});
    rpc.sendRemoteDescriptors();
});


