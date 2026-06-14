namespace Tower;

/// <summary>Human-readable formatting helpers shared across UI components.</summary>
public static class Fmt
{
    static readonly string[] _sizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    static readonly string[] _rateUnits = ["B/s", "KB/s", "MB/s", "GB/s"];

    /// <summary>Format byte count as "1.2 GB", "512 MB", etc.</summary>
    public static string Bytes(ulong bytes)
    {
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < _sizeUnits.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {_sizeUnits[i]}" : $"{v:0.0} {_sizeUnits[i]}";
    }

    /// <inheritdoc cref="Bytes(ulong)"/>
    public static string Bytes(long bytes) => bytes < 0 ? "—" : Bytes((ulong)bytes);

    /// <summary>Format byte-per-second rate as "1.2 MB/s", etc.</summary>
    public static string Rate(double bytesPerSec)
    {
        double v = bytesPerSec;
        int i = 0;
        while (v >= 1024 && i < _rateUnits.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {_rateUnits[i]}" : $"{v:0.0} {_rateUnits[i]}";
    }

    /// <summary>Format a percentage value like "42.3%". Returns "—" for NaN/Infinity.</summary>
    public static string Pct(double pct, int decimals = 1)
    {
        if (double.IsNaN(pct) || double.IsInfinity(pct)) return "—";
        return pct.ToString($"0.{new string('0', decimals)}") + "%";
    }

    /// <summary>Format a percentage value like "42%" (no decimals).</summary>
    public static string PctInt(double pct)
    {
        if (double.IsNaN(pct) || double.IsInfinity(pct)) return "—";
        return $"{(int)Math.Round(pct)}%";
    }

    /// <summary>Format frequency in MHz as "1200 MHz" or "3.6 GHz".</summary>
    public static string Freq(double mhz)
    {
        if (mhz >= 1000) return $"{mhz / 1000:0.0} GHz";
        return $"{mhz:0} MHz";
    }
}
