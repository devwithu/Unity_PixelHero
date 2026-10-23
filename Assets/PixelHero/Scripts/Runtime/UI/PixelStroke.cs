using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// The original's text-shadow outlines (<c>.stroke</c>: 1px in all 8 directions), drawn as 8 offset copies of
    /// the glyph quads under the text. uGUI's Outline only draws the 4 diagonals, which leaves gaps in pixel fonts.
    /// </summary>
    public sealed class PixelStroke : BaseMeshEffect
    {
        public Color Colour = new Color32(0x37, 0x37, 0x37, 255);
        public float Distance = 1;
        /// <summary>Only these offsets (e.g. the footer's <c>0 1px 0 black</c>); null draws all 8.</summary>
        public Vector2[] Offsets;

        static readonly Vector2[] All =
        {
            new Vector2(1, 1), new Vector2(1, -1), new Vector2(0, 1), new Vector2(1, 0),
            new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(0, -1), new Vector2(-1, 0)
        };

        readonly List<UIVertex> _vertices = new List<UIVertex>();

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive()) return;

            _vertices.Clear();
            vh.GetUIVertexStream(_vertices);
            var count = _vertices.Count;
            var offsets = Offsets ?? All;
            var result = new List<UIVertex>(count * (offsets.Length + 1));

            foreach (var offset in offsets)
                for (var i = 0; i < count; i++)
                {
                    var v = _vertices[i];
                    v.position += (Vector3)(offset * Distance);
                    var c = Colour;
                    c.a *= v.color.a / 255f;
                    v.color = c;
                    result.Add(v);
                }

            result.AddRange(_vertices);
            vh.Clear();
            vh.AddUIVertexTriangleStream(result);
        }
    }
}
