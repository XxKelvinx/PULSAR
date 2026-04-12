using System;
using System.Collections.Generic;

namespace Pulsar.Psycho;

public sealed class PulsarPsychoResult
{
    public required int SampleRate { get; init; }
    public required int FftSize { get; init; }
    public required float[] SfbBandCenters { get; init; }
    public required int[] SfbBandOffsets { get; init; }
    public required int[] SfbBandWidths { get; init; }
    public required float[] SfbBandEnergiesDb { get; init; }
    public required float[] SfbBandPeaksDb { get; init; }
    public required float[] Tonality { get; init; }
    public required float[] MaskingThresholdDb { get; init; }
    public required float[] AbsoluteThresholdDb { get; init; }
    public required float[] SmrDb { get; init; }
    public float PerceptualEntropy { get; set; }
    public float TotalEnergyDb { get; set; }
    public float TransientScore { get; set; }
    public float OpusTransientEstimate { get; set; }
    public float AveragePositiveSmr { get; set; }
    public float PeakSmr { get; set; }
    public float MaskingPressure { get; set; }
    public float BassProtection { get; set; }
    public float TonalProtection { get; set; }
    public float LowBandStability { get; set; }
}

public sealed class PulsarPsychoSongAnalysis
{
    public required IReadOnlyList<PulsarPsychoResult> Frames { get; init; }
    public required float TotalPerceptualEntropy { get; init; }
    public required float AveragePerceptualEntropy { get; init; }
    public required float[] FrameEntropyWeights { get; init; }
    public required float[] FrameBudgetShares { get; init; }
}
