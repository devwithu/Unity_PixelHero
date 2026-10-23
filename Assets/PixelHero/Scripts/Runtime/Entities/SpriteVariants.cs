namespace PixelHero.Entities
{
    /// <summary>
    /// The images client/src/utils/util.ts generates from a sprite sheet, on RGBA bytes in canvas order (rows top
    /// to bottom). Quirks kept: the hurt tint skips a pixel when the NEXT pixel's red is 0 (it reads data[i + 4]),
    /// not when the pixel itself is transparent.
    /// </summary>
    public static class SpriteVariants
    {
        /// <summary>util.ts getHurtSprite: RGB (255, 75, 75), alpha untouched.</summary>
        public static byte[] Hurt(byte[] rgba)
        {
            var data = (byte[])rgba.Clone();
            for (var i = 0; i < data.Length; i += 4)
            {
                // `if (spriteData.data[i + 4] === 0) continue;` — past the end it is undefined, so not skipped.
                if (i + 4 < data.Length && data[i + 4] == 0) continue;

                data[i] = 255;
                data[i + 1] = data[i + 2] = 75;
            }
            return data;
        }

        /// <summary>
        /// util.ts getSilhouetteSprite: a transparent pixel (alpha ≤ 24, but not exactly 1) next to an opaque one
        /// (alpha &gt; 24, left/right/up/down) becomes RGBA (255, 255, 150, 150); the image's border is skipped.
        /// </summary>
        public static byte[] Silhouette(byte[] rgba, int width, int height)
        {
            var clone = rgba;
            var data = (byte[])rgba.Clone();

            for (var i = 0; i < clone.Length; i += 4)
            {
                if (clone[i + 3] > 24) continue;
                if (clone[i + 3] == 1) continue;

                var x = i / 4 % width;
                var y = i / 4 / width;
                if (x == 0 || x == width - 1 || y == 0 || y == height - 1) continue;

                var left = clone[i - 1];
                var right = clone[i + 7];
                var up = clone[i - width * 4 + 3];
                var down = clone[i + width * 4 + 3];

                if (left > 24 || right > 24 || up > 24 || down > 24)
                {
                    data[i] = data[i + 1] = 255;
                    data[i + 2] = data[i + 3] = 150;
                }
            }

            return data;
        }
    }
}
