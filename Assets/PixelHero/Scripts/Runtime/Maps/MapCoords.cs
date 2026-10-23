using UnityEngine;

namespace PixelHero.Maps
{
    /// <summary>
    /// The one place where the original's coordinates (tiles, top-left origin, y down) become Unity's (1 unit per
    /// tile, y up). Game logic and the protocol keep the original coordinates everywhere else.
    /// </summary>
    public static class MapCoords
    {
        /// <summary>The Unity tilemap cell of an original tile.</summary>
        public static Vector3Int ToCell(int x, int y) => new Vector3Int(x, -y - 1, 0);

        /// <summary>The Unity world position of an original tile's top-left corner.</summary>
        public static Vector3 TileCorner(float x, float y) => new Vector3(x, -y, 0);

        /// <summary>The Unity world position of an original tile's centre.</summary>
        public static Vector3 TileCenter(float x, float y) => new Vector3(x + 0.5f, -y - 0.5f, 0);

        /// <summary>
        /// The cell transform for a tile drawn with its +x axis along (ux, uy) and +y axis along (vx, vy) on the
        /// canvas (y down). Unity's y points up, so both axes are mirrored vertically, and the image's +y (down)
        /// becomes the sprite's -y.
        /// </summary>
        public static Matrix4x4 TileTransform(int ux, int uy, int vx, int vy)
        {
            var matrix = Matrix4x4.identity;
            matrix.SetColumn(0, new Vector4(ux, -uy, 0, 0));
            matrix.SetColumn(1, new Vector4(-vx, vy, 0, 0));
            return matrix;
        }
    }
}
