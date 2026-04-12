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
        int bandCount = bandEnergies.Length;
        var tonality = new float[bandCount];

        for (int band = 0; band < bandCount; band++)
        {
            float spectralTonality = ComputeBandSpectralTonality(
                bandPeaks[band],
                bandEnergies[band],
                width: 1);

            float temporalTonality = 0.0f;
            if (previousMdct != null && previousMdct.Length == currentMdct.Length)
            {
                temporalTonality = EstimateTemporalBandTonality(currentMdct, previousMdct, band, bandCount);
            }

            float fallback = EstimateTonalityFallback(band, bandEnergies, bandPeaks);
            tonality[band] = (float)Math.Clamp(
                (0.48f * spectralTonality)
                + (0.32f * temporalTonality)
                + (0.20f * fallback),
                0.0f,
                1.0f);
        }

        return tonality;
    }

    private float EstimateTonalityFallback(int band, float[] bandEnergies, float[] bandPeaks)
    {
        float local = ComputeBandSpectralTonality(bandPeaks[band], bandEnergies[band], 1);
        float previous = band > 0 ? ComputeBandSpectralTonality(bandPeaks[band - 1], bandEnergies[band - 1], 1) : local;
        float next = band + 1 < bandEnergies.Length ? ComputeBandSpectralTonality(bandPeaks[band + 1], bandEnergies[band + 1], 1) : local;
        return (float)Math.Clamp((local * 0.60f) + (previous * 0.20f) + (next * 0.20f), 0.0f, 1.0f);
    }

    private static float ComputeBandSpectralTonality(float peakEnergy, float bandEnergy, int width)
    {
        if (bandEnergy <= 1e-12f || width <= 0)
        {
            return 0.0f;
        }

        float normalizedPeak = (float)Math.Clamp((peakEnergy * peakEnergy) / bandEnergy, 0.0f, 1.0f);
        float widthFactor = (float)Math.Clamp(1.0f / MathF.Sqrt(width), 0.2f, 1.0f);
        return (float)Math.Clamp(normalizedPeak * widthFactor, 0.0f, 1.0f);
    }

    private static float EstimateTemporalBandTonality(float[] currentMdct, float[] previousMdct, int band, int bandCount)
    {
        int binsPerBand = Math.Max(1, currentMdct.Length / bandCount);
        int start = Math.Min(currentMdct.Length - 1, band * binsPerBand);
        int end = band == bandCount - 1 ? currentMdct.Length : Math.Min(currentMdct.Length, start + binsPerBand);

        double dot = 0.0;
        double energyA = 0.0;
        double energyB = 0.0;

        for (int i = start; i < end; i++)
        {
            double a = Math.Abs(currentMdct[i]);
            double b = Math.Abs(previousMdct[i]);
            dot += a * b;
            energyA += a * a;
            energyB += b * b;
        }

        if (energyA <= 1e-12 || energyB <= 1e-12)
        {
            return 0.0f;
        }

        double similarity = dot / Math.Sqrt(energyA * energyB);
        return (float)Math.Clamp((float)similarity, 0.0f, 1.0f);
    }
}
