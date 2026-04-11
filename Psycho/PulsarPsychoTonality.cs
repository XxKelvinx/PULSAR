using System;

namespace Pulsar.Psycho;

public partial class PulsarPsycho
{
    private float[] ComputeTonality(
        float[] currentMdct,
        float[]? previousMdct,
        float[] bandEnergies,
        float[] bandPeaks)
    {
        throw new NotImplementedException();
    }

    private float EstimateTonalityFallback(int band, float[] bandEnergies, float[] bandPeaks)
    {
        throw new NotImplementedException();
    }

    private static float ComputeBandSpectralTonality(float peakEnergy, float bandEnergy, int width)
    {
        throw new NotImplementedException();
    }
}
