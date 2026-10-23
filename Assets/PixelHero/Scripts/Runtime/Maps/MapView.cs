using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Sprites;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace PixelHero.Maps
{
    /// <summary>
    /// Draws the map like the Canvas2D renderer (canvas.ts draw / drawAnimatedIndexes) onto Unity tilemaps:
    /// a full redraw of the visible window when the window changes, and in between only the cells of animated
    /// tiles whose frame advanced. Draw decisions come from <see cref="TileDrawPlanner"/>; each canvas gets
    /// <see cref="StackDepth"/> tilemaps so that a cell's stacked tiles keep their order.
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        public const int StackDepth = 4;

        /// <summary>Sorting order of the background canvas; entities draw between it and the foreground.</summary>
        public const int BackSortingOrder = -100;
        public const int ForeSortingOrder = 100;
        public const int OverlaySortingOrder = 200;

        ClientMap _map;
        SpriteCatalog _catalog;
        TileDrawPlanner _planner;
        Func<double> _clock;

        readonly Dictionary<TileLayer, Tilemap[]> _layers = new Dictionary<TileLayer, Tilemap[]>();
        readonly Dictionary<(int, int, int), Tile> _tiles = new Dictionary<(int, int, int), Tile>();
        readonly List<TileDraw> _draws = new List<TileDraw>();
        RectInt _window;
        bool _needsFullDraw = true;

        public ClientMap Map => _map;
        public TileDrawPlanner Planner => _planner;

        /// <param name="clock">The game time in milliseconds (Date.now() in the original).</param>
        public void Initialize(ClientMap map, SpriteCatalog catalog, Func<double> clock)
        {
            _map = map;
            _catalog = catalog;
            _clock = clock;

            var widths = map.Info.Tilesets.ToDictionary(t => t.FirstGid, t => catalog.GetTexture(t.TextureKey).width);
            _planner = new TileDrawPlanner(map, widths, clock);

            // Not `??`: a missing Unity component is a fake null.
            if (!TryGetComponent<Grid>(out var grid)) grid = gameObject.AddComponent<Grid>();
            grid.cellSize = Vector3.one;

            foreach (TileLayer layer in Enum.GetValues(typeof(TileLayer)))
            {
                var maps = new Tilemap[StackDepth];
                for (var depth = 0; depth < StackDepth; depth++)
                {
                    var child = new GameObject($"{layer}{depth}");
                    child.transform.SetParent(transform, false);
                    maps[depth] = child.AddComponent<Tilemap>();
                    var renderer = child.AddComponent<TilemapRenderer>();
                    renderer.sortingOrder = SortingOrder(layer) + depth;
                    renderer.mode = TilemapRenderer.Mode.Chunk;
                    if (UnlitMaterial != null) renderer.sharedMaterial = UnlitMaterial;
                }
                _layers[layer] = maps;
            }

            map.TileChanged += _ => _needsFullDraw = true;
        }

        static Material _unlitMaterial;

        /// <summary>
        /// The canvas draws tiles unlit (darkness and lights are a separate overlay), so the tilemaps use URP's
        /// unlit sprite shader instead of the 2D renderer's lit default.
        /// </summary>
        static Material UnlitMaterial
        {
            get
            {
                if (_unlitMaterial == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                    if (shader != null) _unlitMaterial = new Material(shader) { name = "PixelHero Map (Unlit)" };
                }
                return _unlitMaterial;
            }
        }

        static int SortingOrder(TileLayer layer) =>
            layer == TileLayer.Back ? BackSortingOrder : layer == TileLayer.Fore ? ForeSortingOrder : OverlaySortingOrder;

        /// <summary>
        /// The camera's visible cells (gridX, gridY, gridWidth, gridHeight). Like canvas.ts draw, a full redraw covers
        /// x from gridX - 2 to gridX + gridWidth - 1 (same for y).
        /// </summary>
        public void SetWindow(int gridX, int gridY, int gridWidth, int gridHeight)
        {
            var window = new RectInt(gridX - 2, gridY - 2, gridWidth + 2, gridHeight + 2);
            if (window.Equals(_window)) return;
            _window = window;
            _needsFullDraw = true;
        }

        /// <summary>Forces the next frame to redraw the whole window (renderer.forceRendering).</summary>
        public void ForceRedraw() => _needsFullDraw = true;

        void Update()
        {
            if (_map == null) return;
            Tick();
        }

        /// <summary>One renderer frame: updater.ts updateAnimatedTiles, then canvas.ts draw.</summary>
        public void Tick()
        {
            foreach (var index in _planner.UpdateAnimatedTiles(_clock())) DrawCell(index);

            if (_needsFullDraw)
            {
                FullDraw();
                _needsFullDraw = false;
            }
            else DrawAnimatedIndexes();
        }

        void FullDraw()
        {
            foreach (var maps in _layers.Values)
            foreach (var tilemap in maps)
                tilemap.ClearAllTiles();

            for (var y = _window.yMin; y < _window.yMax; y++)
            for (var x = _window.xMin; x < _window.xMax; x++)
            {
                if (_map.IsOutOfBounds(x, y)) continue;
                DrawCell(_map.CoordToIndex(x, y));
            }
        }

        /// <summary>canvas.ts drawAnimatedIndexes: redraw the known cells of tiles whose frame advanced.</summary>
        void DrawAnimatedIndexes()
        {
            foreach (var pair in _planner.AnimatedTileIndexes)
            {
                if (!_planner.AnimatedTiles.TryGetValue(pair.Key.ToString(), out var animatedTile) || animatedTile.Uploaded)
                    continue;

                foreach (var index in pair.Value) DrawCell(index);
                animatedTile.Uploaded = true;
            }
        }

        void DrawCell(int index)
        {
            var (x, y) = _map.IndexToCoord(index);
            var cell = MapCoords.ToCell(x, y);

            _draws.Clear();
            _planner.ParseTile(_map.Data[index], index, _draws);

            var depth = new Dictionary<TileLayer, int>();
            foreach (var draw in _draws)
            {
                depth.TryGetValue(draw.Layer, out var k);
                if (k >= StackDepth) continue;
                depth[draw.Layer] = k + 1;

                var tilemap = _layers[draw.Layer][k];
                tilemap.SetTile(cell, GetTile(draw));
                tilemap.SetTransformMatrix(cell, MapCoords.TileTransform(draw.Ux, draw.Uy, draw.Vx, draw.Vy));
            }

            foreach (var pair in _layers)
            {
                depth.TryGetValue(pair.Key, out var used);
                for (var k = used; k < StackDepth; k++) pair.Value[k].SetTile(cell, null);
            }
        }

        Tile GetTile(TileDraw draw)
        {
            var key = (draw.Tileset.FirstGid, draw.Sx, draw.Sy);
            if (_tiles.TryGetValue(key, out var tile)) return tile;

            var texture = _catalog.GetTexture(draw.Tileset.TextureKey);
            var size = _map.Info.TileSize;
            // The canvas measures from the top; Unity textures from the bottom.
            var rect = new Rect(draw.Sx, texture.height - draw.Sy - size, size, size);

            tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), size, 0, SpriteMeshType.FullRect);
            tile.flags = TileFlags.None; // allow per-cell transforms
            tile.colliderType = Tile.ColliderType.None;
            _tiles[key] = tile;
            return tile;
        }
    }
}
