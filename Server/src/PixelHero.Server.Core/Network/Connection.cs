using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Network
{
    /// <summary>
    /// The socket under a connection, with uWS semantics: end() runs the close handling synchronously (the
    /// server's <see cref="Sockets"/> layer does that), and send or end on a closed socket throws.
    /// </summary>
    public interface ISocket
    {
        /// <summary>getRemoteAddressAsText(), decoded (what Utils.bufferToAddress receives).</summary>
        string RemoteAddressText { get; }

        /// <summary>The `cf-connecting-ip` header value ('' when absent).</summary>
        string ForwardedAddress { get; }

        bool Closed { get; }

        /// <summary>The player instance stored in the socket's user data (set before the connection is announced).</summary>
        string Instance { get; set; }

        void Send(string message);

        void End(double code, string reason);
    }

    /// <summary>network/connection.ts.</summary>
    public sealed class Connection
    {
        public string address = "";

        public Action<object> messageCallback;

        // Used for filtering duplicate messages.
        string lastMessage = "";
        double lastMessageTime;
        readonly double messageDifference = 100; // Prevent duplicate messages coming in faster than 100ms.

        public double messageRate; // The amount of messages received in the last second.
        double timeoutDuration = 1000 * 60 * 10; // 10 minutes

        JsTimeout rateInterval;
        JsTimeout verifyInterval;
        JsTimeout disconnectTimeout;

        public bool closed;

        Action closeCallback;

        public string instance;
        readonly ISocket socket;

        public Connection(string instance, ISocket socket)
        {
            // Field initializers: lastMessageTime = Date.now().
            lastMessageTime = Node.DateNow();

            this.instance = instance;
            this.socket = socket;

            // Convert the IP address hex string to a readable IP address.
            address = JsData.Truthy(socket.ForwardedAddress) ? socket.ForwardedAddress : Utils.BufferToAddress(socket.RemoteAddressText);

            // Reset the messages per second every second.
            rateInterval = Node.SetInterval(() => messageRate = 0, 1000); // 1 second

            // Run the verification inteval every 30 seconds to ensure the connection is still open.
            verifyInterval = Node.SetInterval(IsClosed, 30_000); // 30 seconds

            Log.Info($"Received socket connection from: {address}.");
        }

        public void Reject(string reason)
        {
            // Tried rejecting an already closed connection, attempt to destroy the player class.
            if (closed)
            {
                HandleClose(reason);
                return;
            }

            Close(reason);
        }

        public void Close(string reason = null, bool force = false)
        {
            // Prevent accessing a closed connection.
            if (!closed) socket.End(1010, reason);

            if (JsData.Truthy(reason)) Log.Info($"Connection {address} has closed, reason: {reason}.");

            if (force) HandleClose();
        }

        public void HandleClose(string reason = null)
        {
            Log.Info($"Closing socket connection to: {address}.");

            if (JsData.Truthy(reason)) Log.Info($"Received reason: {reason}.");

            closeCallback?.Invoke();

            ClearTimeout();
            ClearRateInterval();
            ClearVerifyInterval();
        }

        public void UpdateTimeout(double duration)
        {
            timeoutDuration = duration;

            RefreshTimeout();
        }

        public void RefreshTimeout()
        {
            // Clear the existing timeout and start over.
            ClearTimeout();

            // Start a new timeout and set the player's timeout variable.
            disconnectTimeout = Node.SetTimeout(() => Reject("timeout"), timeoutDuration);
        }

        void ClearVerifyInterval()
        {
            if (verifyInterval == null) return;

            Node.ClearInterval(verifyInterval);
            verifyInterval = null;
        }

        void ClearRateInterval()
        {
            if (rateInterval == null) return;

            Node.ClearInterval(rateInterval);
            rateInterval = null;
        }

        void ClearTimeout()
        {
            if (disconnectTimeout == null) return;

            Node.ClearTimeout(disconnectTimeout);
            disconnectTimeout = null;
        }

        /// <summary>
        /// Port fix (ANALYSIS.md §9 #4; the harness patches the original the same way): the original never recorded the
        /// last message, so nothing was ever filtered. A message equal to the last accepted one within
        /// messageDifference is dropped.
        /// </summary>
        public bool IsDuplicate(string message)
        {
            if (message == lastMessage && Node.DateNow() - lastMessageTime < messageDifference) return true;

            lastMessage = message;
            lastMessageTime = Node.DateNow();

            return false;
        }

        void IsClosed()
        {
            if (!closed) return;

            Log.Warning($"Connection {address} closed improperly.");

            HandleClose();
        }

        public void Send(object message) => SendUTF8(PixelHero.Js.JsJson.Stringify(message));

        public void SendUTF8(string message)
        {
            // Prevent sending messages to a closed connection, log for debugging.
            if (closed)
            {
                Log.Warning("Attempted to send message to closed connection.");
                Log.Trace();
                return;
            }

            socket.Send(message);
        }

        public void OnMessage(Action<object> callback) => messageCallback = callback;

        public void OnClose(Action callback) => closeCallback = callback;
    }
}
