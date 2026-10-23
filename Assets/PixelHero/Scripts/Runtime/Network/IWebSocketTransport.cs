using System;

namespace PixelHero.Network
{
    public enum TransportState
    {
        Closed,
        Connecting,
        Open,
        Closing
    }

    /// <summary>
    /// Platform-independent text WebSocket. Game code talks only to this interface so that the
    /// PC implementation (<see cref="ClientWebSocketTransport"/>) can be swapped for the WebGL .jslib-backed one.
    ///
    /// Events are never raised from background threads: implementations queue them, and
    /// <see cref="DispatchEvents"/> (called once per frame from the main thread) raises them in order.
    /// </summary>
    public interface IWebSocketTransport : IDisposable
    {
        TransportState State { get; }

        event Action Opened;
        event Action<string> MessageReceived;
        event Action<string> Error;

        /// <summary>
        /// Raised once when the connection ends, whether closed locally, remotely, or after a failure, with the close
        /// code and reason (1006 and "" when there was no close frame). The PixelHero server rejects with code 1010
        /// and a reason such as "invalidlogin" (server network/connection.ts reject).
        /// </summary>
        event Action<int, string> Closed;

        void Connect(string url);

        /// <summary>Queues a UTF-8 text frame. Messages are sent in call order. Ignored unless <see cref="State"/> is Open.</summary>
        void Send(string text);

        void Close();

        /// <summary>Raises queued events on the calling (main) thread.</summary>
        void DispatchEvents();
    }
}
