using System;
using System.Collections.Generic;
using PixelHero.Sprites;
using UnityEngine;
using UnityEngine.Rendering;

namespace PixelHero.Entities
{
    /// <summary>
    /// Draws <see cref="EntityDraw"/> lists onto screen-sized render textures that behave like the client's entity
    /// canvases: premultiplied alpha, point sampling, the recorded transform, globalAlpha, and source-over or
    /// destination-over compositing. Canvas pixels have a top-left origin; the projection takes care of that.
    /// The mask canvas is not drawn: it is the #entities texture shown again at 20% opacity.
    /// </summary>
    public sealed class EntityCanvasRenderer : IDisposable
    {
        readonly SpriteCatalog _catalog;
        // Blend modes are render state: a MaterialPropertyBlock cannot change them, so each composite has a material.
        readonly Material _sourceOver;
        readonly Material _destinationOver;
        readonly Mesh _quad;
        readonly CommandBuffer _commands = new CommandBuffer { name = "PixelHero entity canvases" };
        readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        readonly List<Vector3> _vertices = new List<Vector3>(4);
        readonly List<Vector2> _uvs = new List<Vector2>(4);
        readonly List<Mesh> _meshes = new List<Mesh>();

        public readonly RenderTexture Entities;
        public readonly RenderTexture Fore;

        public EntityCanvasRenderer(SpriteCatalog catalog, int width, int height)
        {
            _catalog = catalog;
            _sourceOver = CanvasMaterial("source-over", BlendMode.One, BlendMode.OneMinusSrcAlpha);
            _destinationOver = CanvasMaterial("destination-over", BlendMode.OneMinusDstAlpha, BlendMode.One);
            _quad = new Mesh();
            Entities = CreateCanvas(width, height, "entities");
            Fore = CreateCanvas(width, height, "entities-fore");
        }

        /// <summary>A PixelHero/CanvasImage material with one composite operation.</summary>
        public static Material CanvasMaterial(string name, BlendMode source, BlendMode destination)
        {
            var material = new Material(Shader.Find("PixelHero/CanvasImage")) { name = $"PixelHero canvas image ({name})" };
            material.SetFloat("_SrcBlend", (float)source);
            material.SetFloat("_DstBlend", (float)destination);
            return material;
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

        /// <summary>Clears both canvases and draws the frame's list in order.</summary>
        public void Render(IReadOnlyList<EntityDraw> draws)
        {
            _commands.Clear();
            foreach (var mesh in _meshes) DestroyObject(mesh);
            _meshes.Clear();

            foreach (var canvas in new[] { Entities, Fore })
            {
                _commands.SetRenderTarget(canvas);
                _commands.ClearRenderTarget(false, true, Color.clear);
            }

            RenderTexture current = null;
            foreach (var draw in draws)
            {
                if (draw.Canvas == EntityCanvas.Mask || draw.Sprite == null) continue;

                var texture = Sheet(draw);
                // A canvas draws nothing for an image that has not loaded (or does not exist).
                if (texture == null || !draw.Sprite.Loaded) continue;

                var target = draw.Canvas == EntityCanvas.Entities ? Entities : Fore;
                if (target != current)
                {
                    _commands.SetRenderTarget(target);
                    // Canvas pixels: x right, y down, origin top-left.
                    // SetViewProjectionMatrices applies the render-texture flip itself; no GL.GetGPUProjectionMatrix here.
                    var projection = Matrix4x4.Ortho(0, target.width, target.height, 0, -1, 1);
                    _commands.SetViewProjectionMatrices(Matrix4x4.identity, projection);
                    current = target;
                }

                var mesh = Quad(draw, texture);
                _meshes.Add(mesh);

                _properties.Clear();
                _properties.SetTexture("_MainTex", texture);
                _properties.SetVector("_MainTex_TexelSize", new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height));
                _properties.SetFloat("_Alpha", (float)draw.Alpha);
                var material = draw.Composite == "destination-over" ? _destinationOver : _sourceOver;

                var matrix = Matrix4x4.identity;
                matrix.m00 = (float)draw.A;
                matrix.m10 = (float)draw.B;
                matrix.m01 = (float)draw.C;
                matrix.m11 = (float)draw.D;
                matrix.m03 = (float)draw.E;
                matrix.m13 = (float)draw.F;

                _commands.DrawMesh(mesh, matrix, material, 0, 0, _properties);
            }

            Graphics.ExecuteCommandBuffer(_commands);
        }

        readonly Dictionary<(string, SpriteVariant), Texture2D> _variants = new Dictionary<(string, SpriteVariant), Texture2D>();

        /// <summary>The image a draw uses: the sheet, or its generated hurt / silhouette copy (made once).</summary>
        Texture2D Sheet(EntityDraw draw)
        {
            var sheet = _catalog.GetSheet(draw.Sprite.Info);
            if (sheet == null || draw.Variant == SpriteVariant.Normal) return sheet;

            var key = (draw.Sprite.Key, draw.Variant);
            if (_variants.TryGetValue(key, out var made)) return made;

            var bytes = ReadCanvasBytes(sheet);
            bytes = draw.Variant == SpriteVariant.Hurt ? SpriteVariants.Hurt(bytes) : SpriteVariants.Silhouette(bytes, sheet.width, sheet.height);

            made = new Texture2D(sheet.width, sheet.height, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"{draw.Sprite.Key} ({draw.Variant})"
            };
            var pixels = new Color32[sheet.width * sheet.height];
            for (var y = 0; y < sheet.height; y++)
            for (var x = 0; x < sheet.width; x++)
            {
                var i = (y * sheet.width + x) * 4;
                pixels[(sheet.height - 1 - y) * sheet.width + x] = new Color32(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
            }
            made.SetPixels32(pixels);
            made.Apply();
            _variants[key] = made;
            return made;
        }

        /// <summary>A sheet's RGBA bytes, rows top to bottom (the sheets are imported non-readable, so via a blit).</summary>
        static byte[] ReadCanvasBytes(Texture2D sheet)
        {
            var temporary = RenderTexture.GetTemporary(sheet.width, sheet.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(sheet, temporary);
            var previous = RenderTexture.active;
            RenderTexture.active = temporary;
            var copy = new Texture2D(sheet.width, sheet.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, sheet.width, sheet.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);

            var pixels = copy.GetPixels32();
            DestroyObject(copy);
            var bytes = new byte[pixels.Length * 4];
            for (var y = 0; y < sheet.height; y++)
            for (var x = 0; x < sheet.width; x++)
            {
                var p = pixels[(sheet.height - 1 - y) * sheet.width + x];
                var i = (y * sheet.width + x) * 4;
                bytes[i] = p.r;
                bytes[i + 1] = p.g;
                bytes[i + 2] = p.b;
                bytes[i + 3] = p.a;
            }
            return bytes;
        }

        /// <summary>The destination rect in local space, textured with the source rect (textures are bottom-up).</summary>
        Mesh Quad(EntityDraw draw, Texture2D texture)
        {
            float x0 = (float)draw.Dx, y0 = (float)draw.Dy, x1 = (float)(draw.Dx + draw.Dw), y1 = (float)(draw.Dy + draw.Dh);
            float u0 = (float)(draw.Sx / texture.width), u1 = (float)((draw.Sx + draw.Sw) / texture.width);
            float v0 = (float)(1 - draw.Sy / texture.height), v1 = (float)(1 - (draw.Sy + draw.Sh) / texture.height);

            _vertices.Clear();
            _vertices.Add(new Vector3(x0, y0));
            _vertices.Add(new Vector3(x1, y0));
            _vertices.Add(new Vector3(x1, y1));
            _vertices.Add(new Vector3(x0, y1));

            _uvs.Clear();
            _uvs.Add(new Vector2(u0, v0));
            _uvs.Add(new Vector2(u1, v0));
            _uvs.Add(new Vector2(u1, v1));
            _uvs.Add(new Vector2(u0, v1));

            var mesh = new Mesh();
            mesh.SetVertices(_vertices);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        static void DestroyObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        public void Dispose()
        {
            foreach (var mesh in _meshes) DestroyObject(mesh);
            foreach (var texture in _variants.Values) DestroyObject(texture);
            DestroyObject(_quad);
            DestroyObject(_sourceOver);
            DestroyObject(_destinationOver);
            Entities.Release();
            Fore.Release();
            _commands.Dispose();
        }
    }
}
