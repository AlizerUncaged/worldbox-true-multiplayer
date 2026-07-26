using System;

namespace WorldboxMP.Client.Util
{
    /// <summary>
    /// Singleton utility for human-readable byte counts (used by the net HUD's
    /// received/sent counters). Formatting is capped at megabytes: totals larger
    /// than 1 GB still display in MB, per the HUD spec.
    /// </summary>
    public sealed class ByteSize
    {
        public static ByteSize Instance { get; } = new ByteSize();
        private ByteSize() { }

        // Source - https://stackoverflow.com/a/14488941
        // Posted by JLRishe, modified by community. See post 'Timeline' for change history
        // Retrieved 2026-07-26, License - CC BY-SA 4.0
        static readonly string[] SizeSuffixes =
                           { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        public string SizeSuffix(Int64 value, int decimalPlaces = 1)
        {
            if (decimalPlaces < 0) { throw new ArgumentOutOfRangeException("decimalPlaces"); }
            if (value < 0) { return "-" + SizeSuffix(-value, decimalPlaces); }
            if (value == 0) { return string.Format("{0:n" + decimalPlaces + "} bytes", 0); }

            // mag is 0 for bytes, 1 for KB, 2, for MB, etc.
            int mag = (int)Math.Log(value, 1024);

            // Cap at MB (mag 2): larger totals keep the MB suffix instead of moving to GB+.
            if (mag > 2) mag = 2;

            // 1L << (mag * 10) == 2 ^ (10 * mag)
            // [i.e. the number of bytes in the unit corresponding to mag]
            decimal adjustedSize = (decimal)value / (1L << (mag * 10));

            // make adjustment when the value is large enough that
            // it would round up to 1000 or more (skipped at the MB cap)
            if (mag < 2 && Math.Round(adjustedSize, decimalPlaces) >= 1000)
            {
                mag += 1;
                adjustedSize /= 1024;
            }

            return string.Format("{0:n" + decimalPlaces + "} {1}",
                adjustedSize,
                SizeSuffixes[mag]);
        }
    }
}
