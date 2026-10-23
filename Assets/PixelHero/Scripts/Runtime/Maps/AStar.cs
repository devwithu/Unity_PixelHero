using System;
using System.Collections.Generic;

namespace PixelHero.Maps
{
    /// <summary>
    /// The client's path finder (client/src/lib/astar.ts in its DEFAULT mode, used through utils/pathfinder.ts):
    /// 4-way moves, Manhattan distance, only cells whose grid value is 0 are walkable. Quirks kept on purpose:
    /// the search gives up (empty path) once the open list holds more than 100 nodes; the start cell is never
    /// marked as seen; the first node with the lowest f wins ties.
    /// </summary>
    public static class AStar
    {
        sealed class Node
        {
            public int X;
            public int Y;
            public double F;
            public double G;
            public int V;
            public Node P;
        }

        /// <param name="grid">Row-major grid (grid[y * cols + x]); 0 = walkable.</param>
        /// <returns>[x, y] steps from start to end inclusive, or an empty list.</returns>
        public static List<int[]> Find(Func<int, int, int> grid, int cols, int rows, int startX, int startY, int endX, int endY)
        {
            var limit = (double)cols * rows;
            var seen = new HashSet<int>();
            var result = new List<int[]>();
            var open = new List<Node> { new Node { X = startX, Y = startY, F = 0, G = 0, V = startX + startY * cols } };
            var length = 1;
            var endV = endX + endY * cols;

            do
            {
                if (length > 100) return new List<int[]>();

                var max = limit;
                var min = 0;
                for (var i = 0; i < length; ++i)
                {
                    var openF = open[i].F;
                    if (openF < max)
                    {
                        max = openF;
                        min = i;
                    }
                }

                var current = open[min];
                open.RemoveAt(min);

                if (current.V == endV)
                {
                    length = 0;
                    for (var node = current; node != null; node = node.P) result.Add(new[] { node.X, node.Y });
                    result.Reverse();
                }
                else
                {
                    --length;
                    foreach (var adj in Successors(current.X, current.Y, grid, rows, cols))
                    {
                        adj.P = current;
                        adj.V = adj.X + adj.Y * cols;
                        if (seen.Contains(adj.V)) continue;

                        adj.G = current.G + Manhattan(adj.X, adj.Y, current.X, current.Y);
                        adj.F = adj.G + Manhattan(adj.X, adj.Y, endX, endY);
                        // open[length++] = adj: the slot after the live entries (JS arrays grow as needed).
                        if (length < open.Count) open[length] = adj;
                        else open.Add(adj);
                        length++;
                        seen.Add(adj.V);
                    }
                }
            } while (length > 0);

            return result;
        }

        static double Manhattan(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) + Math.Abs(y1 - y2);

        /// <summary>astar.ts successors with nothingToDo: north, east, south, west.</summary>
        static List<Node> Successors(int x, int y, Func<int, int, int> grid, int rows, int cols)
        {
            int n = y - 1, s = y + 1, e = x + 1, w = x - 1;
            var result = new List<Node>(4);
            if (n > -1 && grid(x, n) == 0) result.Add(new Node { X = x, Y = n });
            if (e < cols && grid(e, y) == 0) result.Add(new Node { X = e, Y = y });
            if (s < rows && grid(x, s) == 0) result.Add(new Node { X = x, Y = s });
            if (w > -1 && grid(w, y) == 0) result.Add(new Node { X = w, Y = y });
            return result;
        }
    }

    /// <summary>
    /// client utils/pathfinder.ts + game.ts findPath on a <see cref="ClientMap"/> grid. Ignored tiles are opened
    /// for the search and closed again (to 1, whatever they were, like the original — unless they were already 0).
    /// </summary>
    public sealed class PathFinder
    {
        public sealed class TileIgnore
        {
            public int X;
            public int Y;
            public bool Fake;
        }

        public List<int[]> Find(ClientMap map, int startX, int startY, int endX, int endY, List<TileIgnore> ignores = null)
        {
            ignores ??= new List<TileIgnore>();
            HandleIgnore(map, ignores, true);
            var path = AStar.Find((x, y) => map.Grid[y * map.Width + x], map.Width, map.Height, startX, startY, endX, endY);
            HandleIgnore(map, ignores, false);
            return path;
        }

        static void HandleIgnore(ClientMap map, List<TileIgnore> ignores, bool ignored)
        {
            foreach (var ignore in ignores)
            {
                if (ignore.X < 0 || ignore.Y < 0) continue;
                if (ignore.Fake) continue;
                var index = ignore.Y * map.Width + ignore.X;
                if (map.Grid[index] == 0) ignore.Fake = true;
                map.Grid[index] = (byte)(ignored ? 0 : 1);
            }
        }
    }
}
