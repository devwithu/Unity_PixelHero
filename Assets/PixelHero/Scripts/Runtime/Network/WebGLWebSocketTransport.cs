using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AOT;

namespace PixelHero.Network
{
    /// <summary>
    /// <see cref="IWebSocketTransport"/> for WebGL builds, backed by the browser's WebSocket through
    /// <c>Plugins/WebGL/PixelHeroWebSocket.jslib</c>. The jslib calls back into static methods with the
    /// socket id; events are queued and raised from <see cref="DispatchEvents"/> like the PC transport.
    /// Compiles everywhere so the editor checks it, but only works in a WebGL player.
    /// From the BrowserQuest port, plus the close code and reason.
    /// </summary>
    public sealed class WebGLWebSocketTransport : IWebSocketTransport
    {
        delegate void OpenCallback(int id);
        delegate void TextCallback(int id, IntPtr utf8, int length);
        delegate void CloseCallback(int id, int code, IntPtr utf8, int length);

        [DllImport("__Internal")] static extern void PixelHeroWebSocket_Init(OpenCallback onOpen, TextCallback onMessage, TextCallback onError, CloseCallback onClose);
        [DllImport("__Internal")] static extern int PixelHeroWebSocket_Connect(string url);
        [DllImport("__Internal")] static extern void PixelHeroWebSocket_Send(int id, string text);
        [DllImport("__Internal")] static extern void PixelHeroWebSocket_Close(int id);

        static readonly Dictionary<int, WebGLWebSocketTransport> Sockets = new Dictionary<int, WebGLWebSocketTransport>();
        static bool _initialized;

        readonly Queue<Action> _events = new Queue<Action>();
        int _id;
        TransportState _state = TransportState.Closed;

        public TransportState State => _state;

        public event Action Opened;
        public event Action<string> MessageReceived;
        public event Action<string> Error;
        public event Action<int, string> Closed;

        public void Connect(string url)
        {
            if (_state != TransportState.Closed)
                throw new InvalidOperationException($"Cannot connect while {_state}.");

            if (!_initialized)
            {
                PixelHeroWebSocket_Init(OnOpen, OnMessage, OnError, OnClose);
                _initialized = true;
            }

            _state = TransportState.Connecting;
            _id = PixelHeroWebSocket_Connect(url);
            if (_id <= 0)
            {
                _events.Enqueue(() => Error?.Invoke($"Cannot open a WebSocket to {url}."));
                RaiseClosed(1006, string.Empty);
                return;
            }
            Sockets[_id] = this;
        }

        public void Send(string text)
        {
            if (_state != TransportState.Open)
                return;
            PixelHeroWebSocket_Send(_id, text);
        }

        public void Close()
        {
            if (_state == TransportState.Closed || _state == TransportState.Closing)
                return;
            _state = TransportState.Closing;
            PixelHeroWebSocket_Close(_id);
        }

        public void DispatchEvents()
        {
            while (_events.Count > 0)
                _events.Dequeue()();
        }

        public void Dispose()
        {
            if (Sockets.Remove(_id))
                PixelHeroWebSocket_Close(_id);
            _events.Clear();
            _state = TransportState.Closed;
        }

        void RaiseClosed(int code, string reason)
        {
            Sockets.Remove(_id);
            _state = TransportState.Closed;
            _events.Enqueue(() => Closed?.Invoke(code, reason));
        }

        static string ReadUtf8(IntPtr utf8, int length)
        {
            if (length <= 0) return string.Empty;
            var bytes = new byte[length];
            Marshal.Copy(utf8, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        [MonoPInvokeCallback(typeof(OpenCallback))]
        static void OnOpen(int id)
        {
            if (!Sockets.TryGetValue(id, out var socket) || socket._state != TransportState.Connecting)
                return; // Close() was called while connecting; OnClose follows.
            socket._state = TransportState.Open;
            socket._events.Enqueue(() => socket.Opened?.Invoke());
        }

        [MonoPInvokeCallback(typeof(TextCallback))]
        static void OnMessage(int id, IntPtr utf8, int length)
        {
            if (!Sockets.TryGetValue(id, out var socket) || socket._state != TransportState.Open)
                return;
            var text = ReadUtf8(utf8, length);
            socket._events.Enqueue(() => socket.MessageReceived?.Invoke(text));
        }

        [MonoPInvokeCallback(typeof(TextCallback))]
        static void OnError(int id, IntPtr utf8, int length)
        {
            if (!Sockets.TryGetValue(id, out var socket) || socket._state == TransportState.Closing)
                return; // Closing a socket that is still connecting makes the browser report an error.
            var message = ReadUtf8(utf8, length);
            socket._events.Enqueue(() => socket.Error?.Invoke(message));
        }

        [MonoPInvokeCallback(typeof(CloseCallback))]
        static void OnClose(int id, int code, IntPtr utf8, int length)
        {
            if (Sockets.TryGetValue(id, out var socket))
                socket.RaiseClosed(code, ReadUtf8(utf8, length));
        }
    }
}
