using System;
using UnityEngine;

namespace PixelHero.UI
{
    /// <summary>
    /// menu/menu.ts: a window that fades in when shown (util.ts fadeIn, +0.1 opacity a frame) and out when hidden
    /// (fadeOut, -0.2 a frame), marks its HUD button active while shown, and tells the controller when it opens so
    /// that the other windows close (controllers/menu.ts hides every menu on show).
    /// </summary>
    public abstract class MenuPanel
    {
        protected readonly RectTransform Root;
        readonly CanvasGroup _group;
        float _target;

        /// <summary>Called before the panel shows (the controller hides the others).</summary>
        public Action Showing;

        /// <summary>The HUD button's active state (classList active).</summary>
        public Action<bool> ButtonActive;

        protected MenuPanel(RectTransform root)
        {
            Root = root;
            _group = root.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0;
            root.gameObject.SetActive(false);
        }

        /// <summary>display: flex (menu.ts isVisible).</summary>
        public bool IsVisible => _target > 0;

        public virtual void Show()
        {
            Showing?.Invoke();
            ButtonActive?.Invoke(true);
            _target = 1;
            Root.gameObject.SetActive(true);
            Root.SetAsLastSibling();
        }

        public virtual void Hide()
        {
            ButtonActive?.Invoke(false);
            _target = 0;
        }

        public void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }

        public virtual void Update(float deltaTime)
        {
            _group.alpha = Mathf.MoveTowards(_group.alpha, _target, (_target > _group.alpha ? 0.1f : 0.2f) * 60 * deltaTime);
            _group.blocksRaycasts = _group.interactable = _target > 0;
            if (_target == 0 && _group.alpha <= 0 && Root.gameObject.activeSelf) Root.gameObject.SetActive(false);
        }
    }
}
