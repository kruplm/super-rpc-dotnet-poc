using System;
namespace SuperRPC
{
    public interface RPCChannel {}
    public interface RPCChannelSendSync: RPCChannel {
        /**
        * Sends a message and returns the response synchronously.
        */
        object SendSync(RPC_Message message);
    }

    public interface RPCChannelSendAsync: RPCChannel {
        /**
        * Sends a message asnychronously. The response will come via the `receive` callback function.
        */
        void SendAsync(RPC_Message message);
    }

    public record MessageReceivedEventArgs(RPC_Message message, RPCChannel? replyChannel = null, object? context = null);

    public interface RPCChannelReceive: RPCChannel {
        /**
        * Event for when an async message arrives.
        */
        event EventHandler<MessageReceivedEventArgs> MessageReceived;
    }

    public record RPCAsyncAndReceiveChannel(Action<RPC_Message> sendAsync) : RPCChannelSendAsync, RPCChannelReceive {
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        public void SendAsync(RPC_Message message) {
            sendAsync(message);
        }
        
        public void Received(RPC_Message message, RPCChannel? replyChannel = null, object? context = null) {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message, replyChannel, context));
        }
    }
}

