using PixelHero.Server.Controllers;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>
    /// game/map/grids.ts. The original builds rows -1..height-1 and columns -1..width-1 (negative indices are plain
    /// object properties on the arrays); any other coordinate reads undefined.
    /// </summary>
    public sealed class Grids
    {
        readonly JsObject<Entity>[,] entityGrid;
        readonly int width;
        readonly int height;

        public Grids(double width, double height)
        {
            this.width = (int)width;
            this.height = (int)height;

            entityGrid = new JsObject<Entity>[this.height + 1, this.width + 1];

            for (var y = -1; y < height; y++)
                for (var x = -1; x < width; x++)
                    entityGrid[y + 1, x + 1] = new JsObject<Entity>();
        }

        /// <summary>entityGrid[y][x], or null (undefined) for anything outside the grid.</summary>
        JsObject<Entity> Cell(double x, double y)
        {
            if (x % 1 != 0 || y % 1 != 0 || double.IsNaN(x) || double.IsNaN(y)) return null;
            if (x < -1 || y < -1 || x >= width || y >= height) return null;

            return entityGrid[(int)y + 1, (int)x + 1];
        }

        /// <summary>entityGrid[y][x] where the row itself must exist (reading from an undefined row throws).</summary>
        JsObject<Entity> CellOrThrow(double x, double y, string reading)
        {
            var rowExists = y % 1 == 0 && y >= -1 && y < height;
            if (!rowExists) throw new JsTypeError($"Cannot read properties of undefined (reading '{reading}')");

            return Cell(x, y);
        }

        public Effect GetEffectAt(double x, double y)
        {
            Entity entity = null;

            // Look through all the entities at the specified coordinate and return an effect entity if found.
            ForEachEntityAt(x, y, e =>
            {
                // Ignore non-effect entities
                if (!e.IsEffect()) return;

                entity = e;
            });

            return entity as Effect;
        }

        public void UpdateEntity(Entity entity)
        {
            if (entity.x == entity.oldX && entity.y == entity.oldY) return;

            RemoveFromEntityGrid(entity, entity.oldX, entity.oldY);
            AddToEntityGrid(entity);
        }

        public void AddToEntityGrid(Entity entity)
        {
            var cell = CellOrThrow(entity.x, entity.y, JsString.Of(entity.x)) ??
                       throw new JsTypeError($"Cannot set properties of undefined (setting '{entity.instance}')");

            cell.Set(entity.instance, entity);
        }

        public void RemoveFromEntityGrid(Entity entity) => RemoveFromEntityGrid(entity, entity.x, entity.y);

        public void RemoveFromEntityGrid(Entity entity, double x, double y)
        {
            var cell = CellOrThrow(x, y, JsString.Of(x)) ??
                       throw new JsTypeError($"Cannot convert undefined or null to object");

            cell.Delete(entity.instance);
        }

        public bool HasEntityAt(double x, double y) => Cell(x, y) != null;

        public void ForEachEntityAt(double x, double y, Action<Entity> callback)
        {
            var cell = CellOrThrow(x, y, JsString.Of(x));

            // for...in over undefined does nothing.
            if (cell == null) return;

            foreach (var pair in cell) callback(pair.Value);
        }

        public void ForEachEntityNear(double x, double y, Action<Entity> callback, double radius = 1)
        {
            for (var i = x - radius; i <= x + radius; i++)
                for (var j = y - radius; j <= y + radius; j++)
                    if (Cell(i, j) != null)
                        ForEachEntityAt(i, j, callback);
        }
    }
}
