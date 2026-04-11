using System;
using System.Collections.Generic;

namespace Pulsar.Psycho;

public partial class PulsarPsycho
{
    private const float TmnDb = 15.0f;
    private const float NmtDb = 5.5f;

    private const float MaskHighDb = 1.5f;
    private const float MaskLowDb = 3.0f;
    private const float MaskLowSprEnLongDb = 3.0f;
    private const float MaskHighSprEnLongDb = 2.0f;
    private const float MaskHighSprEnLongLowBrDb = 1.5f;
    private const float MaskLowSprEnShortDb = 2.0f;
    private const float MaskHighSprEnShortDb = 1.5f;

    private float[] ComputeMaskingThresholdDb(
        float[] bandEnergies,
        float[] tonality,
        float[] barkValues,
        int bitrate,
        bool isShortWindow)
    {
        int bandCount = Math.Min(Math.Min(bandEnergies.Length, tonality.Length), barkValues.Length);
        var spreadEnergies = new float[bandCount];
        Array.Copy(bandEnergies, spreadEnergies, bandCount);

        CalculateSpreadingFactors(barkValues, bitrate, isShortWindow,
            out var maskLowFactor, out var maskHighFactor,
            out _, out _);

        ApplyFdkSpreading(spreadEnergies, maskLowFactor, maskHighFactor);

        var maskingThresholdDb = new float[bandCount];
        for (int i = 0; i < bandCount; i++)
        {
            float energyDb = EnergyToDb(spreadEnergies[i]);
            float offsetDb = CalculateThresholdOffsetDb(tonality[i]);
            maskingThresholdDb[i] = energyDb - offsetDb;
        }

        return maskingThresholdDb;
    }

    private static float EnergyToDb(float energy)
    {
        return 10.0f * MathF.Log10(MathF.Max(energy, 1e-12f));
    }

    private static float CalculateThresholdOffsetDb(float tonality)
    {
        float t = Math.Min(Math.Max(tonality, 0.0f), 1.0f);
        return NmtDb + (TmnDb - NmtDb) * t;
    }

    private static void CalculateSpreadingFactors(
        float[] barkValues,
        int bitrate,
        bool isShortWindow,
        out float[] maskLowFactor,
        out float[] maskHighFactor,
        out float[] maskLowFactorSprEn,
        out float[] maskHighFactorSprEn)
    {
        int numPb = barkValues.Length;
        maskLowFactor = new float[numPb];
        maskHighFactor = new float[numPb];
        maskLowFactorSprEn = new float[numPb];
        maskHighFactorSprEn = new float[numPb];

        float maskLowSprEn = isShortWindow ? MaskLowSprEnShortDb : MaskLowSprEnLongDb;
        float maskHighSprEn = isShortWindow
            ? MaskHighSprEnShortDb
            : (bitrate > 20000 ? MaskHighSprEnLongDb : MaskHighSprEnLongLowBrDb);

        for (int i = 0; i < numPb; i++)
        {
            if (i == 0)
            {
                maskHighFactor[i] = 0.0f;
                maskLowFactor[numPb - 1] = 0.0f;
                maskHighFactorSprEn[i] = 0.0f;
                maskLowFactorSprEn[numPb - 1] = 0.0f;
                continue;
            }

            float deltaBark = barkValues[i] - barkValues[i - 1];
            maskHighFactor[i] = GetMaskFactor(MaskHighDb * deltaBark);
            maskLowFactor[i - 1] = GetMaskFactor(MaskLowDb * deltaBark);
            maskHighFactorSprEn[i] = GetMaskFactor(maskHighSprEn * deltaBark);
            maskLowFactorSprEn[i - 1] = GetMaskFactor(maskLowSprEn * deltaBark);
        }
    }

    private static float GetMaskFactor(float dbSlope)
    {
        return MathF.Pow(10.0f, -dbSlope / 10.0f);
    }

    private static void ApplyFdkSpreading(float[] energies, float[] maskLowFactor, float[] maskHighFactor)
    {
        int pbCnt = energies.Length;
        if (pbCnt == 0)
        {
            return;
        }

        float delay = energies[0];
        for (int i = 1; i < pbCnt; i++)
        {
            delay = Math.Max(energies[i], maskHighFactor[i] * delay);
            energies[i] = delay;
        }

        delay = energies[pbCnt - 1];
        for (int i = pbCnt - 2; i >= 0; i--)
        {
            delay = Math.Max(energies[i], maskLowFactor[i] * delay);
            energies[i] = delay;
        }
    }

    private static float[] ComputeAbsoluteThresholdDb(float[] bandCenters)
    {
        int bandCount = bandCenters.Length;
        var absoluteThresholdDb = new float[bandCount];

        for (int i = 0; i < bandCount; i++)
        {
            float centerHz = bandCenters[i];
            if (centerHz <= 0.0f)
            {
                absoluteThresholdDb[i] = 100.0f;
                continue;
            }

            float f = centerHz / 1000.0f;
            absoluteThresholdDb[i] = 3.64f * MathF.Pow(f, -0.8f)
                - 6.5f * MathF.Exp(-0.6f * (f - 3.3f) * (f - 3.3f))
                + 0.001f * MathF.Pow(f, 4.0f);
        }

        return absoluteThresholdDb;
    }

    private static float[] ComputeSmrDb(float[] energyDb, float[] maskingThresholdDb, float[] absoluteThresholdDb)
    {
        int bandCount = energyDb.Length;
        var smrDb = new float[bandCount];

        for (int i = 0; i < bandCount; i++)
        {
            float thresholdDb = Math.Max(maskingThresholdDb[i], absoluteThresholdDb[i]);
            smrDb[i] = energyDb[i] - thresholdDb;
        }

        return smrDb;
    }

    private static void UpdateSmrAndEntropy(PulsarPsychoResult result)
    {
        float totalSmr = 0.0f;
        float totalEnergyDb = 0.0f;
        int bandCount = result.SmrDb.Length;

        for (int i = 0; i < bandCount; i++)
        {
            totalSmr += result.SmrDb[i];
            totalEnergyDb += result.SfbBandEnergiesDb[i];
        }

        result.PerceptualEntropy = totalSmr;
        result.TotalEnergyDb = bandCount > 0 ? totalEnergyDb / bandCount : 0.0f;
    }

    private void ApplyCompleteTemporalMasking(IList<PulsarPsychoResult> frames)
    {
        if (frames == null || frames.Count == 0)
        {
            return;
        }

        int frameCount = frames.Count;
        int bandCount = frames[0].MaskingThresholdDb.Length;
        var originalThresholds = new float[frameCount][];

        for (int i = 0; i < frameCount; i++)
        {
            originalThresholds[i] = (float[])frames[i].MaskingThresholdDb.Clone();
        }

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float[] targetThresholds = frames[frameIndex].MaskingThresholdDb;

            int preStart = Math.Max(0, frameIndex - _settings.PreMaskFrames);
            for (int prevFrame = preStart; prevFrame < frameIndex; prevFrame++)
            {
                float[] sourceThresholds = originalThresholds[prevFrame];
                for (int band = 0; band < bandCount; band++)
                {
                    targetThresholds[band] = Math.Max(targetThresholds[band], sourceThresholds[band]);
                }
            }

            int postEnd = Math.Min(frameCount, frameIndex + _settings.PostMaskFrames + 1);
            for (int nextFrame = frameIndex + 1; nextFrame < postEnd; nextFrame++)
            {
                float[] sourceThresholds = originalThresholds[nextFrame];
                for (int band = 0; band < bandCount; band++)
                {
                    targetThresholds[band] = Math.Max(targetThresholds[band], sourceThresholds[band]);
                }
            }
        }
    }
}
