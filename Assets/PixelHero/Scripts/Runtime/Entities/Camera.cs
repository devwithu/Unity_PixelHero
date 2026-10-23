using System;

namespace PixelHero.Entities
{
    /// <summary>
    /// client/src/renderer/camera.ts without the DOM: the screen size (the #border element) is given in pixels.
    /// x/y are world pixels of the view's top-left (fractional when the screen is not a whole number of tiles);
    /// gridX/gridY/gridWidth/gridHeight are the tiles the renderers walk.
    /// </summary>
    public sealed class Camera
    {
        public const double MaximumZoom = 8, DefaultZoom = 4;
        public const int MaxGridWidth = 64, MaxGridHeight = 32;

        readonly int _width;
        readonly int _height;
        readonly int _tileSize;

        public double ScreenWidth;
        public double ScreenHeight;

        public double X;
        public double Y;
        public int GridX;
        public int GridY;
        public int GridWidth;
        public int GridHeight;
        double _borderX;
        double _borderY;
        public double BorderWidth;
        public double BorderHeight;
        public double ZoomFactor = DefaultZoom;
        bool _centered = true;
        public bool LockX;
        public bool LockY;
        public double MaxZoom = MaximumZoom;
        public double MinZoom = 2.6;

        public Action ZoomCallback;

        public Camera(int mapWidth, int mapHeight, int tileSize, double screenWidth, double screenHeight)
        {
            _width = mapWidth;
            _height = mapHeight;
            _tileSize = tileSize;
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            Update();
        }

        public void Update()
        {
            BorderWidth = ScreenWidth / _tileSize / ZoomFactor;
            BorderHeight = ScreenHeight / _tileSize / ZoomFactor;
            GridWidth = (int)BorderWidth + 1;
            GridHeight = (int)BorderHeight + 1;

            if (GridWidth > MaxGridWidth) GridWidth = MaxGridWidth;
            if (GridHeight > MaxGridHeight) GridHeight = MaxGridHeight;

            _borderX = (_width - GridWidth) * _tileSize;
            _borderY = (_height - GridHeight) * _tileSize;
        }

        void SetGridPosition(int gridX, int gridY)
        {
            GridX = gridX;
            GridY = gridY;
            X = gridX * _tileSize;
            Y = gridY * _tileSize;
        }

        public void SetZoom(double zoom = DefaultZoom)
        {
            ZoomFactor = zoom;
            if (double.IsNaN(ZoomFactor)) ZoomFactor = DefaultZoom;
            if (ZoomFactor > MaxZoom) ZoomFactor = MaxZoom;
            if (ZoomFactor < MinZoom) ZoomFactor = MinZoom;
            ZoomCallback?.Invoke();
        }

        /// <summary>camera.ts zoom: <c>parseFloat((zoomFactor + amount).toFixed(1))</c>.</summary>
        public void Zoom(double amount = 0) =>
            SetZoom(PixelHero.Js.JsNumber.ParseJsonNumber(PixelHero.Js.JsNumber.ToFixed(ZoomFactor + amount, 1)));

        public void Clip() => SetGridPosition((int)JsMath.Round(X / _tileSize), (int)JsMath.Round(Y / _tileSize));

        public void Center() => _centered = true;

        public void Decenter()
        {
            _centered = false;
            Clip();
        }

        public bool IsCentered() => _centered;

        public void CentreOn(Entity character)
        {
            var width = BorderWidth / 2;
            var height = BorderHeight / 2;
            var nextX = character.X - width * _tileSize;
            var nextY = character.Y - height * _tileSize;

            if (nextX >= 0 && nextX <= _borderX && !LockX)
            {
                X = nextX;
                GridX = (int)JsMath.Round(character.X / _tileSize - (int)width);
            }
            else OffsetX(nextX);

            if (nextY >= 0 && nextY <= _borderY && !LockY)
            {
                Y = nextY;
                GridY = (int)JsMath.Round(character.Y / _tileSize - (int)height);
            }
            else OffsetY(nextY);
        }

        void OffsetX(double nextX)
        {
            if (nextX <= _tileSize)
            {
                X = 0;
                GridX = 0;
            }
            else if (nextX >= _borderX)
            {
                X = _borderX;
                GridX = (int)JsMath.Round(_borderX / _tileSize);
            }
        }

        void OffsetY(double nextY)
        {
            if (nextY <= _tileSize)
            {
                Y = 0;
                GridY = 0;
            }
            else if (nextY >= _borderY)
            {
                Y = _borderY;
                GridY = (int)JsMath.Round(_borderY / _tileSize);
            }
        }

        /// <summary>camera.ts forEachVisiblePosition: rows from gridY - offset, then columns, inside the map.</summary>
        public void ForEachVisiblePosition(Action<int, int> callback, int offset = 2, int? offsetRight = null)
        {
            var right = offsetRight ?? offset;
            for (var y = GridY - offset; y < GridY + GridHeight + right; y++)
            for (var x = GridX - offset; x < GridX + GridWidth + right; x++)
            {
                if (x < 0 || y < 0 || x >= _width || y >= _height) continue;
                callback(x, y);
            }
        }
    }
}
