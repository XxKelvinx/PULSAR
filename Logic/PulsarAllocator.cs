using System;
using System.Collections.Generic;
using Pulsar.Psycho;

public sealed class PulsarAllocationConfig
{
    // TRUE VBR: Wir nutzen Qualitätsstufen (0 bis 9) statt TargetKbps!
    // 0 = Best (Transparent), 5 = Normal, 9 = Worst (Starke Kompression)
    // Achtung: Die effektive Quality-Zuordnung im aktuellen Projekt wird durch die Bitbudget-Formel
    // in Program.cs sowie durch den Quantizer zusammen bestimmt.
    public int Quality { get; init; } = 4; 

    public int SampleRate { get; init; } = 44100;
    public int HopSize { get; init; } = PulsarBlockLadder.ControlHopSize;
    
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
    public required int[] BandBits { get; init; }
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
    // Negative = strenger (threshold sinkt → mehr Bits nötig → höhere Qualität, Q0/V0).
    // Positiv = lockerer (threshold steigt → weniger Bits nötig → stärkere Kompression, Q9/V9).
    // Skalierung: redVal wird in der thrExp-dB-Domäne addiert, dann mit 4 zur effectiveThrDb.
    private static readonly float[] QualityRedVals = { -9.0f, -7.5f, -6.0f, -4.5f, -3.0f, -1.5f, 0.0f, 1.5f, 3.0f, 4.5f };

    // Schritt-Faktor: 6 dB SNR ≈ 1 Bit; wird für Gain-Ableitung aus SNR-Target verwendet.
    private const float ThrExpGainStep = 6.0f;

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

        // TRUE VBR: Kein globales Budget mehr! 
        // Wir bestimmen die "Dummy"-Bits für den Quantizer rein aus dem Quality-Level.
        int qualityLevel = Math.Clamp(_config.Quality, 0, 9);
        int nominalBitsPerFrame = Math.Max(320, 1850 - (qualityLevel * 150)); 

        for (int index = 0; index < framePlans.Count; index++)
        {
            PulsarFrameDemand demandFrame = demandAnalysis.Frames[index];
            
            // Wir verteilen fiktive BandBits basierend auf dem Demand, damit 
            // die 'effectiveDensity' in deinem Quantizer korrekt arbeiten kann.
            int[] bandBits = AllocateBandBits(demandFrame, nominalBitsPerFrame);

            allocations.Add(new PulsarFrameAllocation
            {
                TargetBits = 0,     // Abgeschafft
                MetadataBits = 0,   // Abgeschafft (Der RangeCoder schreibt, was er braucht)
                BlockBits = 0,      // Abgeschafft
                MetadataRatio = 0,
                BandBits = bandBits,
                ComplexityWeight = demandFrame.FrameDemand,
            });
        }

        return allocations;
    }

    /// <summary>
    /// TRUE VBR (Constant Quality) Quantisierung.
    /// Ersetzt die alte Binary-Search. Nutzt Ogg Vorbis/LAME Logik und
    /// FDK-AAC thrExp^4-Formel für die Gain-Bestimmung.
    /// </summary>
    public PulsarRateControlResult QuantizeFrameVbr(
        float[] mdctSpectrum,
        int[] bandBits,
        PulsarPsychoResult psycho,
        int ignoredTargetBits,     // Wird ignoriert! API bleibt für deinen Packer intakt.
        PulsarFrameDemand demand)  // NEU: Wir brauchen den Demand für die PE-Modulation!
    {
        ArgumentNullException.ThrowIfNull(mdctSpectrum);
        ArgumentNullException.ThrowIfNull(bandBits);
        ArgumentNullException.ThrowIfNull(psycho);

        // 1. Basis-Gain aus thrExp^4-Formel (FDK-AAC clean-room Konzept)
        //    thrExp = thr^(1/4) in der Log-Domäne = thrDb / 4
        //    redVal = qualitätsgesteuerte Verschiebung (negativ = strenger, positiv = lockerer)
        //    effectiveThr = (thrExp + redVal)^4 = ((thrDb/4) + redVal)^4
        //    Dies bestimmt, wie viel Spielraum der Quantisierer gegenüber der Maskierung hat.
        int qualityLevel = Math.Clamp(_config.Quality, 0, 9);
        float redValDb = QualityRedVals[qualityLevel]; // dB-Verschiebung in der thrExp-Domäne

        float avgThrExpDb = ComputeAverageThrExpDb(psycho);
        float effectiveThrExpDb = avgThrExpDb + redValDb;

        // effectiveThrDb = (thrExp)^4 in der Log-Domäne = effectiveThrExpDb * 4
        float effectiveThrDb = effectiveThrExpDb * 4.0f;

        // 2. Global Gain aus der effektiven Schwelle ableiten.
        //    Wir wollen: quantization_noise_db ≈ effectiveThrDb
        //    Da noise_db ≈ energyDb - snrDb und snrDb ≈ gain * 6 dB, gilt:
        //    gain ≈ (avgEnergyDb - effectiveThrDb) / 6.0
        float avgEnergyDb = ComputeAverageBandEnergyDb(psycho);
        float snrTargetDb = avgEnergyDb - effectiveThrDb;
        int frameGain = (int)Math.Round(snrTargetDb / ThrExpGainStep);

        // 3. Stille- und Noise-Floor-Erkennung (Bit-Saver)
        if (psycho.TotalEnergyDb < -48.0f)
        {
            frameGain += 120; // Ruhige Frames dürfen gröber quantisiert werden
        }

        // 4. Grenzen setzen (Gottmodus 0 bis Trash 550)
        frameGain = Math.Clamp(frameGain, 0, 550);

        // 5. One-Shot Quantisierung (Keine while-Schleife mehr!)
        var bestQuantized = PulsarQuantizer.QuantizeSpectrumDetailed(mdctSpectrum, bandBits, psycho, frameGain);

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

    private int[] AllocateBandBits(PulsarFrameDemand demandFrame, int nominalBlockBits)
    {
        int bandCount = demandFrame.BandDemands.Length;
        if (bandCount <= 0) return Array.Empty<int>();

        int[] bandBits = new int[bandCount];
        int assigned = 0;

        // Fiktive Verteilung der Bits basierend auf SMR (BandDemands)
        for (int i = 0; i < bandCount; i++)
        {
            int share = (int)Math.Floor(nominalBlockBits * demandFrame.BandDemands[i]);
            bandBits[i] = Math.Max(2, share); // Jedes Band bekommt mindestens 2 Dummy-Bits, damit es aktiv bleibt
            assigned += bandBits[i];
        }

        return bandBits;
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
}
