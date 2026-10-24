// Browser text input bridge for PixelHero.UI.WebTextInput.
// Unity's WebGL player reads keys itself and has no IME, so Korean (and any composed) text cannot be typed into a
// uGUI InputField. While a field is focused, an invisible HTML <input> lies over it and takes the typing; its value,
// selection and Enter/Escape go back to C# through the function pointers registered with PixelHeroTextInput_Init.
var PixelHeroTextInput = {
    $phti: {
        element: null,
        onChange: 0,
        onKey: 0,
        composing: false,

        withUtf8: function (text, fn) {
            var length = lengthBytesUTF8(text);
            var ptr = _malloc(length + 1);
            stringToUTF8(text, ptr, length + 1);
            try {
                fn(ptr, length);
            } finally {
                _free(ptr);
            }
        },

        sendChange: function () {
            var input = phti.element;
            phti.withUtf8(input.value, function (ptr, length) {
                {{{ makeDynCall('viiii', 'phti.onChange') }}}(ptr, length, input.selectionStart || 0, input.selectionEnd || 0);
            });
        },

        create: function () {
            var input = document.createElement('input');
            input.setAttribute('autocomplete', 'off');
            input.setAttribute('autocorrect', 'off');
            input.setAttribute('autocapitalize', 'off');
            input.setAttribute('spellcheck', 'false');
            var style = input.style;
            style.position = 'fixed';
            style.zIndex = '1000';
            style.margin = '0';
            style.padding = '0';
            style.border = '0';
            style.outline = 'none';
            style.background = 'transparent';
            style.color = 'transparent';
            style.caretColor = 'transparent';
            style.opacity = '0';
            style.display = 'none';

            input.addEventListener('input', phti.sendChange);
            input.addEventListener('keyup', phti.sendChange);
            input.addEventListener('click', phti.sendChange);
            // Keys typed here are not the game's: keep them from Unity's listeners further up the page.
            ['keydown', 'keyup', 'keypress'].forEach(function (type) {
                input.addEventListener(type, function (event) { event.stopPropagation(); });
            });
            input.addEventListener('compositionstart', function () { phti.composing = true; });
            input.addEventListener('compositionend', function () {
                phti.composing = false;
                phti.sendChange();
            });
            input.addEventListener('keydown', function (event) {
                // The Enter that commits a composition is not a submit (keyCode 229 covers Safari, which ends the
                // composition before this keydown).
                if (event.isComposing || phti.composing || event.keyCode === 229) return;
                var key = event.key === 'Enter' ? 1 : event.key === 'Escape' ? 2 : 0;
                if (key === 0) return;
                event.preventDefault();
                phti.sendChange();
                {{{ makeDynCall('vi', 'phti.onKey') }}}(key);
            });
            // A click on the canvas elsewhere ends the edit from C# (the field loses focus there); anything else
            // that takes the focus while the field is still being edited gives it back.
            input.addEventListener('blur', function () {
                setTimeout(function () {
                    if (input.style.display !== 'none' && document.hasFocus()) input.focus();
                }, 0);
            });
            document.body.appendChild(input);
            phti.element = input;
        }
    },

    PixelHeroTextInput_Init: function (onChange, onKey) {
        phti.onChange = onChange;
        phti.onKey = onKey;
    },

    PixelHeroTextInput_Open: function (textPtr, password, maxLength) {
        if (!phti.element) phti.create();
        var input = phti.element;
        input.type = password ? 'password' : 'text';
        if (maxLength > 0) input.maxLength = maxLength;
        else input.removeAttribute('maxlength');
        input.value = UTF8ToString(textPtr);
        phti.composing = false;
        input.style.display = 'block';
        input.focus();
        input.setSelectionRange(input.value.length, input.value.length);
    },

    PixelHeroTextInput_SetText: function (textPtr) {
        if (!phti.element) return;
        var input = phti.element;
        input.value = UTF8ToString(textPtr);
        input.setSelectionRange(input.value.length, input.value.length);
    },

    // x, y, width, height in canvas pixels with the origin top-left.
    PixelHeroTextInput_Place: function (x, y, width, height) {
        if (!phti.element) return;
        var canvas = Module.canvas;
        var rect = canvas.getBoundingClientRect();
        var scaleX = rect.width / canvas.width;
        var scaleY = rect.height / canvas.height;
        var style = phti.element.style;
        style.left = (rect.left + x * scaleX) + 'px';
        style.top = (rect.top + y * scaleY) + 'px';
        style.width = (width * scaleX) + 'px';
        style.height = (height * scaleY) + 'px';
        style.fontSize = Math.max(8, height * scaleY * 0.6) + 'px';
    },

    PixelHeroTextInput_Close: function () {
        if (!phti.element) return;
        phti.element.style.display = 'none';
        phti.element.blur();
        phti.composing = false;
    }
};

autoAddDeps(PixelHeroTextInput, '$phti');
mergeInto(LibraryManager.library, PixelHeroTextInput);
