using System;
using System.Runtime.InteropServices;
using System.Text;
using AOT;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// Korean (IME) typing in WebGL builds on desktop browsers. The player has no IME, so while an InputField is
    /// focused, <c>Plugins/WebGL/PixelHeroTextInput.jslib</c> lays an invisible HTML input over it that takes the
    /// typing, and its value and selection are copied into the field every change. Unity's keyboard capture is off
    /// meanwhile; Enter and Escape come back as Input System key presses, so the existing
    /// <c>Keyboard.current.enterKey</c> checks still see them, and end the edit as a native InputField would.
    /// Mobile browsers already type through Unity's own HTML input (TouchScreenKeyboard); elsewhere this does nothing.
    /// </summary>
    public sealed class WebTextInput : MonoBehaviour
    {
        delegate void ChangeCallback(IntPtr utf8, int length, int selectionStart, int selectionEnd);
        delegate void KeyCallback(int key);

        [DllImport("__Internal")] static extern void PixelHeroTextInput_Init(ChangeCallback onChange, KeyCallback onKey);
        [DllImport("__Internal")] static extern void PixelHeroTextInput_Open(string text, bool password, int maxLength);
        [DllImport("__Internal")] static extern void PixelHeroTextInput_SetText(string text);
        [DllImport("__Internal")] static extern void PixelHeroTextInput_Place(float x, float y, float width, float height);
        [DllImport("__Internal")] static extern void PixelHeroTextInput_Close();

        static readonly Vector3[] Corners = new Vector3[4];

        // Filled by the browser callbacks, which run between frames; applied in Update.
        static string _pendingText;
        static int _pendingStart, _pendingEnd;
        static Key _pendingKey = Key.None;

        InputField _active;
        string _synced;
        string _original;
        Rect _placed;
        Key _releaseKey = Key.None;
        Key _endKey = Key.None;

        // TouchScreenKeyboard.isSupported is true in desktop browsers too; a field that opened Unity's mobile
        // keyboard has touchScreenKeyboard set instead (see Focused).
        static bool Supported => Application.platform == RuntimePlatform.WebGLPlayer && !Application.isMobilePlatform;

        void Awake()
        {
            if (!Supported)
            {
                enabled = false;
                return;
            }
            PixelHeroTextInput_Init(OnChange, OnKey);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            // The emulated press lasts one frame.
            if (_releaseKey != Key.None && keyboard != null && keyboard[_releaseKey].isPressed)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                _releaseKey = Key.None;
            }

            var focused = Focused();
            if (focused != _active)
            {
                if (_active != null) Close();
                if (focused != null) Open(focused);
            }
            if (_active == null) return;

            if (_pendingText != null)
            {
                _active.text = _pendingText;
                var length = _active.text.Length;
                _active.selectionAnchorPosition = Mathf.Clamp(_pendingStart, 0, length);
                _active.selectionFocusPosition = Mathf.Clamp(_pendingEnd, 0, length);
                _active.ForceLabelUpdate();
                _synced = _pendingText;
                _pendingText = null;
            }
            // The game or the field's validation changed the text: show it in the browser input too.
            if (_active.text != _synced)
            {
                _synced = _active.text;
                PixelHeroTextInput_SetText(_synced);
            }

            if (_pendingKey != Key.None && keyboard != null)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(_pendingKey));
                _releaseKey = _endKey = _pendingKey;
                _pendingKey = Key.None;
            }

            Place();
        }

        void LateUpdate()
        {
            if (_endKey == Key.None) return;
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard[_endKey].wasPressedThisFrame) return;
            // The game's own handlers have seen the key this frame; finish as the native InputField: Enter submits,
            // Escape restores the text from before the edit.
            var key = _endKey;
            _endKey = Key.None;
            if (_active == null || !_active.isFocused) return;
            var field = _active;
            if (key == Key.Enter) field.onSubmit?.Invoke(field.text);
            else field.text = _original;
            field.DeactivateInputField();
        }

        void OnDisable()
        {
            if (_active != null) Close();
        }

        static InputField Focused()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent<InputField>(out var field) && field.isFocused && field.interactable &&
                   field.touchScreenKeyboard == null
                ? field
                : null;
        }

        void Open(InputField field)
        {
            _active = field;
            _synced = _original = field.text;
            _pendingText = null;
            _pendingKey = Key.None;
            _placed = default;
            SetCapture(false);
            PixelHeroTextInput_Open(field.text, field.inputType == InputField.InputType.Password, field.characterLimit);
            Place();
        }

        void Close()
        {
            _active = null;
            _pendingText = null;
            _pendingKey = Key.None;
            PixelHeroTextInput_Close();
            SetCapture(true);
        }

        /// <summary>Moves the browser input over the field (canvas pixels, origin top-left).</summary>
        void Place()
        {
            var canvas = _active.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            var camera = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            ((RectTransform)_active.transform).GetWorldCorners(Corners);
            var min = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(camera, Corners[2]);
            var rect = new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
            if (rect == _placed) return;
            _placed = rect;
            PixelHeroTextInput_Place(rect.x, rect.y, rect.width, rect.height);
        }

        static void SetCapture(bool capture)
        {
#if UNITY_WEBGL
            WebGLInput.captureAllKeyboardInput = capture;
#endif
        }

        [MonoPInvokeCallback(typeof(ChangeCallback))]
        static void OnChange(IntPtr utf8, int length, int selectionStart, int selectionEnd)
        {
            if (length <= 0) _pendingText = string.Empty;
            else
            {
                var bytes = new byte[length];
                Marshal.Copy(utf8, bytes, 0, length);
                _pendingText = Encoding.UTF8.GetString(bytes);
            }
            _pendingStart = selectionStart;
            _pendingEnd = selectionEnd;
        }

        [MonoPInvokeCallback(typeof(KeyCallback))]
        static void OnKey(int key) => _pendingKey = key == 1 ? Key.Enter : key == 2 ? Key.Escape : Key.None;
    }
}
