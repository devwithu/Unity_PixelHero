using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>A clickable text that changes colour under the pointer (CSS <c>:hover { color }</c>).</summary>
    public sealed class HoverLink : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public Color Normal = Color.white;
        public Color Hover = Color.white;
        public Action Clicked;

        Graphic Graphic => GetComponent<Graphic>();

        void OnDisable() => Graphic.color = Normal;

        public void OnPointerEnter(PointerEventData eventData) => Graphic.color = Hover;

        public void OnPointerExit(PointerEventData eventData) => Graphic.color = Normal;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Clicked?.Invoke();
        }
    }
}
