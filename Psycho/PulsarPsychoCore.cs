using System;
using System.Collections.Generic;

namespace Pulsar.Psycho;

public partial class PulsarPsycho
{
    private readonly PulsarPsychoSettings _settings;

    public PulsarPsycho(PulsarPsychoSettings? settings = null)
    {
        _settings = settings ?? new PulsarPsychoSettings();
    }

    public IReadOnlyList<PulsarPsychoResult> AnalyzeSong(float[] input)
    {
        throw new NotImplementedException();
    }

    public PulsarPsychoSongAnalysis AnalyzeSongWithGlobalBudget(float[] input, int totalBits)
    {
        throw new NotImplementedException();
    }

    private List<PulsarPsychoResult> AnalyzeSongFrames(float[] input)
    {
        throw new NotImplementedException();
    }

    public (PulsarPsychoResult Result, float[] MdctCoefficients) AnalyzeFrame(float[] frame, float[]? previousMdct)
    {
        throw new NotImplementedException();
    }
}
