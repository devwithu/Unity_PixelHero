using System;
using System.Linq;
using PixelHero.Common;
using PixelHero.Editor;
using PixelHero.Entities;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;

namespace PixelHero.Tests
{
    public sealed class EntityPacketsTests
    {
        /// <summary>
        /// GameClient forwards only <see cref="EntityPackets.Handled"/> to <see cref="EntityPackets.Handle"/>, so every
        /// packet Handle understands must be listed (Rank, Countdown and Overlay once were not, and were dropped in
        /// game while the replays, which call Handle directly, passed).
        /// </summary>
        [Test]
        public void HandledListsEveryPacketHandleUnderstands()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            var world = new World(new ManualClock { Now = 0 }, map, SpriteDefinitions.ParseAll(catalog.SpritesJson.text, map.Info.TileSize), _ => false);
            var packets = new EntityPackets(world);

            foreach (Packets id in Enum.GetValues(typeof(Packets)))
            {
                bool understood;
                try
                {
                    understood = packets.Handle(id, new object[] { null, null, null });
                }
                catch (Exception)
                {
                    // It got past the switch and choked on the empty arguments.
                    understood = true;
                }

                Assert.AreEqual(understood, EntityPackets.Handled.Contains(id), $"{id}");
            }
        }
    }
}
