using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace PrintStreamer.Streamers
{
    internal sealed record OverlayLayoutResult(
        string DrawboxX,
        string DrawboxY,
        string TextX,
        string TextY,
        bool HasCustomX,
        bool HasCustomY,
        int ApproxTextHeight,
        string RawX,
        string RawY);

    internal static class OverlayLayout
    {
        private const int BaselinePadding = 10;

        public static OverlayLayoutResult Calculate(IConfiguration config, string textFilePath, int fontSize, int boxHeight)
        {
            var rawX = config.GetValue<string>("Overlay:X");
            var rawY = config.GetValue<string>("Overlay:Y");
            var hasCustomX = !string.IsNullOrWhiteSpace(rawX);
            var hasCustomY = !string.IsNullOrWhiteSpace(rawY);
            var drawboxX = (rawX ?? "0").Replace(" ", string.Empty);
            var drawboxY = (rawY ?? "h").Replace(" ", string.Empty);
            var lineCount = 1;
            try
            {
                if (File.Exists(textFilePath))
                {
                    var initialText = File.ReadAllText(textFilePath);
                    if (!string.IsNullOrEmpty(initialText))
                    {
                        lineCount = initialText.Split('\n').Length;
                    }
                }
            }
            catch
            {
                // Ignore overlay reading errors; default line count is fine.
            }

            var approxTextHeight = Math.Max(fontSize, 12) * Math.Max(1, lineCount);

            return new OverlayLayoutResult(drawboxX, drawboxY, drawboxX, drawboxY, hasCustomX, hasCustomY, approxTextHeight, rawX ?? string.Empty, rawY ?? string.Empty);
        }
    }
}
