const { SuperRPC } = superrpc;

let rpc, service, squareIt, MyService, testJsHost;

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

    rpc.registerHostFunction('jsFunc', (s1, s2) => {
        console.log(s1, s2);
        return s1 + s2;
    }, {});


    rpc.registerHostObject('jsObj', {
        Add: (x, y) => x + y
    }, {
        functions: ['Add']
    });

    class JsService {
        Add(a, b) {
            return a + b;
        }
    }

    rpc.registerHostClass('JsService', JsService, {
        ctor: {},
        instance: {
            functions:['Add']
        }
    });

    const jsServiceInstance = new JsService();
    rpc.registerHostFunction('getJsService', () => jsServiceInstance);

    rpc.sendRemoteDescriptors();

    service = rpc.getProxyObject('service');
    squareIt = rpc.getProxyFunction('squareIt');
    MyService = rpc.getProxyClass('MyService');
    testJsHost = rpc.getProxyFunction('testJsHost');
});


