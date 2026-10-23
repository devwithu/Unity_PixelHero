using System;
using System.Collections.Generic;
using PixelHero.Entities;
using PixelHero.Sprites;
using PixelHero.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace PixelHero.Game
{
    /// <summary>
    /// The #overlay canvas: renderer.ts drawOverlays on a screen-sized render texture (premultiplied, like the entity
    /// canvases).
    /// <list type="number">
    /// <item>The overlay image (fog) is stretched over the canvas.</item>
    /// <item>illuminated's DarkMask is drawn on its own canvas: the colour fills it, then each visible lamp's mask (a
    /// radial gradient, opaque at the centre and clear at radius t) is drawn with destination-out at its stamp
    /// position. The result is drawn over the image.</item>
    /// </list>
    /// A stamp whose radius is not finite is skipped; the original throws there (see <see cref="Lights"/>).
    /// </summary>
    public sealed class OverlayCanvasRenderer : IDisposable
    {
        readonly SpriteCatalog _catalog;
        readonly Material _sourceOver, _destinationOut;
        readonly CommandBuffer _commands = new CommandBuffer { name = "PixelHero overlay canvas" };
        readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly Dictionary<int, Texture2D> _gradients = new Dictionary<int, Texture2D>();
        readonly RenderTexture _darkMask;
        Texture2D _fill;
        string _fillColour;

        public readonly RenderTexture Overlay;

        public OverlayCanvasRenderer(SpriteCatalog catalog, int width, int height)
        {
            _catalog = catalog;
            _sourceOver = EntityCanvasRenderer.CanvasMaterial("source-over", BlendMode.One, BlendMode.OneMinusSrcAlpha);
            _destinationOut = EntityCanvasRenderer.CanvasMaterial("destination-out", BlendMode.Zero, BlendMode.OneMinusSrcAlpha);
            Overlay = CreateCanvas(width, height, "overlay");
            _darkMask = CreateCanvas(width, height, "illuminated-dm");
        }

        static RenderTexture CreateCanvas(int width, int height, string name)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Point
            };
            texture.Create();
            return texture;
        }

        public void Render(Lights lights)
        {
            _commands.Clear();
            foreach (var mesh in _meshes) Destroy(mesh);
            _meshes.Clear();

            Target(Overlay);
            _commands.ClearRenderTarget(false, true, Color.clear);

            var stamps = lights.Draw();
            if (stamps != null)
            {
                float width = Overlay.width, height = Overlay.height;
                var image = lights.OverlayImage != null ? _catalog.GetTexture(lights.OverlayImage) : null;
                if (image != null) Draw(image, 0, 0, width, height, _sourceOver);

                // DarkMask.compute.
                Target(_darkMask);
                _commands.ClearRenderTarget(false, true, Color.clear);
                Draw(Fill(lights.DarkColour), 0, 0, width, height, _sourceOver);
                foreach (var stamp in stamps)
                {
                    if (!stamp.IsFinite) continue;
                    var radius = (int)stamp.Radius;
                    Draw(Gradient(radius), (float)stamp.X, (float)stamp.Y, 2 * radius, 2 * radius, _destinationOut);
                }

                // DarkMask.render onto the overlay.
                Target(Overlay);
                Draw(_darkMask, 0, 0, width, height, _sourceOver, true);
            }

            Graphics.ExecuteCommandBuffer(_commands);
        }

        void Target(RenderTexture target)
        {
            _commands.SetRenderTarget(target);
            // Canvas pixels: x right, y down, origin top-left (the render-texture flip is applied by Unity).
            _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, target.width, target.height, 0, -1, 1));
        }

        void Draw(Texture texture, float x, float y, float width, float height, Material material, bool premultiplied = false)
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(x, y), new Vector3(x + width, y), new Vector3(x + width, y + height), new Vector3(x, y + height) });
            // Textures are bottom-up; render textures read here were drawn with the same flip.
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _meshes.Add(mesh);

            _properties.Clear();
            _properties.SetTexture("_MainTex", texture);
            _properties.SetVector("_MainTex_TexelSize", new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height));
            _properties.SetFloat("_Alpha", 1);
            _properties.SetFloat("_Premultiplied", premultiplied ? 1 : 0);
            _commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0, _properties);
        }

        /// <summary>The fillStyle as a 1×1 image; an invalid colour leaves the default black.</summary>
        Texture2D Fill(string colour)
        {
            if (_fill != null && _fillColour == colour) return _fill;
            if (_fill == null) _fill = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            _fill.SetPixel(0, 0, CssColour.Parse(colour, new Color32(0, 0, 0, 255)));
            _fill.Apply();
            _fillColour = colour;
            return _fill;
        }

        /// <summary>Light.getVisibleMaskCache: 2t × 2t, a radial gradient from opaque black at the centre to clear at t.</summary>
        Texture2D Gradient(int radius)
        {
            if (_gradients.TryGetValue(radius, out var made)) return made;

            var size = 2 * radius;
            made = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                double dx = x + 0.5 - radius, dy = y + 0.5 - radius;
                var alpha = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / radius);
                pixels[y * size + x] = new Color32(0, 0, 0, (byte)Math.Round(alpha * 255));
            }
            made.SetPixels32(pixels);
            made.Apply();
            _gradients[radius] = made;
            return made;
        }

        static void Destroy(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        public void Dispose()
        {
            foreach (var mesh in _meshes) Destroy(mesh);
            foreach (var texture in _gradients.Values) Destroy(texture);
            if (_fill != null) Destroy(_fill);
            Destroy(_sourceOver);
            Destroy(_destinationOut);
            Overlay.Release();
            _darkMask.Release();
            _commands.Dispose();
        }
    }
}
