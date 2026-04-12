using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar.Psycho;

public sealed class PulsarFrameDemand
{
    public required double FrameDemand { get; init; }
    public required double PePressure { get; init; }
    public required double SmrPressure { get; init; }
    public required double MaskingPressure { get; init; }
    public required double TransientPressure { get; init; }
    public required double BassProtection { get; init; }
    public required double TonalProtection { get; init; }
    public required double[] BandDemands { get; init; }
}

public sealed class PulsarDemandAnalysis
{
    public required IReadOnlyList<PulsarFrameDemand> Frames { get; init; }
    public required double AverageFrameDemand { get; init; }
    public required double TotalDemandWeight { get; init; }
    public required double[] FrameDemandShares { get; init; }
}

public sealed class PulsarDemandModel
{
    private readonly PulsarAllocationConfig _config;

    public PulsarDemandModel(PulsarAllocationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public PulsarDemandAnalysis Analyze(
        IReadOnlyList<PulsarFramePlan> framePlans,
        IReadOnlyList<PulsarPsychoResult> psychoFrames)
    {
        ArgumentNullException.ThrowIfNull(framePlans);
        ArgumentNullException.ThrowIfNull(psychoFrames);

        if (framePlans.Count != psychoFrames.Count)
        {
            throw new ArgumentException("Frame plan count must match psycho frame count.", nameof(psychoFrames));
        }

        int frameCount = framePlans.Count;
        if (frameCount == 0)
        {
            return new PulsarDemandAnalysis
            {
                Frames = Array.Empty<PulsarFrameDemand>(),
                AverageFrameDemand = 1.0,
                TotalDemandWeight = 1.0,
                FrameDemandShares = Array.Empty<double>(),
            };
        }

        var rawFrameDemands = new double[frameCount];
        var frameEnergy = new double[frameCount];
        var frameTonality = new double[frameCount];
        var rawFrames = new PulsarFrameDemand[frameCount];
        
        double activeEnergySum = 0.0;
        int activeEnergyCount = 0;

        for (int i = 0; i < frameCount; i++)
        {
            PulsarPsychoResult psycho = psychoFrames[i];
            PulsarFramePlan plan = framePlans[i];

            frameEnergy[i] = Math.Max(-120.0, psycho.TotalEnergyDb);
            frameTonality[i] = Average(psycho.Tonality);
            
            rawFrameDemands[i] = ComputeFrameDemand(plan, psycho);

            // Wir sammeln nur hörbare Energie für den wahren Durchschnitt
            if (frameEnergy[i] > -70.0) 
            {
                activeEnergySum += frameEnergy[i];
                activeEnergyCount++;
            }
        }

        // Wahre Durchschnittslautstärke des aktiven Songs (ohne Stille-Phasen)
        double averageActiveEnergy = activeEnergyCount > 0
            ? activeEnergySum / activeEnergyCount
            : ComputeAverage(frameEnergy);
            
        double totalDemand = 0.0;

        for (int i = 0; i < frameCount; i++)
        {
            // Wir übergeben jetzt den wahren Durchschnitt für die adaptive Stille-Erkennung
            rawFrameDemands[i] = ApplyEnergyPenalty(rawFrameDemands[i], frameEnergy[i], averageActiveEnergy);
            rawFrameDemands[i] = Math.Max(rawFrameDemands[i], _config.MinFrameDemand);
            totalDemand += rawFrameDemands[i];
        }

        var smoothedDemands = SmoothFrameDemands(rawFrameDemands, radius: 2);
        double totalSmoothed = smoothedDemands.Sum();
        
        if (totalSmoothed <= 0.0)
        {
            for (int i = 0; i < frameCount; i++)
            {
                smoothedDemands[i] = 1.0 / frameCount;
            }
            totalSmoothed = 1.0;
        }

        var frameShares = new double[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            frameShares[i] = smoothedDemands[i] / totalSmoothed;
        }

        for (int i = 0; i < frameCount; i++)
        {
            PulsarPsychoResult psycho = psychoFrames[i];
            PulsarFramePlan plan = framePlans[i];
            var tonality = frameTonality[i];
            
            double pePressure = ComputePePressure(plan, psycho);
            double smrPressure = ComputeSmrPressure(psycho);
            double maskingPressure = ComputeMaskingPressure(psycho);
            double transientPressure = ComputeTransientPressure(psycho);
            double tonalProtection = Math.Clamp(tonality * 1.15, 0.0, 2.0);
            double bassProtection = Math.Max(0.0, psycho.BassProtection);

            rawFrames[i] = new PulsarFrameDemand
            {
                FrameDemand = Math.Max(smoothedDemands[i], _config.MinFrameDemand),
                PePressure = pePressure,
                SmrPressure = smrPressure,
                MaskingPressure = maskingPressure,
                TransientPressure = transientPressure,
                BassProtection = bassProtection,
                TonalProtection = tonalProtection,
                BandDemands = ComputeBandDemands(psycho),
            };
        }

        return new PulsarDemandAnalysis
        {
            Frames = rawFrames,
            AverageFrameDemand = totalDemand / frameCount,
            TotalDemandWeight = totalSmoothed,
            FrameDemandShares = frameShares,
        };
    }

    private double ComputeFrameDemand(PulsarFramePlan plan, PulsarPsychoResult psycho)
    {
        double pePressure = ComputePePressure(plan, psycho);
        double smrPressure = ComputeSmrPressure(psycho);
        double maskingPressure = ComputeMaskingPressure(psycho);
        double transientPressure = ComputeTransientPressure(psycho);
        double tonalPressure = ComputeTonalPressure(psycho);
        double bassProtection = Math.Max(0.0, psycho.BassProtection);
        double preEchoRisk = Math.Clamp(plan.PreEchoRisk, 0.0, 2.0);
        double spectralFlux = Math.Clamp(plan.SpectralFlux, 0.0, 1.5);
        
        double blockModifier = plan.BlockSize switch
        {
            <= 512 => 0.14,
            <= 1024 => 0.08,
            >= 4096 => -0.08,
            _ => 0.00,
        };

        double demand = _config.BaseFrameDemand
            + (_config.PerceptualEntropyWeight * pePressure)
            + (_config.SmrWeight * smrPressure)
            + (_config.MaskingPressureWeight * maskingPressure)
            + (_config.TransientBoostWeight * transientPressure)
            + (_config.TonalProtectionWeight * tonalPressure)
            + (_config.BassProtectionWeight * bassProtection)
            + (0.18 * preEchoRisk)
            + (0.10 * spectralFlux)
            + blockModifier;

        return Math.Max(0.0, demand);
    }

    private double ComputePePressure(PulsarFramePlan plan, PulsarPsychoResult psycho)
    {
        double referenceBlockSize = 1024.0;
        double blockRatio = referenceBlockSize / Math.Max(128.0, plan.BlockSize);
        double normalizedPe = psycho.PerceptualEntropy * blockRatio;

        return Math.Clamp(normalizedPe / 400.0, 0.0, 3.2);
    }

    private double ComputeSmrPressure(PulsarPsychoResult psycho)
    {
        return Math.Clamp((psycho.AveragePositiveSmr + 3.0f) / 14.0f, 0.0, 2.6);
    }

    private double ComputeMaskingPressure(PulsarPsychoResult psycho)
    {
        return Math.Clamp(psycho.MaskingPressure / 16.0f, 0.0, 2.2);
    }

    private double ComputeTransientPressure(PulsarPsychoResult psycho)
    {
        return Math.Clamp(psycho.TransientScore + (psycho.OpusTransientEstimate * 0.85f), 0.0, 3.4);
    }

    private static double ComputeTonalPressure(PulsarPsychoResult psycho)
    {
        return Math.Clamp(Average(psycho.Tonality) * 1.28, 0.0, 2.1);
    }

    private double ApplyEnergyPenalty(double demand, double energyDb, double averageActiveEnergy)
    {
        // FIX 2.1: Adaptive Noise Floor. Stille wird definiert als 50 dB UNTER der Durchschnittslautstärke.
        // Ein lauter Song (-10 dB) hat Stille bei -60 dB. Ein leiser Song (-35 dB) hat Stille bei -85 dB.
        // Max. Grenze bei -80 dB, damit extrem laute Master-Tracks trotzdem sauber abschneiden.
        double noiseFloor = Math.Min(-80.0, averageActiveEnergy - 50.0);
        double dynamicRange = 40.0; // Überblenden zwischen Noise Floor und hörbarem Audio
        
        double penalty = Math.Clamp((energyDb - noiseFloor) / dynamicRange, 0.05, 1.0);
        return demand * penalty;
    }

    private static double[] SmoothFrameDemands(double[] demands, int radius)
    {
        int count = demands.Length;
        var smoothed = new double[count];

        for (int i = 0; i < count; i++)
        {
            double total = 0.0;
            double weight = 0.0;

            for (int offset = -radius; offset <= radius; offset++)
            {
                int index = Math.Clamp(i + offset, 0, count - 1);
                double factor = 1.0 - (Math.Abs(offset) / (double)(radius + 1));
                total += demands[index] * factor;
                weight += factor;
            }

            smoothed[i] = total / Math.Max(1.0, weight);
        }

        return smoothed;
    }

    private double[] ComputeBandDemands(PulsarPsychoResult psycho)
    {
        int bandCount = psycho.SfbBandEnergiesDb.Length;
        if (bandCount == 0)
        {
            return Array.Empty<double>();
        }

        var bandWeights = new double[bandCount];
        double totalWeight = 0.0;

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            float energyDb = psycho.SfbBandEnergiesDb[bandIndex];
            float thresholdDb = psycho.MaskingThresholdDb[Math.Min(bandIndex, psycho.MaskingThresholdDb.Length - 1)];
            float smrDb = psycho.SmrDb[Math.Min(bandIndex, psycho.SmrDb.Length - 1)];
            float tonality = psycho.Tonality[Math.Min(bandIndex, psycho.Tonality.Length - 1)];
            float centerHz = psycho.SfbBandCenters[Math.Min(bandIndex, psycho.SfbBandCenters.Length - 1)];

            double headroom = Math.Max(0.0, energyDb - thresholdDb);
            double tonalBonus = 1.0 + (0.35 * Math.Clamp(tonality, 0.0f, 1.0f));
            double lowBandBias = centerHz <= _config.BassProtectionEndHz ? 1.0 + (_config.LowBandProtectionBoost * psycho.LowBandStability) : 1.0;
            double transientBoost = centerHz >= _config.HighBandTransientStartHz ? 1.0 + (_config.HighBandTransientBoost * Math.Clamp(psycho.TransientScore, 0.0f, 1.0f)) : 1.0;
            double energyWeight = 1.0 + Math.Clamp(headroom / 10.0, 0.0, 1.6);
            double smrWeight = 1.0 + Math.Clamp(smrDb / 16.0, 0.0, 1.4);

            double weight = 0.32
                + (0.32 * energyWeight)
                + (0.22 * smrWeight)
                + (0.20 * tonalBonus)
                + (0.16 * lowBandBias)
                + (0.14 * transientBoost);

            bandWeights[bandIndex] = Math.Max(0.08, weight);
            totalWeight += bandWeights[bandIndex];
        }

        if (totalWeight <= 0.0)
        {
            double uniformWeight = 1.0 / bandCount;
            Array.Fill(bandWeights, uniformWeight);
            return bandWeights;
        }

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            bandWeights[bandIndex] /= totalWeight;
        }

        return bandWeights;
    }

    private static double Average(IReadOnlyList<float> values)
    {
        if (values.Count == 0) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private static double ComputeAverage(double[] values)
    {
        if (values.Length == 0) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return sum / values.Length;
    }
}