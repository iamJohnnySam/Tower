using System.Globalization;

namespace Tower.Core.Tuya;

/// <summary>Tuya v3.3+ colour DPS codec. Hex = HHHHSSSSVVVV (H 0-360, S 0-1000, V 0-1000).</summary>
public static class TuyaColor
{
    public static string HsvHexFromRgb(string rgbHex)
    {
        var (r, g, b) = ParseRgb(rgbHex);
        var (h, s, v) = RgbToHsv(r, g, b);
        return $"{(int)Math.Round(h):x4}{(int)Math.Round(s * 1000):x4}{(int)Math.Round(v * 1000):x4}";
    }

    public static string RgbHexFromHsvHex(string tuyaHex)
    {
        if (string.IsNullOrEmpty(tuyaHex) || tuyaHex.Length < 12) return "#ffffff";
        int h = int.Parse(tuyaHex.Substring(0, 4), NumberStyles.HexNumber);
        int s = int.Parse(tuyaHex.Substring(4, 4), NumberStyles.HexNumber);
        int v = int.Parse(tuyaHex.Substring(8, 4), NumberStyles.HexNumber);
        var (r, g, b) = HsvToRgb(h, s / 1000.0, v / 1000.0);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    static (int r, int g, int b) ParseRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (255, 255, 255);
        return (Convert.ToInt32(hex.Substring(0, 2), 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    static (double h, double s, double v) RgbToHsv(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min, h = 0;
        if (d != 0)
        {
            if (max == rf) h = 60 * (((gf - bf) / d) % 6);
            else if (max == gf) h = 60 * (((bf - rf) / d) + 2);
            else h = 60 * (((rf - gf) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
