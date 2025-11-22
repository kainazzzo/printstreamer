using Microsoft.VisualStudio.TestTools.UnitTesting;
using PrintStreamer.Streamers;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class OverlayFilterUtilTests
    {
        [DataTestMethod]
        [DataRow(0.2, 0.2)]
        [DataRow(-1, 0.0)]
        [DataRow(1, 0.6)]
        public void ClampBannerFraction_Works(double input, double expected)
        {
            var res = OverlayFilterUtil.ClampBannerFraction(input);
            Assert.AreEqual(expected, res);
        }

        [TestMethod]
        public void Esc_EscapesSingleQuoteAndBackslash()
        {
            var s = @"C:\fonts\My'Font.ttf";
            var outp = OverlayFilterUtil.Esc(s);
            Assert.IsTrue(outp.Contains("\\\\"));
            Assert.IsTrue(outp.Contains("\\'"));
        }

        [TestMethod]
        public void BuildDrawbox_UsesBoxHeightWhenProvided()
        {
            var s = OverlayFilterUtil.BuildDrawbox("0", "h-20", 42, "black@0.4");
            Assert.IsTrue(s.Contains("h=42"));
        }

        [TestMethod]
        public void BuildDrawbox_UsesBannerFractionWhenNoHeight()
        {
            var s = OverlayFilterUtil.BuildDrawbox("0", "h-20", 0, "black@0.4");
            Assert.IsTrue(s.Contains("h=0"));
        }
    }
}
