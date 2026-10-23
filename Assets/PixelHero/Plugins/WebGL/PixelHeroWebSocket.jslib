// Browser WebSocket bridge for PixelHero.Network.WebGLWebSocketTransport.
// Sockets are identified by integer ids; events go back to C# through function pointers
// registered once with PixelHeroWebSocket_Init (static [MonoPInvokeCallback] methods).
// From the BrowserQuest port; the close callback also carries the close reason, which the
// PixelHero server uses to say why it rejected the connection (code 1010, e.g. "invalidlogin").
var PixelHeroWebSocket = {
    $phws: {
        sockets: {},
        nextId: 1,
        onOpen: 0,
        onMessage: 0,
        onError: 0,
        onClose: 0,

        // Passes a JS string to C# as a temporary UTF-8 buffer (pointer + byte length).
        withUtf8: function (text, fn) {
            var length = lengthBytesUTF8(text);
            var ptr = _malloc(length + 1);
            stringToUTF8(text, ptr, length + 1);
            try {
                fn(ptr, length);
            } finally {
                _free(ptr);
            }
        }
    },

    PixelHeroWebSocket_Init: function (onOpen, onMessage, onError, onClose) {
        phws.onOpen = onOpen;
        phws.onMessage = onMessage;
        phws.onError = onError;
        phws.onClose = onClose;
    },

    PixelHeroWebSocket_Connect: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        var id = phws.nextId++;
        var socket;
        try {
            socket = new WebSocket(url);
        } catch (e) {
            // Invalid URL or a blocked scheme (e.g. ws:// from an https:// page).
            return -1;
        }
        phws.sockets[id] = socket;

        socket.onopen = function () {
            {{{ makeDynCall('vi', 'phws.onOpen') }}}(id);
        };
        socket.onmessage = function (event) {
            if (typeof event.data !== 'string')
                return; // The PixelHero protocol is JSON text only.
            phws.withUtf8(event.data, function (ptr, length) {
                {{{ makeDynCall('viii', 'phws.onMessage') }}}(id, ptr, length);
            });
        };
        socket.onerror = function () {
            // Browsers deliberately give no details for WebSocket errors.
            phws.withUtf8('WebSocket error (' + url + ')', function (ptr, length) {
                {{{ makeDynCall('viii', 'phws.onError') }}}(id, ptr, length);
            });
        };
        socket.onclose = function (event) {
            delete phws.sockets[id];
            phws.withUtf8(event.reason || '', function (ptr, length) {
                {{{ makeDynCall('viiii', 'phws.onClose') }}}(id, event.code, ptr, length);
            });
        };
        return id;
    },

    PixelHeroWebSocket_Send: function (id, textPtr) {
        var socket = phws.sockets[id];
        if (!socket || socket.readyState !== 1)
            return;
        socket.send(UTF8ToString(textPtr));
    },

    PixelHeroWebSocket_Close: function (id) {
        var socket = phws.sockets[id];
        if (!socket)
            return;
        if (socket.readyState === 0 || socket.readyState === 1)
            socket.close(1000);
    }
};

autoAddDeps(PixelHeroWebSocket, '$phws');
mergeInto(LibraryManager.library, PixelHeroWebSocket);
