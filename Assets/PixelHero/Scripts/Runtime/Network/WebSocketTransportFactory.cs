namespace PixelHero.Network
{
    public static class WebSocketTransportFactory
    {
        public static IWebSocketTransport Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // System.Net.WebSockets is unavailable in WebGL builds; use the browser's WebSocket.
            return new WebGLWebSocketTransport();
#else
            return new ClientWebSocketTransport();
#endif
        }
    }
}
