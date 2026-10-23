using System.Net;
using System.Threading.Channels;
using PixelHero.Server.Network;
using SuperSocket.WebSocket;
using SuperSocket.WebSocket.Server;

namespace PixelHero.Server.Host
{
    /// <summary>
    /// SuperSocket answers a client's close frame by echoing it, then keeps the TCP connection open until the client
    /// drops it, and no handler sees the frame. uWS closes right away, which is what makes the player leave the world
    /// at once; this session does the same (from the BrowserQuest port, D:\Git\2dmmo Server/.../GameSession.cs).
    /// </summary>
    sealed class GameSession : WebSocketSession
    {
        /// <summary>Set by <see cref="SessionSocket"/> when the server itself starts the close handshake.</summary>
        public volatile bool ClosedByServer;

        public override async ValueTask SendAsync(WebSocketPackage message, CancellationToken cancellationToken)
        {
            await base.SendAsync(message, cancellationToken);
            if (message.OpCode == OpCode.Close && !ClosedByServer)
                await CloseAsync(SuperSocket.Connection.CloseReason.RemoteClosing);
        }
    }

    /// <summary>
    /// <see cref="ISocket"/> over a SuperSocket session, with uWS semantics on the game thread: End() marks the
    /// socket closed and runs the close handling synchronously; send or end afterwards throws. Frames go out from one
    /// pump task in the order the game thread queued them, and the close frame after them.
    /// </summary>
    sealed class SessionSocket : ISocket
    {
        readonly WebSocketSession _session;
        readonly Sockets _sockets;
        readonly Channel<(string Text, bool Close, double Code, string Reason)> _outgoing =
            Channel.CreateUnbounded<(string, bool, double, string)>(new UnboundedChannelOptions { SingleReader = true });

        public SessionSocket(WebSocketSession session, Sockets sockets)
        {
            _session = session;
            _sockets = sockets;

            var endPoint = session.RemoteEndPoint as IPEndPoint;
            RemoteAddressText = endPoint?.Address.ToString() ?? session.RemoteEndPoint?.ToString() ?? "";
            ForwardedAddress = session.HttpHeader?.Items?["cf-connecting-ip"] ?? "";

            _ = PumpAsync();
        }

        public string RemoteAddressText { get; }
        public string ForwardedAddress { get; }

        /// <summary>Only touched on the game thread.</summary>
        public bool Closed { get; set; }

        public string Instance { get; set; }

        void Check()
        {
            if (Closed) throw new InvalidOperationException("Invalid access of closed uWS.WebSocket/SSLWebSocket.");
        }

        public void Send(string message)
        {
            Check();
            _outgoing.Writer.TryWrite((message, false, 0, null));
        }

        public void End(double code, string reason)
        {
            Check();
            Closed = true;

            if (_session is GameSession game) game.ClosedByServer = true;
            _outgoing.Writer.TryWrite((null, true, code, reason ?? ""));

            // uWS calls the close handler from inside end().
            _sockets.HandleClose(Instance);
        }

        async Task PumpAsync()
        {
            try
            {
                await foreach (var item in _outgoing.Reader.ReadAllAsync())
                {
                    if (item.Close)
                    {
                        _outgoing.Writer.TryComplete();
                        await _session.CloseAsync((CloseReason)(int)item.Code, item.Reason, CancellationToken.None);
                        return;
                    }

                    await _session.SendAsync(item.Text);
                }
            }
            catch (Exception)
            {
                // The session is gone; SuperSocket reports the close through the session handler.
                _outgoing.Writer.TryComplete();
            }
        }
    }
}
