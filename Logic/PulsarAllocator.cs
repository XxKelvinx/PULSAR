using System;
using System.Collections.Generic;
using Pulsar.Psycho;

public sealed class PulsarAllocationConfig
{
    // PVQ-Probe: Qualität steuert hier nur noch grob das Pulsbudget.
    // V1 = 138 kbps, V2 = 256 kbps, V3 = 355 kbps.
    public int Quality { get; init; } = PulsarQualityProfile.DefaultQuality; 

    public int SampleRate { get; init; } = 44100;
    public int HopSize { get; init; } = PulsarBlockLadder.ControlHopSize;
    public int ChannelCount { get; init; } = 1;
    public double PulseBudgetPerBit { get; init; } = 0.25;
    public int MinPulseBudgetPerFrame { get; init; } = 24;
    public int MaxPulseBudgetPerFrame { get; init; } = 16384;
    
    // Adaptive Floor Limits
    public double MaxBandFloorBitsRatio { get; init; } = 0.25;
    public double MinBandFloorBitsRatio { get; init; } = 0.02;

    public double PerceptualEntropyWeight { get; init; } = 0.85;
    public double SmrWeight { get; init; } = 0.70;
    public double MaskingPressureWeight { get; init; } = 0.55;
    public double TransientBoostWeight { get; init; } = 0.25;
    public double BassProtectionWeight { get; init; } = 0.40;
    public double TonalProtectionWeight { get; init; } = 0.22;
    public double LowBandProtectionBoost { get; init; } = 0.35;
    public double HighBandTransientBoost { get; init; } = 0.22;
    public float BassProtectionEndHz { get; init; } = 260.0f;
    public float HighBandTransientStartHz { get; init; } = 4000.0f;

    public double BaseFrameDemand { get; init; } = 0.10;
    public double MinFrameDemand { get; init; } = 0.10;
}

public sealed class PulsarFrameAllocation
{
    public required int TargetBits { get; init; } // Wird in TrueVBR ignoriert, bleibt für API-Kompatibilität
    public required int MetadataBits { get; init; }
    public required int BlockBits { get; init; }
    public required double MetadataRatio { get; init; }
    public required int PulseBudget { get; init; }
    /// <summary>Frame bit budget in integer bits (not BITRES fractional). Used by V6 encoder.</summary>
    public required int BitBudget { get; init; }
    public required double ComplexityWeight { get; init; }
}

public sealed class PulsarRateControlResult
{
    public required int FinalGlobalGain { get; init; }
    public required int EstimatedBits { get; init; } // In TrueVBR immer 0 (irrelevant)
    public required int TargetBits { get; init; }    // In TrueVBR immer 0 (irrelevant)
    public required bool BudgetMet { get; init; }    // In TrueVBR immer true
    public required PulsarQuantizedSpectrum QuantizedSpectrum { get; init; }
}

public sealed class PulsarAllocator
{
    private readonly PulsarAllocationConfig _config;
    private readonly PulsarDemandModel _demandModel;

    // FDK-AAC thrExp^4 quality reduction values (in dB, applied in thrExp = threshold/4 domain).
    // Negative = strenger (threshold sinkt → mehr Bits nötig → höhere Qualität, V1).
    // Positiv = lockerer (threshold steigt → weniger Bits nötig → stärkere Kompression, V3).
    // Skalierung: redVal wird in der thrExp-dB-Domäne addiert, dann mit 4 zur effectiveThrDb.
    // Schritt-Faktor: 6 dB SNR ≈ 1 Bit; wird für Gain-Ableitung aus SNR-Target verwendet.
    private const float ThrExpGainStep = 6.0f;
    private const int SpreadNone = 0;
    private const int SpreadLight = 1;
    private const int SpreadNormal = 2;
    private const int SpreadAggressive = 3;

    public PulsarAllocator(PulsarAllocationConfig? config = null)
    {
        _config = config ?? new PulsarAllocationConfig();
        _demandModel = new PulsarDemandModel(_config);
    }

    public List<PulsarFrameAllocation> AllocateSong(
        IReadOnlyList<PulsarFramePlan> framePlans,
        IReadOnlyList<PulsarPsychoResult> psychoFrames)
    {
        ArgumentNullException.ThrowIfNull(framePlans);
        ArgumentNullException.ThrowIfNull(psychoFrames);

        var allocations = new List<PulsarFrameAllocation>(framePlans.Count);
        if (framePlans.Count == 0) return allocations;

        PulsarDemandAnalysis demandAnalysis = _demandModel.Analyze(framePlans, psychoFrames);

        // PVQ-Probe: Quality bestimmt nur das grobe Pulsbudget pro Control-Frame.
        int qualityLevel = PulsarQualityProfile.ClampQuality(_config.Quality);
        int nominalSongKbps = PulsarQualityProfile.GetNominalKbps(qualityLevel);
        double bitsPerControlFrame = nominalSongKbps * 1000.0 * (_config.HopSize / (double)Math.Max(1, _config.SampleRate));
        int nominalBitsPerFrame = Math.Max(96, (int)Math.Round(bitsPerControlFrame / Math.Max(1, _config.ChannelCount)));
        int nominalPulseBudget = (int)Math.Round(nominalBitsPerFrame * PulsarQualityProfile.GetBitBudgetScale(qualityLevel));
        nominalPulseBudget = Math.Clamp(
            nominalPulseBudget,
            Math.Max(1, _config.MinPulseBudgetPerFrame),
            Math.Max(_config.MinPulseBudgetPerFrame, _config.MaxPulseBudgetPerFrame));

        // --- Real bit budget (Phase 2): derive from target kbps directly ---
        // Each control-hop segment covers HopSize samples at SampleRate.
        double secondsPerSegment = _config.HopSize / (double)Math.Max(1, _config.SampleRate);
        double bitsPerSegmentNominal = nominalSongKbps * 1000.0 * secondsPerSegment / Math.Max(1, _config.ChannelCount);
        int nominalBitBudget = Math.Max(32, (int)Math.Round(bitsPerSegmentNominal));

        int[] framePulseBudgets = BuildSongFramePulseBudgets(demandAnalysis, nominalPulseBudget);
        int[] frameBitBudgets = BuildSongFrameBitBudgets(demandAnalysis, nominalBitBudget);

        for (int index = 0; index < framePlans.Count; index++)
        {
            PulsarFrameDemand demandFrame = demandAnalysis.Frames[index];

            int framePulseBudget = framePulseBudgets[index];
            int frameBitBudget = frameBitBudgets[index];
            allocations.Add(new PulsarFrameAllocation
            {
                TargetBits = 0,     // Abgeschafft
                MetadataBits = 0,   // Abgeschafft (Der RangeCoder schreibt, was er braucht)
                BlockBits = 0,      // Abgeschafft
                MetadataRatio = 0,
                PulseBudget = framePulseBudget,
                BitBudget = frameBitBudget,
                ComplexityWeight = framePulseBudget / (double)Math.Max(1, nominalPulseBudget),
            });
        }

        return allocations;
    }

    public PulsarRateControlResult QuantizeFrameVbr(
        float[] mdctSpectrum,
        int framePulseBudget,
        PulsarPsychoResult psycho,
        int ignoredTargetBits,     // Wird ignoriert! API bleibt für deinen Packer intakt.
        int[]? bandOffsets = null)
    {
        ArgumentNullException.ThrowIfNull(mdctSpectrum);
        ArgumentNullException.ThrowIfNull(psycho);

        // 1. Basis-Gain aus thrExp^4-Formel (weiterhin grob als Frame-Skalierung)
        int qualityLevel = PulsarQualityProfile.ClampQuality(_config.Quality);
        float redValDb = PulsarQualityProfile.GetThresholdRedValDb(qualityLevel); // dB-Verschiebung in der thrExp-Domäne

        float avgThrExpDb = ComputeAverageThrExpDb(psycho);
        float effectiveThrExpDb = avgThrExpDb + redValDb;

        // effectiveThrDb = (thrExp)^4 in der Log-Domäne = effectiveThrExpDb * 4
        float effectiveThrDb = effectiveThrExpDb * 4.0f;

        // 2. Global Gain wird später als grobe Pulsbudget-Korrektur benutzt.
        float avgEnergyDb = ComputeAverageBandEnergyDb(psycho);
        float snrTargetDb = avgEnergyDb - effectiveThrDb;
        int frameGain = (int)Math.Round(snrTargetDb / ThrExpGainStep * 64.0f);

        // 3. Stille- und Noise-Floor-Erkennung (Bit-Saver)
        if (psycho.TotalEnergyDb < -48.0f)
        {
            frameGain += 120; // Ruhige Frames dürfen gröber quantisiert werden
        }

        // 4. Grenzen setzen (Gottmodus 0 bis Trash 550)
        frameGain = Math.Clamp(frameGain, 0, 550);

        // 5. One-Shot PVQ-Quantisierung.
        var bestQuantized = PulsarQuantizer.QuantizeSpectrumDetailed(mdctSpectrum, framePulseBudget, psycho, frameGain, bandOffsets);

        return new PulsarRateControlResult
        {
            FinalGlobalGain = frameGain,
            EstimatedBits = 0, // Weg mit dem Lügen-Estimator!
            TargetBits = 0,
            BudgetMet = true,
            QuantizedSpectrum = bestQuantized,
        };
    }

    /// <summary>
    /// Berechnet den gewichteten Durchschnitt der thrExp-Werte (= MaskingThreshold / 4 in dB).
    /// Entspricht FDKaacEnc_calcThreshExp: thrExpLdData = sfbThresholdLdData >> 2.
    /// Hier in dB: thrExpDb = maskingThresholdDb / 4.
    /// </summary>
    private static float ComputeAverageThrExpDb(PulsarPsychoResult psycho)
    {
        int bandCount = psycho.MaskingThresholdDb.Length;
        if (bandCount == 0) return -30.0f; // Fallback

        double sum = 0.0;
        double weightSum = 0.0;

        for (int b = 0; b < bandCount; b++)
        {
            float thrDb = psycho.MaskingThresholdDb[b];
            float energyDb = GetArrayValue(psycho.SfbBandEnergiesDb, b);

            // Nur Bänder einbeziehen, die über der absoluten Hörschwelle liegen
            if (energyDb < -90.0f) continue;

            // Gewichtung: Bänder mit hoher Energie beeinflussen den Gain stärker
            double weight = Math.Max(0.01, energyDb + 100.0); // Energie als Gewicht (0..200)
            sum += (thrDb / 4.0) * weight;  // thrExp = threshold^(1/4) = thrDb/4 in dB-Domäne
            weightSum += weight;
        }

        return weightSum > 0.0 ? (float)(sum / weightSum) : -30.0f;
    }

    /// <summary>
    /// Berechnet den gewichteten Durchschnitt der Band-Energien in dB.
    /// </summary>
    private static float ComputeAverageBandEnergyDb(PulsarPsychoResult psycho)
    {
        int bandCount = psycho.SfbBandEnergiesDb.Length;
        if (bandCount == 0) return -40.0f;

        double sum = 0.0;
        int count = 0;

        for (int b = 0; b < bandCount; b++)
        {
            float e = psycho.SfbBandEnergiesDb[b];
            if (e < -90.0f) continue;
            sum += e;
            count++;
        }

        return count > 0 ? (float)(sum / count) : -40.0f;
    }

    private static float GetArrayValue(float[] arr, int index)
    {
        if (arr.Length == 0) return 0.0f;
        return arr[Math.Min(index, arr.Length - 1)];
    }

    /// <summary>
    /// Global bit budget distribution: same demand-weighted approach as pulse budgets
    /// but operating in real bits. The total song bit budget is derived from target kbps.
    /// </summary>
    private int[] BuildSongFrameBitBudgets(PulsarDemandAnalysis demandAnalysis, int nominalBitBudget)
    {
        int frameCount = demandAnalysis.Frames.Count;
        if (frameCount <= 0) return Array.Empty<int>();

        // Demand-weighted VBR bit budgets: give complex frames more bits.
        // Global pool stays at nominalBitBudget × frameCount.
        int minBitBudget = Math.Max(16, nominalBitBudget / 4);
        int maxBitBudget = Math.Min(65535, nominalBitBudget * 3);

        // Compute demand weights from psycho model
        double totalDemand = 0;
        var demands = new double[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            double d = Math.Max(0.01, demandAnalysis.Frames[i].FrameDemand);
            demands[i] = d;
            totalDemand += d;
        }

        // Scale demands so sum(budgets) == nominalBitBudget * frameCount
        long globalPool = (long)nominalBitBudget * frameCount;
        int[] budgets = new int[frameCount];
        long allocated = 0;
        for (int i = 0; i < frameCount; i++)
        {
            double share = demands[i] / totalDemand * globalPool;
            budgets[i] = Math.Clamp((int)Math.Round(share), minBitBudget, maxBitBudget);
            allocated += budgets[i];
        }

        // Redistribute any over/under allocation proportionally
        long diff = globalPool - allocated;
        if (diff != 0 && frameCount > 0)
        {
            // Spread the difference across frames proportionally to their budget
            for (int i = 0; i < frameCount && diff != 0; i++)
            {
                int adjust = (int)Math.Clamp(diff * budgets[i] / Math.Max(1, allocated), -budgets[i] / 2, maxBitBudget - budgets[i]);
                budgets[i] += adjust;
                diff -= adjust;
            }
        }

        return budgets;
    }

    private int[] BuildSongFramePulseBudgets(PulsarDemandAnalysis demandAnalysis, int nominalPulseBudget)
    {
        int frameCount = demandAnalysis.Frames.Count;
        if (frameCount <= 0)
        {
            return Array.Empty<int>();
        }

        // Global song budget: total pulses available = nominalPulseBudget × frameCount.
        // Demand weighting below redistributes these pulses toward frames that need them;
        // the per-frame cap gives enough headroom (2.5× nominal) for transient / tonal peaks
        // while the floor protects near-silent frames from starving the entropy coder.
        int minimumPulseBudget = Math.Max(4, Math.Min(_config.MinPulseBudgetPerFrame, Math.Max(8, nominalPulseBudget / 6)));
        int configuredMaximum = Math.Max(minimumPulseBudget + 1, _config.MaxPulseBudgetPerFrame);
        int maximumPulseBudget = Math.Min(configuredMaximum, Math.Max(minimumPulseBudget + 1, (int)Math.Round(nominalPulseBudget * 2.5)));
        // Leave global average essentially at nominal; demand weights bias frames, not total.
        double songBudgetScale = Math.Clamp(0.95 + (demandAnalysis.AverageFrameDemand * 0.08), 0.90, 1.10);
        int totalSongPulseBudget = (int)Math.Round(nominalPulseBudget * frameCount * songBudgetScale);
        totalSongPulseBudget = Math.Clamp(totalSongPulseBudget, minimumPulseBudget * frameCount, maximumPulseBudget * frameCount);

        double[] weights = new double[frameCount];
        double totalWeight = 0.0;
        for (int i = 0; i < frameCount; i++)
        {
            PulsarFrameDemand demandFrame = demandAnalysis.Frames[i];
            double demandShare = demandAnalysis.FrameDemandShares.Length > i
                ? Math.Max(1e-6, demandAnalysis.FrameDemandShares[i])
                : 1.0 / frameCount;
            double normalizedDemand = Math.Clamp(demandFrame.FrameDemand / Math.Max(0.25, demandAnalysis.AverageFrameDemand), 0.25, 4.0);
            double transientBoost = 1.0 + (0.08 * Math.Clamp(demandFrame.TransientPressure, 0.0, 3.0));
            double tonalBoost = 1.0 + (0.06 * Math.Clamp(demandFrame.TonalProtection, 0.0, 2.0));
            double bassBoost = 1.0 + (0.04 * Math.Clamp(demandFrame.BassProtection, 0.0, 2.0));
            double weight = Math.Max(1e-6, demandShare * normalizedDemand * transientBoost * tonalBoost * bassBoost);
            weights[i] = weight;
            totalWeight += weight;
        }

        int[] budgets = new int[frameCount];
        if (totalWeight <= 0.0)
        {
            return CreateUniformBudgets(frameCount, minimumPulseBudget);
        }

        for (int i = 0; i < frameCount; i++)
        {
            budgets[i] = minimumPulseBudget;
        }

        int remaining = totalSongPulseBudget - (minimumPulseBudget * frameCount);
        if (remaining <= 0)
        {
            return budgets;
        }

        double[] remainders = new double[frameCount];
        int assigned = 0;
        for (int i = 0; i < frameCount; i++)
        {
            int headroom = maximumPulseBudget - budgets[i];
            if (headroom <= 0)
            {
                remainders[i] = 0.0;
                continue;
            }

            double exactShare = remaining * (weights[i] / totalWeight);
            int floorShare = (int)Math.Floor(exactShare);
            int assignedShare = Math.Min(headroom, Math.Max(0, floorShare));
            budgets[i] += assignedShare;
            remainders[i] = exactShare - assignedShare;
            assigned += assignedShare;
        }

        int leftover = remaining - assigned;
        while (leftover > 0)
        {
            int bestIndex = -1;
            double bestScore = double.NegativeInfinity;

            for (int i = 0; i < frameCount; i++)
            {
                int headroom = maximumPulseBudget - budgets[i];
                if (headroom <= 0)
                {
                    continue;
                }

                double score = remainders[i] + (weights[i] * 0.001) + (headroom * 0.0001);
                if (score > bestScore || (Math.Abs(score - bestScore) < 1e-12 && (bestIndex < 0 || i < bestIndex)))
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            budgets[bestIndex]++;
            remainders[bestIndex] = 0.0;
            leftover--;
        }

        return budgets;
    }

    private static int[] CreateUniformBudgets(int frameCount, int nominalPulseBudget)
    {
        int[] budgets = new int[frameCount];
        for (int i = 0; i < frameCount; i++) budgets[i] = nominalPulseBudget;
        return budgets;
    }

    public int EstimateEntropyBits(int[] quantizedData)
    {
        ArgumentNullException.ThrowIfNull(quantizedData);

        int bits = 0;
        int cursor = 0;
        while (cursor < quantizedData.Length)
        {
            if (quantizedData[cursor] == 0)
            {
                int runStart = cursor;
                while (cursor < quantizedData.Length && quantizedData[cursor] == 0)
                {
                    cursor++;
                }

                int runLength = cursor - runStart;
                bits += 2 + EstimateZeroRunBits(runLength);
                continue;
            }

            int magnitude = Math.Abs(quantizedData[cursor]);
            bits += 2 + EstimateMagnitudeBits(magnitude) + 1;
            cursor++;
        }

        return bits;
    }

    private static int EstimateZeroRunBits(int runLength)
    {
        if (runLength <= 1) return 1;
        if (runLength <= 3) return 3;
        if (runLength <= 7) return 5;
        if (runLength <= 15) return 7;
        return 10 + ILog((uint)Math.Max(1, runLength - 16));
    }

    private static int EstimateMagnitudeBits(int magnitude)
    {
        if (magnitude <= 1) return 1;
        if (magnitude == 2) return 2;
        if (magnitude <= 4) return 3;
        if (magnitude <= 8) return 4;
        if (magnitude <= 16) return 5;
        return 6 + ILog((uint)Math.Max(1, magnitude - 17));
    }

    private static int ILog(uint value)
    {
        int bits = 0;
        while (value > 0)
        {
            value >>= 1;
            bits++;
        }

        return bits;
    }

    private static int ComputeSpreadDecision(PulsarFrameDemand demandFrame, int bandIndex, int bandCount)
    {
        double bandPosition = bandCount == 1 ? 0.0 : bandIndex / (double)(bandCount - 1);
        double tonal = Math.Clamp(demandFrame.TonalProtection, 0.0, 2.0);
        double transient = Math.Clamp(demandFrame.TransientPressure, 0.0, 3.0);
        double lowBand = Math.Clamp(demandFrame.BassProtection, 0.0, 2.0);
        double frameDemand = Math.Clamp(demandFrame.FrameDemand, 0.0, 2.0);

        double score = (0.72 * transient)
            + (0.12 * frameDemand)
            + (0.10 * bandPosition)
            - (0.45 * tonal)
            - (0.18 * lowBand);

        if (score < 0.16) return SpreadNone;
        if (score < 0.42) return SpreadLight;
        if (score < 0.80) return SpreadNormal;
        return SpreadAggressive;
    }
}
