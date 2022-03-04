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

    public record RPCSendAsyncChannel(Action<RPC_Message> sendAsync) : RPCChannelSendAsync {
        public void SendAsync(RPC_Message message) {
            sendAsync(message);
        }
    }

    public record RPCSendAsyncAndReceiveChannel(Action<RPC_Message> sendAsync) : RPCSendAsyncChannel(sendAsync), RPCChannelReceive {
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        
        public void Received(RPC_Message message, RPCChannel? replyChannel = null, object? context = null) {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message, replyChannel, context));
        }
    }

}

