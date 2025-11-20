using System.Globalization;

namespace PrintStreamer.Streamers
{
    internal static class OverlayFilterUtil
    {
        public static string Esc(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");

        public static double ClampBannerFraction(double f)
        {
            if (double.IsNaN(f)) return 0.2;
            if (f < 0) return 0;
            if (f > 0.6) return 0.6;
            return f;
        }

        public static string BuildDrawbox(string x, string y, int boxHeightConfig, string boxColor)
        {

            return $"drawbox=x={x}:y={y}:w=iw:h={boxHeightConfig}:color={boxColor}:t=fill";
        
        }
    }
}
