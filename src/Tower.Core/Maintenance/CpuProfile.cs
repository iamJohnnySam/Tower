namespace Tower.Core.Maintenance;

public static class CpuProfile
{
    /// <summary>
    /// Returns the 0-167 slot index for the given day-of-week and hour.
    /// Monday=0 .. Sunday=6  (mirrors Python's weekday()).
    /// </summary>
    public static int SlotFor(DayOfWeek dow, int hour)
    {
        // .NET DayOfWeek: Sunday=0 … Saturday=6
        // Python weekday():  Monday=0 … Sunday=6
        int d = ((int)dow + 6) % 7;  // Mon→0, Tue→1 … Sun→6
        return d * 24 + hour;
    }

    /// <summary>
    /// Returns the hour (0-23) with the lowest average CPU across all days
    /// that have ≥10 samples for that slot.  Returns 3 (3 AM) if fewer than
    /// 12 valid hours exist (not enough history yet).
    /// </summary>
    public static int BestWindow(double[] cpu, int[] samples)
    {
        var avgs = new List<(int hr, double avg)>();
        for (int hr = 0; hr < 24; hr++)
        {
            var vals = new List<double>();
            for (int d = 0; d < 7; d++)
            {
                int i = d * 24 + hr;
                if (samples[i] >= 10)
                    vals.Add(cpu[i]);
            }
            if (vals.Count > 0)
                avgs.Add((hr, vals.Average()));
        }

        if (avgs.Count < 12)
            return 3;

        return avgs.OrderBy(x => x.avg).First().hr;
    }
}
