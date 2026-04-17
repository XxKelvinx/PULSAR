using System;

public static class PulsarQualityProfile
{
    public const int MinQuality = 1;
    public const int MaxQuality = 3;
    public const int DefaultQuality = 3;

    private static readonly int[] NominalKbpsByQuality =
    [
        60, 138, 256,
    ];

    // Positive values relax masking thresholds, negative values spend more bits.
    private static readonly float[] ThresholdRedValDbByQuality =
    [
        4.0f, 0.0f, -4.0f,
    ];

    private static readonly double[] PulseScaleByQuality =
    [
        24.0, 48.0, 64.0,
    ];

    // Pulses per bit of per-frame budget. CELT range coder spends ~3-5 bits per pulse
    // including sign and position, so the right order of magnitude is 0.2..0.4 pulses/bit.
    // Calibrated per quality so that V1≈138, V2≈256, V3≈355 kbps on real content.
    private static readonly double[] BitBudgetScaleByQuality =
    [
        // NOTE: below ~0.02 the per-band side-info floor (12-bit EnergyQ + 12-bit ScaleQ per
        // band × ~50 bands × 170 fps ≈ 200 kbps) dominates, so further reductions don't drop
        // real bitrate. Reducing that floor (coarser/delta-coded energy) is the next target.
        0.020, 0.035, 0.055,
    ];

    public static int ClampQuality(int quality) => Math.Clamp(quality, MinQuality, MaxQuality);

    public static bool IsValidQuality(int quality) => quality >= MinQuality && quality <= MaxQuality;

    public static int GetNominalKbps(int quality) => NominalKbpsByQuality[ClampQuality(quality) - MinQuality];

    public static float GetThresholdRedValDb(int quality) => ThresholdRedValDbByQuality[ClampQuality(quality) - MinQuality];

    public static double GetPulseScale(int quality) => PulseScaleByQuality[ClampQuality(quality) - MinQuality];

    public static double GetBitBudgetScale(int quality) => BitBudgetScaleByQuality[ClampQuality(quality) - MinQuality];

    // Bandwidth cutoff per quality: V1=17kHz, V2=18kHz, V3=19kHz
    private static readonly ushort[] BandwidthLimitByQuality = [17000, 18000, 19000];
    public static ushort GetBandwidthLimitHz(int quality) => BandwidthLimitByQuality[ClampQuality(quality) - MinQuality];

    public static int ResolveQualityFromTargetKbps(int targetKbps)
    {
        int bestQuality = DefaultQuality;
        int bestDistance = int.MaxValue;

        for (int quality = MinQuality; quality <= MaxQuality; quality++)
        {
            int distance = Math.Abs(GetNominalKbps(quality) - targetKbps);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestQuality = quality;
            }
        }

        return bestQuality;
    }

    public static string DescribeScale()
    {
        var entries = new string[NominalKbpsByQuality.Length];
        for (int quality = MinQuality; quality <= MaxQuality; quality++)
        {
            entries[quality - MinQuality] = $"V{quality}~{GetNominalKbps(quality)}kbps";
        }

        return string.Join(", ", entries);
    }
}
