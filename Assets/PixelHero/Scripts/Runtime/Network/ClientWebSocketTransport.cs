#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelHero.Network
{
    /// <summary>
    /// <see cref="IWebSocketTransport"/> for PC/Editor, built on System.Net.WebSockets.ClientWebSocket.
    /// Socket I/O runs on thread-pool tasks; all events are queued and raised from <see cref="DispatchEvents"/>.
    /// From the BrowserQuest port, plus the close code and reason.
    /// </summary>
    public sealed class ClientWebSocketTransport : IWebSocketTransport
    {
        const int ReceiveBufferSize = 16 * 1024;
        const int AbnormalClosure = 1006;

        readonly ConcurrentQueue<Action> _events = new ConcurrentQueue<Action>();
        readonly ConcurrentQueue<string> _outgoing = new ConcurrentQueue<string>();
        readonly SemaphoreSlim _outgoingSignal = new SemaphoreSlim(0);

        ClientWebSocket _socket;
        CancellationTokenSource _cts;
        volatile TransportState _state = TransportState.Closed;
        int _closedRaised;

        public TransportState State => _state;

        public event Action Opened;
        public event Action<string> MessageReceived;
        public event Action<string> Error;
        public event Action<int, string> Closed;

        public void Connect(string url)
        {
            if (_state != TransportState.Closed)
                throw new InvalidOperationException($"Cannot connect while {_state}.");

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _closedRaised = 0;
            _state = TransportState.Connecting;
            _ = RunAsync(new Uri(url), _socket, _cts.Token);
        }

        public void Send(string text)
        {
            if (_state != TransportState.Open)
                return;
            _outgoing.Enqueue(text);
            _outgoingSignal.Release();
        }

        public void Close()
        {
            if (_state == TransportState.Closed || _state == TransportState.Closing)
                return;
            _state = TransportState.Closing;
            _ = CloseAsync(_socket, _cts);
        }

        public void DispatchEvents()
        {
            while (_events.TryDequeue(out var raise))
                raise();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _socket?.Dispose();
            _state = TransportState.Closed;
        }

        async Task RunAsync(Uri uri, ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                if (_state != TransportState.Connecting) // Close() was called while connecting
                    return;
                _state = TransportState.Open;
                _events.Enqueue(() => Opened?.Invoke());

                _ = SendLoopAsync(socket, token);
                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    _events.Enqueue(() => Error?.Invoke(e.Message));
            }
            finally
            {
                var code = socket.CloseStatus.HasValue ? (int)socket.CloseStatus.Value : AbnormalClosure;
                RaiseClosedOnce(code, socket.CloseStatusDescription ?? string.Empty);
            }
        }

        async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var message = new MemoryStream();

            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token).ConfigureAwait(false);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    _events.Enqueue(() => MessageReceived?.Invoke(text));
                }
                message.SetLength(0);
            }
        }

        async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _outgoingSignal.WaitAsync(token).ConfigureAwait(false);
                    if (!_outgoing.TryDequeue(out var text) || socket.State != WebSocketState.Open)
                        continue;

                    var bytes = Encoding.UTF8.GetBytes(text);
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    _events.Enqueue(() => Error?.Invoke(e.Message));
            }
        }

        async Task CloseAsync(ClientWebSocket socket, CancellationTokenSource cts)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The connection is going away regardless; the receive loop reports the close.
            }
            finally
            {
                cts.Cancel();
                RaiseClosedOnce((int)WebSocketCloseStatus.NormalClosure, string.Empty);
            }
        }

        void RaiseClosedOnce(int code, string reason)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0)
                return;
            _state = TransportState.Closed;
            _events.Enqueue(() => Closed?.Invoke(code, reason));
        }
    }
}
#endif
