using PixelHero.Audio;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>
    /// GainParam follows the Web Audio automation rules audio.ts relies on (values worked out from the spec's
    /// linearRampToValueAtTime and cancelScheduledValues).
    /// </summary>
    public sealed class GainParamTests
    {
        [Test]
        public void ARampWithNothingBeforeItStartsWhenScheduled()
        {
            var gain = new GainParam(0.01);
            gain.LinearRampToValueAtTime(0.51, 13, 10);
            Assert.AreEqual(0.01, gain.Value(9), 1e-12);
            Assert.AreEqual(0.01, gain.Value(10), 1e-12);
            Assert.AreEqual(0.26, gain.Value(11.5), 1e-12);
            Assert.AreEqual(0.51, gain.Value(13), 1e-12);
            Assert.AreEqual(0.51, gain.Value(100), 1e-12);
        }

        [Test]
        public void ALaterRampStartsAtThePreviousEvent()
        {
            // updateVolume's fade in, then (at t = 20) a sound ends: the 0.5 s ramp runs from the old event at 13.
            var gain = new GainParam(0.01);
            gain.LinearRampToValueAtTime(0.01, 10, 10);
            gain.LinearRampToValueAtTime(0.5, 13, 10);
            gain.LinearRampToValueAtTime(0.25, 20.5, 20);
            Assert.AreEqual(0.5 - 0.25 * 7 / 7.5, gain.Value(20), 1e-12);
            Assert.AreEqual(0.25, gain.Value(21), 1e-12);
        }

        [Test]
        public void CancellingARampFallsBackToTheEventBeforeIt()
        {
            var gain = new GainParam(0.01);
            gain.LinearRampToValueAtTime(0.5, 13, 10);
            gain.CancelScheduledValues(11, 11);
            Assert.AreEqual(0.01, gain.Value(11), 1e-12);
            Assert.AreEqual(0.01, gain.Value(50), 1e-12);
        }

        [Test]
        public void CancellingEverythingHoldsTheValueReached()
        {
            var gain = new GainParam(0.01);
            gain.LinearRampToValueAtTime(0.5, 13, 13);
            Assert.AreEqual(0.5, gain.Value(14), 1e-12);
            gain.CancelScheduledValues(13, 14);
            Assert.AreEqual(0.5, gain.Value(14), 1e-12);
            // The duck: an instant ramp from the held value.
            gain.LinearRampToValueAtTime(0.4375, 14, 14);
            Assert.AreEqual(0.4375, gain.Value(14), 1e-12);
        }
    }
}
