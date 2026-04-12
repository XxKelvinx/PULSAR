using System;
using System.Collections.Generic;

namespace Pulsar.Psycho;

public partial class PulsarPsycho
{
    private const int DefaultBandCount = 21;
    private readonly PulsarPsychoSettings _settings;
    private readonly PulsarTransientDetector _transientDetector = new();

    public PulsarPsycho(PulsarPsychoSettings? settings = null)
    {
        _settings = settings ?? new PulsarPsychoSettings();
    }

    public IReadOnlyList<PulsarPsychoResult> AnalyzeSong(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return AnalyzeSongFrames(input);
    }

    public PulsarPsychoSongAnalysis AnalyzeSongWithGlobalBudget(float[] input, int totalBits)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<PulsarPsychoResult> frames = AnalyzeSongFrames(input);
        float[] weights = new float[frames.Count];
        float weightSum = 0.0f;

        for (int i = 0; i < frames.Count; i++)
        {
            float pe = MathF.Max(0.0f, frames[i].PerceptualEntropy);
            float transientBoost = 1.0f + (0.35f * MathF.Max(0.0f, frames[i].TransientScore));
            weights[i] = MathF.Max(0.05f, pe * transientBoost);
            weightSum += weights[i];
        }

        if (weightSum <= 0.0f)
        {
            Array.Fill(weights, 1.0f);
            weightSum = weights.Length;
        }

        float[] shares = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            shares[i] = weights[i] / weightSum;
        }

        float totalPe = 0.0f;
        for (int i = 0; i < frames.Count; i++)
        {
            totalPe += frames[i].PerceptualEntropy;
        }

        return new PulsarPsychoSongAnalysis
        {
            Frames = frames,
            TotalPerceptualEntropy = totalPe,
            AveragePerceptualEntropy = frames.Count > 0 ? totalPe / frames.Count : 0.0f,
            FrameEntropyWeights = weights,
            FrameBudgetShares = shares,
        };
    }

    private List<PulsarPsychoResult> AnalyzeSongFrames(float[] input)
    {
        int frameSize = _settings.FftSize;
        int hopSize = _settings.HopSize;
        int frameCount = input.Length <= 0 ? 0 : ((input.Length + hopSize - 1) / hopSize);
        var frames = new List<PulsarPsychoResult>(frameCount);

        if (frameCount == 0)
        {
            return frames;
        }

        float[] frame = new float[frameSize];
        float[]? previousMdct = null;

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int offset = frameIndex * hopSize;
            Array.Clear(frame, 0, frame.Length);

            if (offset < input.Length)
            {
                int available = Math.Min(frameSize, input.Length - offset);
                Array.Copy(input, offset, frame, 0, available);
            }

            var analysis = AnalyzeFrame(frame, previousMdct);
            frames.Add(analysis.Result);
            previousMdct = analysis.MdctCoefficients;
        }

        ApplyCompleteTemporalMasking(frames);

        for (int i = 0; i < frames.Count; i++)
        {
            float[] smrDb = ComputeSmrDb(frames[i].SfbBandEnergiesDb, frames[i].MaskingThresholdDb, frames[i].AbsoluteThresholdDb);
            float pe = PulsarPerceptualEntropy.CalculateFramePe(frames[i].SfbBandEnergiesDb, frames[i].MaskingThresholdDb);
            frames[i].PerceptualEntropy = pe;
            frames[i].TotalEnergyDb = ComputeAverage(frames[i].SfbBandEnergiesDb);
            Array.Copy(smrDb, frames[i].SmrDb, smrDb.Length);
            PopulateDerivedMetrics(frames[i]);
        }

        return frames;
    }

    public (PulsarPsychoResult Result, float[] MdctCoefficients) AnalyzeFrame(float[] frame, float[]? previousMdct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length != _settings.FftSize)
        {
            throw new ArgumentException("Frame length must match psycho FFT size.", nameof(frame));
        }

        float[] windowed = ApplySineWindow(frame);
        float[] mdct = PulsarTransformEngine.Mdct(windowed);

        int mdctBins = mdct.Length;
        int[] bandOffsets = BuildBandOffsets(mdctBins, _settings.SampleRate, DefaultBandCount);
        int bandCount = bandOffsets.Length - 1;
        int[] bandWidths = new int[bandCount];
        float[] bandCenters = new float[bandCount];
        float[] bandEnergies = new float[bandCount];
        float[] bandPeaks = new float[bandCount];
        float[] bandEnergiesDb = new float[bandCount];
        float[] bandPeaksDb = new float[bandCount];
        float[] barkValues = new float[bandCount];

        for (int band = 0; band < bandCount; band++)
        {
            int start = bandOffsets[band];
            int end = bandOffsets[band + 1];
            int width = Math.Max(1, end - start);
            bandWidths[band] = width;

            double energy = 0.0;
            float peak = 0.0f;
            double weightedHz = 0.0;
            double totalMag = 0.0;

            for (int bin = start; bin < end; bin++)
            {
                float coeff = mdct[bin];
                float magnitude = MathF.Abs(coeff);
                energy += coeff * coeff;
                peak = MathF.Max(peak, magnitude);

                float centerHz = GetMdctBinCenterHz(bin, mdctBins, _settings.SampleRate);
                weightedHz += centerHz * magnitude;
                totalMag += magnitude;
            }

            bandEnergies[band] = (float)Math.Max(energy, 1e-12);
            bandPeaks[band] = Math.Max(peak, 1e-12f);
            bandEnergiesDb[band] = EnergyToDb(bandEnergies[band]);
            bandPeaksDb[band] = EnergyToDb(MathF.Max(bandPeaks[band] * bandPeaks[band], 1e-12f));

            float bandCenterHz = totalMag > 1e-12
                ? (float)(weightedHz / totalMag)
                : GetMdctBinCenterHz(start + (width / 2), mdctBins, _settings.SampleRate);
            bandCenters[band] = bandCenterHz;
            barkValues[band] = HzToBark(bandCenterHz);
        }

        float[] tonality = ComputeTonality(mdct, previousMdct, bandEnergies, bandPeaks);
        bool isShortWindow = frame.Length <= 1024;
        int bitrateGuess = Math.Max(8000, _settings.SampleRate);
        float[] maskingThresholdDb = ComputeMaskingThresholdDb(bandEnergies, tonality, barkValues, bitrateGuess, isShortWindow);
        float[] absoluteThresholdDb = ComputeAbsoluteThresholdDb(bandCenters);
        float[] smrDb = ComputeSmrDb(bandEnergiesDb, maskingThresholdDb, absoluteThresholdDb);
        float perceptualEntropy = PulsarPerceptualEntropy.CalculateFramePe(bandEnergiesDb, maskingThresholdDb);

        float opusTransientEstimate = ComputeOpusTransientEstimate(frame, tonality, bandCenters);
        float transientScore = ComputeTransientScore(mdct, previousMdct, bandEnergies, opusTransientEstimate);

        var result = new PulsarPsychoResult
        {
            SampleRate = _settings.SampleRate,
            FftSize = _settings.FftSize,
            SfbBandCenters = bandCenters,
            SfbBandOffsets = bandOffsets,
            SfbBandWidths = bandWidths,
            SfbBandEnergiesDb = bandEnergiesDb,
            SfbBandPeaksDb = bandPeaksDb,
            Tonality = tonality,
            MaskingThresholdDb = maskingThresholdDb,
            AbsoluteThresholdDb = absoluteThresholdDb,
            SmrDb = smrDb,
            PerceptualEntropy = perceptualEntropy,
            TotalEnergyDb = ComputeAverage(bandEnergiesDb),
            TransientScore = transientScore,
            OpusTransientEstimate = opusTransientEstimate,
        };
        PopulateDerivedMetrics(result);

        return (result, mdct);
    }

    private static float[] ApplySineWindow(float[] frame)
    {
        int n = frame.Length;
        float[] windowed = new float[n];

        for (int i = 0; i < n; i++)
        {
            float window = MathF.Sin(MathF.PI * (i + 0.5f) / n);
            windowed[i] = frame[i] * window;
        }

        return windowed;
    }

    private static int[] BuildBandOffsets(int mdctBins, int sampleRate, int bandCount)
    {
        float nyquist = sampleRate * 0.5f;
        float maxBark = HzToBark(nyquist);
        var offsets = new int[bandCount + 1];
        offsets[0] = 0;

        for (int i = 1; i < bandCount; i++)
        {
            float bark = maxBark * i / bandCount;
            float hz = BarkToHz(bark);
            int bin = (int)MathF.Round((hz / nyquist) * (mdctBins - 1));
            offsets[i] = Math.Clamp(bin, offsets[i - 1] + 1, mdctBins - (bandCount - i));
        }

        offsets[bandCount] = mdctBins;
        return offsets;
    }

    private static float GetMdctBinCenterHz(int bin, int mdctBins, int sampleRate)
    {
        if (mdctBins <= 1)
        {
            return 0.0f;
        }

        return (bin + 0.5f) * (sampleRate * 0.5f / mdctBins);
    }

    private static float HzToBark(float hz)
    {
        return 13.0f * MathF.Atan(0.00076f * hz) + 3.5f * MathF.Atan(MathF.Pow(hz / 7500.0f, 2.0f));
    }

    private static float BarkToHz(float bark)
    {
        float low = 0.0f;
        float high = 24000.0f;

        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (low + high);
            float value = HzToBark(mid);
            if (value < bark)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return 0.5f * (low + high);
    }

    private static float ComputeAverage(float[] values)
    {
        if (values.Length == 0)
        {
            return 0.0f;
        }

        float total = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            total += values[i];
        }

        return total / values.Length;
    }

    private float ComputeOpusTransientEstimate(float[] frame, float[] tonality, float[] bandCenters)
    {
        float toneishness = tonality.Length > 0 ? ComputeAverage(tonality) : 0.0f;
        float weightedToneFreq = 0.0f;
        float toneWeight = 0.0f;

        for (int i = 0; i < tonality.Length; i++)
        {
            float weight = MathF.Max(0.0f, tonality[i]);
            weightedToneFreq += bandCenters[Math.Min(i, bandCenters.Length - 1)] * weight;
            toneWeight += weight;
        }

        float toneFreq = toneWeight > 1e-12f
            ? (weightedToneFreq / toneWeight) / MathF.Max(1.0f, _settings.SampleRate * 0.5f)
            : 0.0f;

        _transientDetector.TransientAnalysis(
            frame,
            frame.Length,
            1,
            out float tfEstimate,
            out _,
            allowWeakTransients: true,
            out bool weakTransient,
            toneFreq,
            toneishness);

        float weakBoost = weakTransient ? 0.12f : 0.0f;
        return Math.Clamp(tfEstimate + weakBoost, 0.0f, 1.5f);
    }

    private static void PopulateDerivedMetrics(PulsarPsychoResult result)
    {
        result.AveragePositiveSmr = ComputeAveragePositiveSmr(result.SmrDb);
        result.PeakSmr = ComputePeakSmr(result.SmrDb);
        result.MaskingPressure = ComputeMaskingPressure(result.SfbBandEnergiesDb, result.MaskingThresholdDb);
        result.BassProtection = ComputeBassProtection(result);
        result.TonalProtection = ComputeTonalProtection(result);
        result.LowBandStability = ComputeLowBandStability(result);
    }

    private static float ComputeTransientScore(float[] currentMdct, float[]? previousMdct, float[] bandEnergies, float opusTransientEstimate)
    {
        if (previousMdct == null || previousMdct.Length != currentMdct.Length)
        {
            return Math.Clamp(opusTransientEstimate, 0.0f, 1.5f);
        }

        double diff = 0.0;
        double energy = 0.0;

        for (int i = 0; i < currentMdct.Length; i++)
        {
            double a = Math.Abs(currentMdct[i]);
            double b = Math.Abs(previousMdct[i]);
            diff += Math.Max(0.0, a - b);
            energy += a;
        }

        float frameFlux = energy > 1e-12 ? (float)(diff / energy) : 0.0f;
        float bassEnergy = bandEnergies.Length > 0 ? bandEnergies[0] : 0.0f;
        float highEnergy = bandEnergies.Length > 0 ? bandEnergies[^1] : 0.0f;
        float spectralTilt = highEnergy > 0.0f ? MathF.Min(1.0f, highEnergy / MathF.Max(1e-12f, bassEnergy + highEnergy)) : 0.0f;

        float mdctTransient = (float)Math.Clamp(frameFlux * (0.75f + (0.50f * spectralTilt)), 0.0f, 1.5f);
        return (float)Math.Clamp((mdctTransient * 0.58f) + (opusTransientEstimate * 0.72f), 0.0f, 1.5f);
    }

    private static float ComputeAveragePositiveSmr(float[] smrDb)
    {
        if (smrDb.Length == 0)
        {
            return 0.0f;
        }

        float total = 0.0f;
        int count = 0;
        for (int i = 0; i < smrDb.Length; i++)
        {
            if (smrDb[i] > 0.0f)
            {
                total += smrDb[i];
                count++;
            }
        }

        return count == 0 ? 0.0f : total / count;
    }

    private static float ComputePeakSmr(float[] smrDb)
    {
        float peak = 0.0f;
        for (int i = 0; i < smrDb.Length; i++)
        {
            peak = MathF.Max(peak, smrDb[i]);
        }

        return peak;
    }

    private static float ComputeMaskingPressure(float[] energyDb, float[] maskingThresholdDb)
    {
        int count = Math.Min(energyDb.Length, maskingThresholdDb.Length);
        if (count == 0)
        {
            return 0.0f;
        }

        float total = 0.0f;
        for (int i = 0; i < count; i++)
        {
            total += MathF.Max(0.0f, energyDb[i] - maskingThresholdDb[i]);
        }

        return total / count;
    }

    private static float ComputeBassProtection(PulsarPsychoResult result)
    {
        float total = 0.0f;
        float weightSum = 0.0f;
        for (int i = 0; i < result.SfbBandCenters.Length; i++)
        {
            float centerHz = result.SfbBandCenters[i];
            if (centerHz > 260.0f)
            {
                continue;
            }

            float weight = centerHz < 100.0f ? 1.5f : 1.0f;
            float headroom = MathF.Max(0.0f, result.SfbBandEnergiesDb[i] - result.MaskingThresholdDb[Math.Min(i, result.MaskingThresholdDb.Length - 1)]);
            total += headroom * weight;
            weightSum += weight;
        }

        return weightSum <= 0.0f ? 0.0f : total / weightSum;
    }

    private static float ComputeTonalProtection(PulsarPsychoResult result)
    {
        if (result.Tonality.Length == 0)
        {
            return 0.0f;
        }

        float total = 0.0f;
        for (int i = 0; i < result.Tonality.Length; i++)
        {
            float centerHz = result.SfbBandCenters[Math.Min(i, result.SfbBandCenters.Length - 1)];
            float weight = centerHz < 2500.0f ? 1.15f : 0.95f;
            total += Math.Clamp(result.Tonality[i], 0.0f, 1.0f) * weight;
        }

        return total / result.Tonality.Length;
    }

    private static float ComputeLowBandStability(PulsarPsychoResult result)
    {
        float total = 0.0f;
        float weightSum = 0.0f;

        for (int i = 0; i < result.SfbBandCenters.Length; i++)
        {
            float centerHz = result.SfbBandCenters[i];
            if (centerHz > 260.0f)
            {
                continue;
            }

            float headroom = MathF.Max(0.0f, result.SfbBandEnergiesDb[i] - result.MaskingThresholdDb[Math.Min(i, result.MaskingThresholdDb.Length - 1)]);
            float tonality = Math.Clamp(result.Tonality[Math.Min(i, result.Tonality.Length - 1)], 0.0f, 1.0f);
            float transientPenalty = 1.0f - (Math.Clamp(result.TransientScore, 0.0f, 1.0f) * 0.6f);
            float weight = centerHz < 100.0f ? 1.35f : 1.0f;

            total += (((headroom / 20.0f) * 0.7f) + (tonality * 0.5f)) * transientPenalty * weight;
            weightSum += weight;
        }

        return weightSum <= 0.0f ? 0.0f : total / weightSum;
    }
}
