using System;

namespace Pulsar.Psycho;

public sealed class PulsarPsychoSettings
{
    public int SampleRate { get; init; } = 44100;
    public int FftSize { get; init; } = 2048;
    public int HopSize { get; init; } = 1024;
    public int PostMaskFrames { get; init; } = 4;
    public int PreMaskFrames { get; init; } = 1;
    public float TemporalPostMaskDecay { get; init; } = 1.5f;
    public float TemporalPreMaskDecay { get; init; } = 0.35f;
}
