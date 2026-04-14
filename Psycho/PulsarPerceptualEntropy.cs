using System;

namespace Pulsar.Psycho;

public static class PulsarPerceptualEntropy
{
    private static readonly float[] RegcoefShort = new float[]
    {
        11.8f, 13.6f, 17.2f, 32.0f, 46.5f, 51.3f, 57.5f, 67.1f,
        71.5f, 84.6f, 97.6f, 130.0f
    };

    private static readonly float[] RegcoefLong = new float[]
    {
        6.8f, 5.8f, 5.8f, 6.4f, 6.5f, 9.9f, 12.1f, 14.4f,
        15.0f, 18.9f, 21.6f, 26.9f, 34.2f, 40.2f, 46.8f,
        56.5f, 60.7f, 73.9f, 85.7f, 93.4f, 126.1f
    };

    /// <summary>
    /// Calculates perceptual entropy for a single frame using band energies
    /// and masking thresholds.
    ///
    /// This implementation mirrors the LAME PE math from libmp3lame/psymodel.c,
    /// where each band is weighted by a tuned coefficient and the entropy is
    /// proportional to the logarithmic energy ratio.
    /// </summary>
    /// <param name="bandEnergiesDb">Spectral band energies in decibels.</param>
    /// <param name="maskingThresholdDb">Masking threshold for each band in decibels.</param>
    /// <returns>Total frame perceptual entropy.</returns>
    public static float CalculateFramePe(float[] bandEnergiesDb, float[] maskingThresholdDb)
    {
        if (bandEnergiesDb == null)
        {
            throw new ArgumentNullException(nameof(bandEnergiesDb));
        }

        if (maskingThresholdDb == null)
        {
            throw new ArgumentNullException(nameof(maskingThresholdDb));
        }

        if (bandEnergiesDb.Length != maskingThresholdDb.Length)
        {
            throw new ArgumentException("Energy and threshold arrays must have the same length.");
        }

        float totalPe = 0.0f;
        float[] coefficients = SelectCoefficients(bandEnergiesDb.Length);

        for (int i = 0; i < bandEnergiesDb.Length; i++)
        {
            float smrDb = bandEnergiesDb[i] - maskingThresholdDb[i];
            if (smrDb <= 0.0f)
            {
                continue;
            }

            // LAME computes PE from the log ratio of energy to masking threshold.
            // Because SMR = 10 * log10(Energy / Threshold), the ratio in base 10 is:
            float logRatio = smrDb * 0.1f;

            // LAME clamps extremely high ratios to 10 decades (1e10) for stability.
            if (logRatio > 10.0f)
            {
                logRatio = 10.0f;
            }

            float weight = coefficients[Math.Min(i, coefficients.Length - 1)];
            totalPe += weight * logRatio;
        }

        return totalPe;
    }

    private static float[] SelectCoefficients(int bandCount)
    {
        return bandCount <= RegcoefShort.Length ? RegcoefShort : RegcoefLong;
    }
}
