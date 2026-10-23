using System;
using System.Collections.Generic;

namespace PixelHero.Maps
{
    /// <summary>The canvas a tile is drawn on (renderer.ts background/foreground/overlay).</summary>
    public enum TileLayer
    {
        Back,
        Fore,
        Overlay
    }

    /// <summary>
    /// One drawImage of the canvas renderer for a map cell: a tileSize square of a tileset, and where the image's
    /// +x and +y axes point on screen (canvas coordinates, y down). OffsetX/Y is the screen position of the image's
    /// top-left corner relative to the cell's corner (a flip moves it to the opposite edge).
    /// </summary>
    public struct TileDraw
    {
        public TileLayer Layer;
        public TilesetRange Tileset;
        public int Sx;
        public int Sy;
        public int Ux;
        public int Uy;
        public int Vx;
        public int Vy;
        public int OffsetX;
        public int OffsetY;
    }

    /// <summary>An animated tile of the canvas renderer (client/src/map/tile.ts).</summary>
    public sealed class TileAnimation
    {
        public int Id;
        public readonly int Index;
        public readonly List<TileAnimationFrame> Frames;
        public readonly bool IsFlipped;
        public readonly bool IsHighTile;
        public readonly object PostAnimationData;

        public int AnimationIndex;
        public bool Expired;
        public bool Unused;
        public bool Uploaded = true;
        public double LastTime;
        public double LastAccessed;

        public TileAnimation(int id, int index, List<TileAnimationFrame> frames, bool isFlipped, bool isHighTile, object postAnimationData, double now)
        {
            Id = id;
            Index = index;
            Frames = frames;
            IsFlipped = isFlipped;
            IsHighTile = isHighTile;
            PostAnimationData = postAnimationData;
            LastAccessed = now;
        }

        /// <summary>
        /// tile.ts animate. The check uses the duration of the frame at AnimationIndex, which has already moved on
        /// to the next frame — so each frame stays up for the next frame's duration, like the original.
        /// </summary>
        public void Animate(double time)
        {
            if (time - LastAccessed > 5000) Unused = true;

            if (time - LastTime > Duration)
            {
                Id = Frames[AnimationIndex].TileId;
                LastTime = time;
                Uploaded = false;

                if (AnimationIndex >= Frames.Count - 1)
                {
                    if (PostAnimationData != null) Expired = true;
                    AnimationIndex = 0;
                }
                else AnimationIndex++;
            }
        }

        public int Duration => Frames[AnimationIndex].Duration;
    }

    /// <summary>
    /// Decides what the original Canvas renderer draws for a map cell (client/src/renderer/canvas.ts parseTile,
    /// drawVisibleTile, drawAnimatedTile, drawTile and drawImage), quirks included:
    /// a one-element array cell is drawn as a number but never counts as a high tile; animated tiles draw
    /// <c>frame tileId + 1</c>. (The original also skipped an animated tile the first time it met it; fixed, ANALYSIS.md
    /// §9 #12.)
    /// A flipped tile with id 0 makes the original throw; such a cell draws nothing here.
    /// </summary>
    public sealed class TileDrawPlanner
    {
        enum TileFlip
        {
            Horizontal,
            Vertical,
            Diagonal
        }

        readonly ClientMap _map;
        readonly int _tileSize;
        readonly Dictionary<int, int> _tilesetWidths;
        readonly Func<double> _now;
        readonly List<string> _animatedDynamicTiles = new List<string>();

        /// <summary>renderer.animateTiles.</summary>
        public bool AnimateTiles = true;

        /// <summary>game.overlays.hasOverlay(); light tiles then go to the overlay canvas.</summary>
        public Func<bool> HasOverlay = () => false;

        /// <summary>map.isLightTile (the original's lights list is never filled).</summary>
        public Func<int, bool> IsLightTile = _ => false;

        /// <summary>canvas.ts animatedTiles: keyed by tile id, or "tileId-index" for one-time (dynamic) animations.</summary>
        public readonly Dictionary<string, TileAnimation> AnimatedTiles = new Dictionary<string, TileAnimation>();

        /// <summary>canvas.ts animatedTileIndexes: the cells of each animated tile, redrawn as it animates.</summary>
        public readonly Dictionary<int, List<int>> AnimatedTileIndexes = new Dictionary<int, List<int>>();

        /// <param name="tilesetWidths">Tileset first gid → image width in pixels.</param>
        /// <param name="now">The game clock (Date.now() in the original).</param>
        public TileDrawPlanner(ClientMap map, Dictionary<int, int> tilesetWidths, Func<double> now)
        {
            _map = map;
            _tileSize = map.Info.TileSize;
            _tilesetWidths = tilesetWidths;
            _now = now;
        }

        /// <summary>canvas.ts parseTile: appends the draws of a cell to <paramref name="draws"/>.</summary>
        public void ParseTile(object tile, int index, List<TileDraw> draws)
        {
            if (tile is int id && id == 0) return;

            if (tile is TransformedTile transformed)
            {
                if (transformed.TileId != 0) DrawVisibleTile(tile, index, draws);
                // else: `~~tile === 0` → for..of over a plain object throws a TypeError in the original.
                return;
            }

            if (tile is List<object> list)
            {
                // ~~[n] is n for a one-element array, so it is drawn as a whole; other arrays give 0 and are iterated.
                if (list.Count == 1 && list[0] is int single && single != 0)
                    DrawVisibleTile(list, index, draws);
                else
                    foreach (var info in list) DrawVisibleTile(info, index, draws);
                return;
            }

            DrawVisibleTile(tile, index, draws);
        }

        /// <summary>Every draw of a cell, fresh.</summary>
        public List<TileDraw> Plan(int index)
        {
            var draws = new List<TileDraw>();
            ParseTile(_map.Data[index], index, draws);
            return draws;
        }

        void DrawVisibleTile(object tile, int index, List<TileDraw> draws)
        {
            var flips = GetFlipped(tile);
            if (flips.Count > 0) tile = ((TransformedTile)tile).TileId;

            // A one-element array reaches here whole: includes() never matches it, `in` and comparisons do.
            var isArray = tile is List<object>;
            var tileId = isArray ? (int)((List<object>)tile)[0] : (int)tile;

            var layer = !isArray && _map.IsHighTile(tileId) ? TileLayer.Fore : TileLayer.Back;

            if (HasOverlay() && !isArray && IsLightTile(tileId)) layer = TileLayer.Overlay;

            if (AnimateTiles && _map.IsAnimatedTile(tileId)) DrawAnimatedTile(tileId, index, flips, draws);
            else DrawTile(layer, tileId, index, flips, draws);
        }

        static List<TileFlip> GetFlipped(object tile)
        {
            var flips = new List<TileFlip>();
            if (!(tile is TransformedTile t) || !(t.V || t.H || t.D)) return flips;

            if (t.V) flips.Add(TileFlip.Vertical);
            if (t.D) flips.Add(TileFlip.Diagonal);
            if (t.H) flips.Add(TileFlip.Horizontal);
            return flips;
        }

        void DrawAnimatedTile(int tile, int index, List<TileFlip> flips, List<TileDraw> draws)
        {
            if (!AnimateTiles) return;

            var isDynamicallyAnimated = _map.DynamicAnimatedTiles.ContainsKey(index);
            var identifier = isDynamicallyAnimated ? $"{tile}-{index}" : tile.ToString();

            // Port fix (ANALYSIS.md §9 #12; the fixtures patch the original the same way): the original only registered a
            // tile met for the first time and left its first cell out of the redraw list, so the cell stayed empty.
            if (!AnimatedTiles.TryGetValue(identifier, out var animatedTile))
            {
                AddAnimatedTile(tile, isDynamicallyAnimated ? index : -1);
                if (!AnimatedTiles.TryGetValue(identifier, out animatedTile)) return;
            }

            if (!AnimatedTileIndexes.TryGetValue(tile, out var indexes)) AnimatedTileIndexes[tile] = new List<int> { index };
            else if (!indexes.Contains(index)) indexes.Add(index);

            animatedTile.LastAccessed = _now();

            if (flips.Count == 0 && animatedTile.IsFlipped) return;

            DrawTile(animatedTile.IsHighTile ? TileLayer.Fore : TileLayer.Back, animatedTile.Id + 1, index, flips, draws);
        }

        void AddAnimatedTile(int tileId, int index)
        {
            var isDynamicallyAnimated = index != -1;
            var identifier = isDynamicallyAnimated ? $"{tileId}-{index}" : tileId.ToString();

            if (isDynamicallyAnimated && _animatedDynamicTiles.Contains(identifier)) return;

            AnimatedTiles[identifier] = new TileAnimation(
                tileId,
                index,
                _map.Info.Animations[tileId],
                false,
                _map.IsHighTile(tileId),
                isDynamicallyAnimated ? _map.DynamicAnimatedTiles[index] : null,
                _now());

            if (isDynamicallyAnimated) _animatedDynamicTiles.Add(identifier);

            ResetAnimatedTiles();
        }

        /// <summary>canvas.ts resetAnimatedTiles: restart every animation in step.</summary>
        public void ResetAnimatedTiles()
        {
            var now = _now();
            foreach (var tile in AnimatedTiles.Values)
            {
                tile.LastTime = now - tile.Duration + 24;
                tile.AnimationIndex = 0;
            }
        }

        /// <summary>
        /// updater.ts updateAnimatedTiles: puts back the data of finished one-time animations, drops unused ones,
        /// advances the rest. Returns the cells whose data changed.
        /// </summary>
        public List<int> UpdateAnimatedTiles(double time)
        {
            var changed = new List<int>();
            foreach (var identifier in new List<string>(AnimatedTiles.Keys))
            {
                var tile = AnimatedTiles[identifier];

                if (tile.Expired)
                {
                    _map.Data[tile.Index] = tile.PostAnimationData;
                    changed.Add(tile.Index);
                }

                if (tile.Unused || tile.Expired)
                {
                    AnimatedTiles.Remove(identifier);
                    if (int.TryParse(identifier, out var key)) AnimatedTileIndexes.Remove(key);
                    continue;
                }

                tile.Animate(time);
            }
            return changed;
        }

        void DrawTile(TileLayer layer, int tileId, int index, List<TileFlip> flips, List<TileDraw> draws)
        {
            var tileset = _map.GetTilesetFromId(tileId);
            if (tileset == null) return;

            var setWidth = _tilesetWidths[tileset.FirstGid] / _tileSize;
            var relativeTileId = tileId - tileset.FirstGid;
            var sx = GetX(relativeTileId + 1, setWidth) * _tileSize;
            var sy = relativeTileId / setWidth * _tileSize;

            // The cell's corner as drawTile computes it (actualTileSize = tileSize here; zoom is the camera's job).
            var cellDx = GetX(index + 1, _map.Width) * _tileSize + 1;
            var cellDy = index / _map.Width * _tileSize + 1;

            draws.Add(Transform(layer, tileset, sx, sy, cellDx, cellDy, flips));
        }

        /// <summary>renderer.ts getX.</summary>
        static int GetX(int index, int width)
        {
            if (index == 0) return 0;
            return index % width == 0 ? width - 1 : index % width - 1;
        }

        /// <summary>The flip sequence of canvas.ts drawImage, applied to a matrix like the 2D context would.</summary>
        TileDraw Transform(TileLayer layer, TilesetRange tileset, int sx, int sy, int cellDx, int cellDy, List<TileFlip> flips)
        {
            var size = _tileSize;
            var m = Affine.Identity;
            int dx = 0, dy = 0;

            if (flips.Count > 0)
            {
                dx = cellDx;
                dy = cellDy;
                var tempX = dx;

                // The diagonal case appends to the list while it is iterated, like the original.
                for (var i = 0; i < flips.Count; i++)
                    switch (flips[i])
                    {
                        case TileFlip.Horizontal:
                            dx = -dx - size;
                            m = m.Multiply(Affine.Scale(-1, 1));
                            break;
                        case TileFlip.Vertical:
                            dy = -dy - size;
                            m = m.Multiply(Affine.Scale(1, -1));
                            break;
                        case TileFlip.Diagonal:
                            m = m.Multiply(Affine.Rotate90());
                            m = m.Multiply(Affine.Translate(0, -size));
                            dx = dy;
                            dy = -tempX;
                            flips.Add(i + 1 < flips.Count && flips[i + 1] == TileFlip.Horizontal ? TileFlip.Horizontal : TileFlip.Vertical);
                            break;
                    }
            }

            // drawImage(image, ..., dx || cell.dx, dy || cell.dy, ...)
            var destX = dx != 0 ? dx : cellDx;
            var destY = dy != 0 ? dy : cellDy;

            var origin = m.Apply(destX, destY);
            var xAxis = m.Apply(destX + size, destY);
            var yAxis = m.Apply(destX, destY + size);

            return new TileDraw
            {
                Layer = layer,
                Tileset = tileset,
                Sx = sx,
                Sy = sy,
                Ux = (xAxis.x - origin.x) / size,
                Uy = (xAxis.y - origin.y) / size,
                Vx = (yAxis.x - origin.x) / size,
                Vy = (yAxis.y - origin.y) / size,
                OffsetX = origin.x - cellDx,
                OffsetY = origin.y - cellDy
            };
        }

        /// <summary>An integer 2D affine transform in canvas setTransform order (a b c d e f).</summary>
        readonly struct Affine
        {
            readonly int _a, _b, _c, _d, _e, _f;

            Affine(int a, int b, int c, int d, int e, int f)
            {
                _a = a; _b = b; _c = c; _d = d; _e = e; _f = f;
            }

            public static Affine Identity => new Affine(1, 0, 0, 1, 0, 0);
            public static Affine Scale(int x, int y) => new Affine(x, 0, 0, y, 0, 0);
            public static Affine Translate(int x, int y) => new Affine(1, 0, 0, 1, x, y);
            /// <summary>rotate(π/2): cos 0, sin 1.</summary>
            public static Affine Rotate90() => new Affine(0, 1, -1, 0, 0, 0);

            public Affine Multiply(Affine n) => new Affine(
                _a * n._a + _c * n._b,
                _b * n._a + _d * n._b,
                _a * n._c + _c * n._d,
                _b * n._c + _d * n._d,
                _a * n._e + _c * n._f + _e,
                _b * n._e + _d * n._f + _f);

            public (int x, int y) Apply(int x, int y) => (_a * x + _c * y + _e, _b * x + _d * y + _f);
        }
    }
}
