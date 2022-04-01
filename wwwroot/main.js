const { SuperRPC } = superrpc;

let rpc, service, squareIt, MyService, testJsHost, testDTO, testServiceInstance;

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
            functions:[ { name: 'Add', returns: 'async' }]
        }
    });

    const jsServiceInstance = new JsService();
    rpc.registerHostFunction('getJsService', () => jsServiceInstance, {});

    // *** TestService *** //
    class TestService {
        Counter = 0;
        Increment() {
            this.Counter++;

            for (const listener of this.listeners) {
                listener(this.Counter);
            }
        }

        listeners = [];

        add_CounterChanged(listener) {
            this.listeners.push(listener);
        }
        remove_CounterChanged(listener) {
            const idx = this.listeners.indexOf(listener);
            if (idx >= 0) {
                this.listeners.splice(idx, 1);
            }
        }
    }
    rpc.registerHostClass('TestService', TestService, {
        instance: {
            functions:[
                { name: 'Increment', returns: 'void' },
                { name: 'add_CounterChanged', returns: 'void' },
                { name: 'remove_CounterChanged', returns: 'void' },
            ],
            proxiedProperties: ['Counter']
        }
    });

    testServiceInstance = new TestService();
    rpc.registerHostFunction('getTestService', () => testServiceInstance, {});

    rpc.sendRemoteDescriptors();

    service = rpc.getProxyObject('service');
    squareIt = rpc.getProxyFunction('squareIt');
    MyService = rpc.getProxyClass('MyService');
    testJsHost = rpc.getProxyFunction('testJsHost');
    testDTO = rpc.getProxyFunction('testDTO');
});


