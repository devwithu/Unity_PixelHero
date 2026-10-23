using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PixelHero.Tests
{
    public class PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            var start = Time.frameCount;
            yield return null;
            yield return null;

            Assert.Greater(Time.frameCount, start);
            Assert.AreEqual(ColorSpace.Gamma, QualitySettings.activeColorSpace);
        }
    }
}
