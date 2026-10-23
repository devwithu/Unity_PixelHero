using PixelHero.Server.Js;

namespace PixelHero.Hub.Network
{
    /// <summary>A socket as the hub's handler sees it (uWS semantics: end() runs the close handler at once).</summary>
    public interface IHubSocket
    {
        /// <summary>getRemoteAddressAsText().</summary>
        string RemoteAddressText { get; }

        /// <summary>The handler's instance for this socket (uWS user data).</summary>
        string Instance { get; set; }

        void Send(string message);

        void End();
    }

    /// <summary>network/connection.ts.</summary>
    public sealed class Connection
    {
        public Action<object> messageCallback;

        public readonly IHubSocket socket;

        public Connection(IHubSocket socket) => this.socket = socket;

        /// <summary>Sends a message to the socket connection.</summary>
        public void Send(string message) => socket.Send(message);

        /// <summary>Gracefully closes the socket connection.</summary>
        public void Close() => socket.End();

        /// <summary>Callback for when a message is received over the socket (the parsed JSON).</summary>
        public void OnMessage(Action<object> callback) => messageCallback = callback;
    }
}
