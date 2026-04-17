using System;
using System.Numerics;
using Pulsar.Psycho;

public sealed class PulsarQuantizedBand
{
    public required int Start { get; init; }
    public required int Width { get; init; }
    public required int Bits { get; init; }
    public required float EnergyDb { get; init; }
    public required uint EnergyQ { get; init; }
    public required uint ScaleQ { get; init; }
    public required float Scale { get; init; }
    public required int RequestedPulseCount { get; init; }
    public required int PulseCount { get; init; }
    public required int PulseCap { get; init; }
    public required float BandNorm { get; init; }
    public required float PulseNorm { get; init; }
    public required int SpreadDecision { get; init; }
    public required int[] Levels { get; init; }
}

public sealed class PulsarQuantizedSpectrum
{
    public required int SpectrumLength { get; init; }
    public required PulsarQuantizedBand[] Bands { get; init; }
}

public static class PulsarQuantizer
{
    private static readonly bool LegacySplitEnabled = false;
    private const float GlobalGainStep = 0.015625f;
    private const int SpreadNone = 0;
    private const int SpreadLight = 1;
    private const int SpreadNormal = 2;
    private const int SpreadAggressive = 3;

    // --- ARCHIVER SYNC LIMITS ---
    private const int ScaleQuantLevels = 4096;
    private const float MinScale = 1e-12f;
    private const float MaxScale = 65536.0f;

    public static void QuantizeSpectrum(float[] spectrum, int framePulseBudget, PulsarPsychoResult psycho)
    {
        PulsarQuantizedSpectrum quantized = QuantizeSpectrumDetailed(spectrum, framePulseBudget, psycho);
        DequantizeSpectrum(spectrum, quantized);
    }

    public static void QuantizeSpectrum(float[] spectrum, int framePulseBudget, PulsarPsychoResult psycho, int globalGain)
    {
        PulsarQuantizedSpectrum quantized = QuantizeSpectrumDetailed(spectrum, framePulseBudget, psycho, globalGain);
        DequantizeSpectrum(spectrum, quantized);
    }

    public static PulsarQuantizedSpectrum QuantizeSpectrumDetailed(float[] spectrum, int framePulseBudget, PulsarPsychoResult psycho)
    {
        return QuantizeSpectrumDetailed(spectrum, framePulseBudget, psycho, 0);
    }

    public static PulsarQuantizedSpectrum QuantizeSpectrumDetailed(float[] spectrum, int framePulseBudget, PulsarPsychoResult psycho, int globalGain, int[]? bandOffsets = null)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(psycho);

        if (spectrum.Length == 0 || psycho.SfbBandOffsets.Length < 2)
        {
            return new PulsarQuantizedSpectrum { SpectrumLength = spectrum.Length, Bands = Array.Empty<PulsarQuantizedBand>() };
        }

        int psychoBinCount = bandOffsets == null ? Math.Max(1, psycho.SfbBandOffsets[^1]) : 0;
        int bandCount = bandOffsets != null ? Math.Max(0, bandOffsets.Length - 1) : Math.Max(0, psycho.SfbBandOffsets.Length - 1);
        var bands = new PulsarQuantizedBand[bandCount];
        var bandPrepass = new BandPrepass[bandCount];

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            int start;
            int end;
            if (bandOffsets != null)
            {
                start = bandOffsets[Math.Min(bandIndex, bandOffsets.Length - 1)];
                end = bandOffsets[Math.Min(bandIndex + 1, bandOffsets.Length - 1)];
            }
            else
            {
                start = MapBandOffset(psycho.SfbBandOffsets[Math.Min(bandIndex, psycho.SfbBandOffsets.Length - 1)], psychoBinCount, spectrum.Length);
                end = MapBandOffset(psycho.SfbBandOffsets[Math.Min(bandIndex + 1, psycho.SfbBandOffsets.Length - 1)], psychoBinCount, spectrum.Length);
            }

            if (end <= start)
            {
                bandPrepass[bandIndex] = new BandPrepass { Start = start, End = start, Width = 0, BandNorm = 0.0f, EnergyQ = 0 };
                continue;
            }

            int width = end - start;
            float bandNorm = ComputeBandNorm(spectrum, start, end);
            if (bandNorm <= 1e-12f)
            {
                bandPrepass[bandIndex] = new BandPrepass { Start = start, End = end, Width = width, BandNorm = 0.0f, EnergyQ = 0 };
                continue;
            }

            uint energyQ = QuantizeLogValue(bandNorm, MinScale, MaxScale, ScaleQuantLevels);
            bandPrepass[bandIndex] = new BandPrepass { Start = start, End = end, Width = width, BandNorm = bandNorm, EnergyQ = energyQ };
        }

        float[] quantizedEnergyDb = new float[bandCount];
        int[] bandWidths = new int[bandCount];
        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            BandPrepass prepass = bandPrepass[bandIndex];
            bandWidths[bandIndex] = prepass.Width;
            float energyScale = DequantizeLogValue(prepass.EnergyQ, MinScale, MaxScale, ScaleQuantLevels);
            quantizedEnergyDb[bandIndex] = 20.0f * MathF.Log10(Math.Max(1e-12f, energyScale));
        }

        int[] bandPulseBudgets = PulsarImplicitAllocator.AllocatePulseCounts(quantizedEnergyDb, bandWidths, framePulseBudget);

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            BandPrepass prepass = bandPrepass[bandIndex];
            if (prepass.Width <= 0 || prepass.BandNorm <= 1e-12f)
            {
                bands[bandIndex] = CreateEmptyBand(prepass.Start, prepass.Width);
                continue;
            }

            float centerHz = GetBandValue(psycho.SfbBandCenters, bandIndex);
            float tonality = Math.Clamp(GetBandValue(psycho.Tonality, bandIndex), 0.0f, 1.0f);
            float smrDb = GetBandValue(psycho.SmrDb, bandIndex);
            float transient = Math.Clamp(psycho.TransientScore, 0.0f, 1.5f);
            float opusTransientEstimate = Math.Clamp(psycho.OpusTransientEstimate, 0.0f, 1.5f);
            float lowBandStability = Math.Clamp(psycho.LowBandStability, 0.0f, 1.0f);
            float averagePositiveSmr = Math.Clamp(psycho.AveragePositiveSmr, 0.0f, 24.0f);
            int spreadDecision = ComputeSpreadDecision(centerHz, tonality, transient, opusTransientEstimate, lowBandStability, averagePositiveSmr);

            // ── Opus exp_rotation: spread energy before PVQ search ──
            // This rotates adjacent coefficients to make energy more uniform,
            // improving PVQ's ability to capture peaked spectra.
            // Reference: exp_rotation() in celt/bands.c
            int requestedPulseCount = Math.Max(0, bandPulseBudgets[bandIndex]);
            int pulseCap = ComputePulseCap(prepass.Width, spreadDecision, tonality, transient, opusTransientEstimate, averagePositiveSmr);
            int pulseCount = requestedPulseCount <= 0
                ? 0
                : Math.Clamp(ScalePulseBudget(requestedPulseCount, globalGain, prepass.Width, centerHz, tonality, transient, opusTransientEstimate, lowBandStability, averagePositiveSmr, smrDb, spreadDecision), 1, pulseCap);
            if (pulseCount <= 0)
            {
                bands[bandIndex] = CreateEmptyBand(prepass.Start, prepass.Width);
                continue;
            }

            PulsarQuantizedBand band = BuildPvqBand(
                spectrum,
                prepass.Start,
                prepass.End,
                pulseCount,
                prepass.BandNorm,
                prepass.EnergyQ,
                quantizedEnergyDb[bandIndex],
                requestedPulseCount,
                pulseCap,
                centerHz,
                tonality,
                transient,
                opusTransientEstimate,
                lowBandStability,
                averagePositiveSmr,
                smrDb,
                spreadDecision);
            bands[bandIndex] = band;
        }

        return new PulsarQuantizedSpectrum { SpectrumLength = spectrum.Length, Bands = bands };
    }

    public static void DequantizeSpectrum(float[] spectrum, PulsarQuantizedSpectrum quantized)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(quantized);
        Array.Clear(spectrum, 0, spectrum.Length);

        // CELT LCG PRNG seed — deterministic noise fill
        uint seed = 0xDEADBEEF;

        foreach (PulsarQuantizedBand band in quantized.Bands)
        {
            if (band.Width <= 0 || band.Start >= spectrum.Length) continue;

            int width = Math.Min(band.Width, spectrum.Length - band.Start);

            if (band.Levels.Length == 0 || band.PulseCount == 0)
            {
                // ── Anti-collapse: fill zero-pulse bands with shaped noise ──
                // CELT anti_collapse prevents energy holes by injecting low-level noise
                // into bands that got zero pulses. The noise level depends on band energy.
                // Reference: anti_collapse() in celt/bands.c
                if (band.BandNorm > 1e-12f && band.EnergyDb > -90.0f)
                {
                    // Noise level: proportional to band energy but much quieter
                    // CELT uses: r = thresh * sqrt(1/N) where thresh = 0.5 * 2^(-depth/8)
                    // For zero-pulse bands, depth=0, so thresh=0.5
                    float noiseLevel = band.BandNorm * 0.5f / MathF.Sqrt(Math.Max(1, width));
                    for (int i = 0; i < width; i++)
                    {
                        // CELT LCG: seed = seed * 1664525 + 1013904223
                        seed = seed * 1664525 + 1013904223;
                        float noise = ((int)seed) / (float)int.MaxValue;
                        spectrum[band.Start + i] = noise * noiseLevel;
                    }
                }
                continue;
            }

            int levelsWidth = Math.Min(width, band.Levels.Length);
            float scale = MathF.Max(band.Scale, 1e-12f);

            for (int i = 0; i < levelsWidth; i++)
            {
                spectrum[band.Start + i] = band.Levels[i] * scale;
            }

            // ── Inverse exp_rotation after dequantization ──
            CeltVq.ExpRotation(spectrum.AsSpan(band.Start, levelsWidth), dir: -1, stride: 1, band.PulseCount, band.SpreadDecision);
        }
    }

    private static PulsarQuantizedBand BuildPvqBand(
        float[] spectrum,
        int start,
        int end,
        int pulseCount,
        float bandNorm,
        uint energyQ,
        float energyDb,
        int requestedPulseCount,
        int pulseCap,
        float centerHz,
        float tonality,
        float transientScore,
        float opusTransientEstimate,
        float lowBandStability,
        float averagePositiveSmr,
        float smrDb,
        int spreadDecision)
    {
        int width = end - start;

        // ── Opus CELT band splitting: recursively split wide bands ──
        // CELT splits bands wider than ~8 bins in half, allocates pulses by energy ratio,
        // and PVQ-searches each half independently. This dramatically improves quality.
        const int BandSplitThreshold = 8;
        const int MinPulsesForSplit = 4; // Need at least this many pulses to justify splitting

        if (LegacySplitEnabled && width >= BandSplitThreshold && pulseCount >= MinPulsesForSplit)
        {
            int[] levels = SplitAndQuantizeBand(spectrum, start, end, pulseCount);

            float pulseNormSplit = 0.0f;
            for (int i = 0; i < width; i++)
                pulseNormSplit += levels[i] * levels[i];
            pulseNormSplit = MathF.Sqrt(Math.Max(1.0f, pulseNormSplit));

            // Scale derived directly from bandNorm / pulseNorm; decoder reconstructs the same
            // value since bandNorm comes from transmitted EnergyQ and pulseNorm from decoded pulses.
            float rawScaleSplit = bandNorm / pulseNormSplit;
            uint scaleQSplit = 0;
            float syncedScaleSplit = rawScaleSplit;

            return new PulsarQuantizedBand
            {
                Start = start,
                Width = width,
                Bits = pulseCount,
                EnergyDb = energyDb,
                EnergyQ = energyQ,
                ScaleQ = scaleQSplit,
                Scale = syncedScaleSplit,
                RequestedPulseCount = requestedPulseCount,
                PulseCount = pulseCount,
                PulseCap = pulseCap,
                BandNorm = bandNorm,
                PulseNorm = pulseNormSplit,
                SpreadDecision = spreadDecision,
                Levels = levels,
            };
        }

        // ── Small band: direct PVQ search ──
        CeltPvqResult pvq = width >= 16 && pulseCount >= 4
            ? CeltVq.QuantPartition(spectrum.AsSpan(start, width), pulseCount, spreadDecision)
            : CeltVq.AlgQuant(spectrum.AsSpan(start, width), pulseCount, spreadDecision);
        int[]? directLevels = pvq.Pulses.Length == 0 ? null : pvq.Pulses;

        if (directLevels == null)
        {
            return CreateEmptyBand(start, width);
        }

        float pulseNorm = 0.0f;
        for (int i = 0; i < width; i++)
        {
            pulseNorm += directLevels[i] * directLevels[i];
        }
        pulseNorm = MathF.Sqrt(Math.Max(1.0f, pulseNorm));

        float rawScale = bandNorm / pulseNorm;
        uint scaleQ = 0;
        float syncedScale = rawScale;

        return new PulsarQuantizedBand
        {
            Start = start,
            Width = width,
            Bits = pulseCount,
            EnergyDb = energyDb,
            EnergyQ = energyQ,
            ScaleQ = scaleQ,
            Scale = syncedScale,
            RequestedPulseCount = requestedPulseCount,
            PulseCount = pulseCount,
            PulseCap = pulseCap,
            BandNorm = bandNorm,
            PulseNorm = pulseNorm,
            SpreadDecision = spreadDecision,
            Levels = directLevels,
        };
    }

    /// <summary>
    /// Opus CELT-style greedy PVQ search: maximizes cos(angle) between X and y.
    /// Returns null if the input is silence.
    /// </summary>
    private static int[]? GreedyPvqSearch(float[] spectrum, int start, int width, int pulseCount)
    {
        float[] absX = new float[width];
        int[] signX = new int[width];
        float sumAbsX = 0.0f;

        for (int i = 0; i < width; i++)
        {
            float val = spectrum[start + i];
            absX[i] = MathF.Abs(val);
            signX[i] = val >= 0.0f ? 1 : -1;
            sumAbsX += absX[i];
        }

        if (sumAbsX <= 1e-12f)
        {
            return null;
        }

        float[] band = new float[width];
        Array.Copy(spectrum, start, band, 0, width);
        CeltPvqResult pvq = CeltVq.AlgQuant(band, pulseCount, SpreadNone, blocks: 1);
        if (UseCeltVqFastPath())
        {
            return pvq.Pulses;
        }

        int[] iy = new int[width];
        int pulsesLeft = pulseCount;

        if (pulseCount > Math.Max(32, width * 2))
        {
            return ProjectPvqSearch(absX, signX, sumAbsX, pulseCount);
        }

        // Pre-search projection when K > N/2
        if (pulseCount > width / 2)
        {
            float scale = (pulseCount + 0.8f) / sumAbsX;
            for (int i = 0; i < width; i++)
            {
                int p = (int)(scale * absX[i]);
                iy[i] = p;
                pulsesLeft -= p;
            }
            while (pulsesLeft < 0)
            {
                int worstIdx = 0;
                float worstRatio = float.PositiveInfinity;
                for (int i = 0; i < width; i++)
                {
                    if (iy[i] > 0)
                    {
                        float ratio = absX[i] / iy[i];
                        if (ratio < worstRatio)
                        {
                            worstRatio = ratio;
                            worstIdx = i;
                        }
                    }
                }
                iy[worstIdx]--;
                pulsesLeft++;
            }
        }

        // Greedy pulse-by-pulse: maximize Rxy²/Ryy
        double xy = 0.0;
        double yy = 0.0;
        for (int i = 0; i < width; i++)
        {
            xy += absX[i] * iy[i];
            yy += iy[i] * (double)iy[i];
        }

        while (pulsesLeft > 0)
        {
            int bestIdx = 0;
            double bestNum = double.NegativeInfinity;
            double bestDen = 1.0;

            for (int i = 0; i < width; i++)
            {
                double newXy = xy + absX[i];
                double newYy = yy + 2.0 * iy[i] + 1.0;
                double num = newXy * newXy;
                if (num * bestDen > bestNum * newYy)
                {
                    bestNum = num;
                    bestDen = newYy;
                    bestIdx = i;
                }
            }

            iy[bestIdx]++;
            xy += absX[bestIdx];
            yy += 2.0 * (iy[bestIdx] - 1) + 1.0;
            pulsesLeft--;
        }

        // Restore signs
        int[] levels = new int[width];
        for (int i = 0; i < width; i++)
        {
            levels[i] = iy[i] * signX[i];
        }
        return levels;
    }

    private static int[] ProjectPvqSearch(float[] absX, int[] signX, float sumAbsX, int pulseCount)
    {
        int width = absX.Length;
        int[] levels = new int[width];
        if (width == 0 || pulseCount <= 0 || sumAbsX <= 1e-12f)
        {
            return levels;
        }

        double scale = pulseCount / (double)sumAbsX;
        double[] remainders = new double[width];
        int assigned = 0;

        for (int i = 0; i < width; i++)
        {
            double exact = absX[i] * scale;
            int pulses = (int)Math.Floor(exact);
            levels[i] = pulses;
            remainders[i] = exact - pulses;
            assigned += pulses;
        }

        while (assigned < pulseCount)
        {
            int bestIndex = 0;
            double bestRemainder = double.NegativeInfinity;
            for (int i = 0; i < width; i++)
            {
                if (remainders[i] > bestRemainder)
                {
                    bestRemainder = remainders[i];
                    bestIndex = i;
                }
            }

            levels[bestIndex]++;
            remainders[bestIndex] = 0.0;
            assigned++;
        }

        while (assigned > pulseCount)
        {
            int bestIndex = -1;
            double bestScore = double.PositiveInfinity;
            for (int i = 0; i < width; i++)
            {
                if (levels[i] <= 0)
                {
                    continue;
                }

                double score = remainders[i];
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            levels[bestIndex]--;
            assigned--;
        }

        for (int i = 0; i < width; i++)
        {
            levels[i] *= signX[i];
        }

        return levels;
    }

    private static bool UseCeltVqFastPath() => true;

    /// <summary>
    /// Opus CELT-style recursive band splitting for wide bands.
    /// Splits the band in half, allocates pulses by energy ratio (theta angle),
    /// and PVQ-searches each half independently.
    /// Reference: quant_partition in celt/bands.c
    /// </summary>
    private static int[] SplitAndQuantizeBand(float[] spectrum, int start, int end, int totalPulses)
    {
        int width = end - start;
        int[] levels = new int[width];

        // Base case: small enough for direct PVQ search
        if (width <= 8 || totalPulses < 4)
        {
            int[]? direct = GreedyPvqSearch(spectrum, start, width, totalPulses);
            if (direct != null)
                Array.Copy(direct, 0, levels, 0, width);
            return levels;
        }

        // Split in half
        int mid = width / 2;
        int leftStart = start;
        int leftEnd = start + mid;
        int rightStart = start + mid;
        int rightEnd = end;

        // Compute energy of each half
        double leftEnergy = 0.0, rightEnergy = 0.0;
        for (int i = leftStart; i < leftEnd; i++)
            leftEnergy += spectrum[i] * (double)spectrum[i];
        for (int i = rightStart; i < rightEnd; i++)
            rightEnergy += spectrum[i] * (double)spectrum[i];

        double totalEnergy = leftEnergy + rightEnergy;
        if (totalEnergy <= 1e-24)
        {
            return levels;
        }

        // Compute theta: energy split angle (CELT-style)
        // theta = atan2(sqrt(rightEnergy), sqrt(leftEnergy))
        double sqrtLeft = Math.Sqrt(Math.Max(leftEnergy, 1e-24));
        double sqrtRight = Math.Sqrt(Math.Max(rightEnergy, 1e-24));
        double theta = Math.Atan2(sqrtRight, sqrtLeft);

        // Allocate pulses proportional to energy, with minimum 1 per active half
        double leftRatio = Math.Cos(theta);
        leftRatio = leftRatio * leftRatio; // cos²(theta) = left energy fraction
        int leftPulses = (int)Math.Round(totalPulses * leftRatio);
        leftPulses = Math.Clamp(leftPulses, 1, totalPulses - 1);
        int rightPulses = totalPulses - leftPulses;

        // Ensure both halves get at least 1 pulse if they have energy
        if (leftPulses <= 0 && leftEnergy > 1e-20) { leftPulses = 1; rightPulses = totalPulses - 1; }
        if (rightPulses <= 0 && rightEnergy > 1e-20) { rightPulses = 1; leftPulses = totalPulses - 1; }

        // Recurse on each half
        int[] leftLevels = SplitAndQuantizeBand(spectrum, leftStart, leftEnd, Math.Max(0, leftPulses));
        int[] rightLevels = SplitAndQuantizeBand(spectrum, rightStart, rightEnd, Math.Max(0, rightPulses));

        // Apply energy gains: mid = cos(theta), side = sin(theta)
        // Scale each half's levels to match the original energy split
        // The PVQ search already matches shape; we just copy into the output
        Array.Copy(leftLevels, 0, levels, 0, mid);
        Array.Copy(rightLevels, 0, levels, mid, width - mid);

        return levels;
    }

    /// <summary>
    /// Opus CELT exp_rotation: rotates adjacent coefficients to spread energy uniformly.
    /// Applied forward before PVQ search, inverse after dequantization.
    /// When spread=Aggressive, rotation is strongest; when spread=None, rotation is weakest.
    /// Reference: exp_rotation() in celt/bands.c
    /// </summary>
    private static void ApplyExpRotation(float[] spectrum, int start, int width, int pulseCount, int spreadDecision, bool forward)
    {
        if (width <= 1 || pulseCount <= 0) return;

        // Compute rotation angle based on spread decision and pulse density
        // CELT: angle = (N² / (N² + K²)) with spread-dependent scaling
        double n2 = width * (double)width;
        double k2 = pulseCount * (double)pulseCount;
        double baseAngle = n2 / (n2 + k2);

        // Spread-dependent scaling (CELT uses different gains per spreading mode)
        double spreadGain = spreadDecision switch
        {
            SpreadNone => 0.0,       // No rotation for no-spread bands
            SpreadLight => 0.5,
            SpreadNormal => 1.0,
            _ => 1.5,               // SpreadAggressive
        };

        double angle = baseAngle * spreadGain;
        if (angle <= 1e-6) return;

        float c = (float)Math.Cos(angle);
        float s = (float)Math.Sin(angle);
        if (!forward) s = -s; // Inverse rotation

        // Apply Givens rotations to adjacent pairs
        // Forward: left to right; Inverse: right to left
        if (forward)
        {
            for (int i = 0; i < width - 1; i++)
            {
                float x0 = spectrum[start + i];
                float x1 = spectrum[start + i + 1];
                spectrum[start + i] = c * x0 + s * x1;
                spectrum[start + i + 1] = -s * x0 + c * x1;
            }
        }
        else
        {
            for (int i = width - 2; i >= 0; i--)
            {
                float x0 = spectrum[start + i];
                float x1 = spectrum[start + i + 1];
                spectrum[start + i] = c * x0 + s * x1;
                spectrum[start + i + 1] = -s * x0 + c * x1;
            }
        }
    }

    private static PulsarQuantizedBand CreateEmptyBand(int start, int width = 0)
    {
        return new PulsarQuantizedBand
        {
            Start = start,
            Width = width,
            Bits = 0,
            EnergyDb = -120.0f,
            EnergyQ = 0,
            ScaleQ = 0,
            Scale = 1.0f,
            RequestedPulseCount = 0,
            PulseCount = 0,
            PulseCap = 0,
            BandNorm = 0.0f,
            PulseNorm = 1.0f,
            SpreadDecision = SpreadNone,
            Levels = Array.Empty<int>(),
        };
    }

    private static int ScalePulseBudget(int requestedPulses, int globalGain, int width, float centerHz, float tonality, float transientScore, float opusTransientEstimate, float lowBandStability, float averagePositiveSmr, float smrDb, int spreadDecision)
    {
        if (requestedPulses <= 0) return 0;

        float gainScale = 1.0f;
        float tonalScale = 0.84f + (0.18f * tonality);
        float transientScale = 0.88f + (0.10f * Math.Clamp(transientScore, 0.0f, 1.5f)) + (0.08f * Math.Clamp(opusTransientEstimate, 0.0f, 1.5f));
        float lowBandScale = centerHz <= 260.0f ? 1.10f + (0.10f * lowBandStability) : centerHz <= 1200.0f ? 1.03f : 1.0f;
        float smrScale = 1.0f + Math.Clamp((smrDb + 3.0f) / 24.0f, 0.0f, 0.22f);
        float widthScale = 1.0f + Math.Clamp(MathF.Log2(width + 1.0f) * 0.03f, 0.0f, 0.15f);
        float spreadScale = spreadDecision switch
        {
            SpreadNone => 0.88f,
            SpreadLight => 0.96f,
            SpreadNormal => 1.02f,
            _ => 1.08f,
        };
        float fullnessScale = 1.0f + Math.Clamp(averagePositiveSmr / 18.0f, 0.0f, 0.12f);

        float scale = gainScale * tonalScale * transientScale * lowBandScale * smrScale * widthScale * spreadScale * fullnessScale;
        int pulseCount = (int)MathF.Round(requestedPulses * scale);
        if (pulseCount <= 0 && requestedPulses > 0) pulseCount = 1;
        return pulseCount;
    }

    private static int ComputePulseCap(int width, int spreadDecision, float tonality, float transientScore, float opusTransientEstimate, float averagePositiveSmr)
    {
        double spreadDensity = spreadDecision switch
        {
            SpreadNone => 96.0,
            SpreadLight => 128.0,
            SpreadNormal => 160.0,
            _ => 192.0,
        };

        double tonalRelief = 1.0 + (0.20 * tonality);
        double transientRelief = 1.0 + (0.16 * Math.Clamp(transientScore, 0.0f, 1.5f)) + (0.10 * Math.Clamp(opusTransientEstimate, 0.0f, 1.5f));
        double smrRelief = 1.0 + Math.Clamp(averagePositiveSmr / 18.0, 0.0, 0.55);
        int cap = (int)Math.Round(Math.Max(1.0, width * spreadDensity * tonalRelief * transientRelief * smrRelief));

        return Math.Clamp(cap, 1, Math.Max(2, width * 256));
    }

    private static float ComputeBandNorm(float[] spectrum, int start, int end)
    {
        int i = start;
        Vector<float> sum = Vector<float>.Zero;
        int vectorEnd = end - Vector<float>.Count;
        for (; i <= vectorEnd; i += Vector<float>.Count)
        {
            var values = new Vector<float>(spectrum, i);
            sum += values * values;
        }

        float energy = 0.0f;
        for (int lane = 0; lane < Vector<float>.Count; lane++)
        {
            energy += sum[lane];
        }

        for (; i < end; i++)
        {
            energy += spectrum[i] * spectrum[i];
        }

        return MathF.Sqrt(Math.Max(energy, 1e-12f));
    }

    private static float ComputePvqShapeWeight(int index, int width, float centerHz, float tonality, float transientScore, float opusTransientEstimate, float lowBandStability, float averagePositiveSmr, int spreadDecision)
    {
        if (width <= 1) return 1.0f;

        float position = index / (float)(width - 1);
        float centerFocus = 1.0f - MathF.Abs(position - 0.5f) * 2.0f;
        float edgeFocus = 1.0f - centerFocus;
        float lowBandBias = centerHz <= 260.0f ? 1.12f + (0.08f * lowBandStability) : centerHz <= 1200.0f ? 1.04f : 1.0f;
        float tonalBias = 1.0f + (0.16f * tonality * centerFocus);
        float transientBias = 1.0f + (0.10f * Math.Clamp(transientScore, 0.0f, 1.5f) + 0.08f * Math.Clamp(opusTransientEstimate, 0.0f, 1.5f)) * edgeFocus;
        float spreadBias = spreadDecision switch
        {
            SpreadNone => 1.10f + (0.10f * centerFocus),
            SpreadLight => 1.02f + (0.06f * centerFocus),
            SpreadNormal => 0.98f + (0.04f * edgeFocus),
            _ => 0.94f + (0.10f * edgeFocus),
        };
        float smrBias = 1.0f + Math.Clamp(averagePositiveSmr / 24.0f, 0.0f, 0.08f);
        return lowBandBias * tonalBias * transientBias * spreadBias * smrBias;
    }

    private static float ComputeSmrPulseBoost(float smrDb, int spreadDecision)
    {
        float t = Math.Clamp((smrDb + 6.0f) / 18.0f, 0.0f, 1.0f);
        float spreadBoost = spreadDecision switch
        {
            SpreadNone => 0.94f,
            SpreadLight => 0.98f,
            SpreadNormal => 1.02f,
            _ => 1.06f,
        };
        return (0.92f + (0.18f * t)) * spreadBoost;
    }

    private static int ComputeSpreadDecision(float centerHz, float tonality, float transientScore, float opusTransientEstimate, float lowBandStability, float averagePositiveSmr)
    {
        float transient = Math.Clamp((transientScore * 0.72f) + (opusTransientEstimate * 0.68f), 0.0f, 1.5f);
        float tonal = Math.Clamp(tonality, 0.0f, 1.0f);
        float lowBand = Math.Clamp(lowBandStability, 0.0f, 1.0f);
        float smr = Math.Clamp(averagePositiveSmr / 18.0f, 0.0f, 1.0f);

        float score = (0.78f * transient)
            + (0.12f * (centerHz > 2500.0f ? 1.0f : 0.0f))
            - (0.46f * tonal)
            - (0.18f * lowBand)
            - (0.10f * smr);

        if (score < 0.18f) return SpreadNone;
        if (score < 0.45f) return SpreadLight;
        if (score < 0.82f) return SpreadNormal;
        return SpreadAggressive;
    }

    private static int[] BuildSpreadOrder(int width, int spreadDecision)
    {
        int[] order = new int[width];
        if (width <= 0) return order;
        if (width == 1)
        {
            order[0] = 0;
            return order;
        }

        switch (spreadDecision)
        {
            case SpreadNone:
                for (int i = 0; i < width; i++) order[i] = i;
                break;
            case SpreadLight:
                FillCenterOutOrder(order);
                break;
            case SpreadNormal:
                FillEvenOddOrder(order);
                break;
            default:
                FillLowHighOrder(order);
                break;
        }

        return order;
    }

    private static void FillCenterOutOrder(int[] order)
    {
        int width = order.Length;
        int center = width / 2;
        int cursor = 0;
        order[cursor++] = center;
        for (int offset = 1; cursor < width; offset++)
        {
            int left = center - offset;
            int right = center + offset;
            if (left >= 0) order[cursor++] = left;
            if (cursor < width && right < width) order[cursor++] = right;
        }
    }

    private static void FillEvenOddOrder(int[] order)
    {
        int width = order.Length;
        int cursor = 0;
        for (int i = 0; i < width; i += 2) order[cursor++] = i;
        for (int i = 1; i < width; i += 2) order[cursor++] = i;
    }

    private static void FillLowHighOrder(int[] order)
    {
        int width = order.Length;
        int cursor = 0;
        int left = 0;
        int right = width - 1;
        while (left <= right)
        {
            order[cursor++] = left++;
            if (cursor < width && left <= right)
            {
                order[cursor++] = right--;
            }
        }
    }

    private static uint QuantizeLogValue(float value, float minValue, float maxValue, int totalLevels)
    {
        float clamped = Math.Clamp(value, minValue, maxValue);
        float minLog = MathF.Log2(minValue);
        float maxLog = MathF.Log2(maxValue);
        float normalized = (MathF.Log2(clamped) - minLog) / Math.Max(1e-12f, maxLog - minLog);
        return (uint)Math.Clamp((int)MathF.Round(normalized * (totalLevels - 1)), 0, totalLevels - 1);
    }

    private static float DequantizeLogValue(uint value, float minValue, float maxValue, int totalLevels)
    {
        float normalized = value / (float)Math.Max(1, totalLevels - 1);
        float minLog = MathF.Log2(minValue);
        float maxLog = MathF.Log2(maxValue);
        return MathF.Pow(2.0f, minLog + ((maxLog - minLog) * normalized));
    }

    private static int MapBandOffset(int sourceOffset, int sourceBinCount, int targetBinCount)
    {
        if (sourceBinCount <= 0 || targetBinCount <= 0) return 0;
        return Math.Clamp((int)MathF.Round(sourceOffset * (targetBinCount / (float)sourceBinCount)), 0, targetBinCount);
    }

    private static float GetBandValue(float[] values, int bandIndex)
    {
        if (values.Length == 0) return 0.0f;
        return values[Math.Min(bandIndex, values.Length - 1)];
    }

    private static float ComputeFrequencyDensityBoost(float centerHz)
    {
        if (centerHz <= 180.0f) return 1.35f;
        if (centerHz <= 1200.0f)
        {
            float t = SmoothStep((centerHz - 180.0f) / 1020.0f);
            return Lerp(1.35f, 1.10f, t);
        }
        if (centerHz <= 6000.0f)
        {
            float t = SmoothStep((centerHz - 1200.0f) / 4800.0f);
            return Lerp(1.10f, 0.95f, t);
        }

        return 0.95f;
    }

    private static float ComputeTransientWeight(float centerHz)
    {
        if (centerHz <= 1800.0f) return 0.0f;
        if (centerHz >= 5500.0f) return 1.0f;
        return SmoothStep((centerHz - 1800.0f) / 3700.0f);
    }

    private static float ComputeDeadZoneScale(float smrDb)
    {
        const float smrLow = -3.0f;
        const float smrHigh = 9.0f;
        float t = SmoothStep((smrDb - smrLow) / (smrHigh - smrLow));
        return Lerp(0.40f, 0.24f, t);
    }

    private static float SmoothStep(float value)
    {
        float t = Math.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }

    private static float Lerp(float start, float end, float t) => start + ((end - start) * t);

    private sealed class BandPrepass
    {
        public required int Start { get; init; }
        public required int End { get; init; }
        public required int Width { get; init; }
        public required float BandNorm { get; init; }
        public required uint EnergyQ { get; init; }
    }
}
