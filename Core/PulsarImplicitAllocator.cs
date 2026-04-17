using System;

#pragma warning disable CS0162

public static class PulsarImplicitAllocator
{
    // ── Opus-inspired allocation constants ──
    // Asymmetric follower slopes (dB per band) — matches CELT dynalloc_analysis
    private const float FollowerSlopeForward = 1.5f;
    private const float FollowerSlopeBackward = 2.0f;
    // Max dynalloc boost per band (dB above envelope → extra pulses)
    private const float MaxDynallocBoostDb = 4.0f;
    // Noise floor: bands this far below the frame peak are skipped
    private const float NoiseFloorOffsetDb = 52.0f;
    // Minimum band energy to be considered active
    private const float SilenceFloorDb = -96.0f;
    // Band weighting: low bands (0-7) get a 2× weight boost, high bands (≥12) get 0.5× — CELT pattern
    private const int LowBandBoostEnd = 8;
    private const int HighBandAttenuateStart = 12;
    private const double LowBandBoostFactor = 2.0;
    private const double HighBandAttenuateFactor = 0.5;

    public static int[] AllocatePulseCounts(float[] quantizedEnergyDb, int[] bandWidths, int framePulseBudget)
    {
        ArgumentNullException.ThrowIfNull(quantizedEnergyDb);
        ArgumentNullException.ThrowIfNull(bandWidths);

        return CeltAllocation.AllocatePulseCounts(quantizedEnergyDb, bandWidths, framePulseBudget);

        int bandCount = Math.Min(quantizedEnergyDb.Length, bandWidths.Length);
        if (bandCount <= 0)
        {
            return Array.Empty<int>();
        }

        framePulseBudget = Math.Max(0, framePulseBudget);
        if (framePulseBudget == 0)
        {
            return new int[bandCount];
        }

        // ── Step 1: Compute asymmetric envelope follower (Opus dynalloc pattern) ──
        float[] follower = ComputeAsymmetricFollower(quantizedEnergyDb, bandCount);

        // ── Step 2: Compute dynalloc boost per band ──
        float[] dynallocBoost = new float[bandCount];
        float framePeakDb = SilenceFloorDb;
        for (int i = 0; i < bandCount; i++)
        {
            framePeakDb = Math.Max(framePeakDb, quantizedEnergyDb[i]);
        }
        float noiseFloor = framePeakDb - NoiseFloorOffsetDb;

        for (int i = 0; i < bandCount; i++)
        {
            float deviation = quantizedEnergyDb[i] - follower[i];
            dynallocBoost[i] = Math.Clamp(deviation, 0.0f, MaxDynallocBoostDb);
        }

        // ── Step 3: Compute per-band weights with energy, width, follower boost, and band position ──
        int[] pulseCaps = new int[bandCount];
        double[] weights = new double[bandCount];
        double totalWeight = 0.0;

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            int width = Math.Max(1, bandWidths[bandIndex]);
            float energyDb = quantizedEnergyDb[bandIndex];

            // Skip bands below noise floor
            if (energyDb < noiseFloor || energyDb < SilenceFloorDb || width <= 0)
            {
                pulseCaps[bandIndex] = 0;
                weights[bandIndex] = 0.0;
                continue;
            }

            // Energy weight: linear scale from dB
            double energyWeight = Math.Pow(10.0, Math.Clamp((energyDb + 48.0f) / 20.0f, -3.5f, 5.0f));
            // Width weight: wider bands need proportionally more pulses
            double widthWeight = 1.0 + Math.Log2(width + 1.0);
            // Dynalloc boost: bands sticking above the envelope get extra weight
            double boostWeight = 1.0 + (dynallocBoost[bandIndex] / 6.0);
            // Band position weighting (CELT pattern): low bands boosted, high bands attenuated
            double positionWeight = bandIndex < LowBandBoostEnd ? LowBandBoostFactor
                : bandIndex >= HighBandAttenuateStart ? HighBandAttenuateFactor
                : 1.0;

            double weight = Math.Max(1e-9, energyWeight * widthWeight * boostWeight * positionWeight);
            weights[bandIndex] = weight;
            totalWeight += weight;

            // Compute pulse cap early for budget-aware allocation
            pulseCaps[bandIndex] = ComputePulseCap(width, framePulseBudget);
        }

        if (totalWeight <= 0.0)
        {
            return new int[bandCount];
        }

        // ── Step 4: Distribute pulses with remainder propagation ──
        int[] pulses = new int[bandCount];
        double[] remainders = new double[bandCount];
        int assigned = 0;

        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            int cap = pulseCaps[bandIndex];
            if (cap <= 0)
            {
                continue;
            }

            double exactShare = framePulseBudget * (weights[bandIndex] / totalWeight);
            int floorShare = (int)Math.Floor(exactShare);
            pulses[bandIndex] = Math.Clamp(floorShare, 0, cap);
            remainders[bandIndex] = exactShare - pulses[bandIndex];
            assigned += pulses[bandIndex];
        }

        // ── Step 5: Balance propagation (Opus pattern) — excess from capped bands flows forward ──
        double balance = 0.0;
        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            int cap = pulseCaps[bandIndex];
            if (cap <= 0) continue;

            int withBalance = pulses[bandIndex] + (int)Math.Floor(balance);
            int excess = Math.Max(0, withBalance - cap);
            pulses[bandIndex] = Math.Min(withBalance, cap);
            balance = excess + (balance - Math.Floor(balance));
            assigned = 0;
            for (int j = 0; j < bandCount; j++) assigned += pulses[j];
        }

        // ── Step 6: Distribute remaining pulses by largest remainder ──
        assigned = 0;
        for (int j = 0; j < bandCount; j++) assigned += pulses[j];
        int remaining = Math.Max(0, framePulseBudget - assigned);

        while (remaining > 0)
        {
            int bestIndex = -1;
            double bestScore = double.NegativeInfinity;

            for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
            {
                if (pulseCaps[bandIndex] <= 0 || pulses[bandIndex] >= pulseCaps[bandIndex])
                {
                    continue;
                }

                double score = remainders[bandIndex] + (weights[bandIndex] / totalWeight * 0.01);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = bandIndex;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            pulses[bestIndex]++;
            remainders[bestIndex] = 0.0;
            remaining--;
        }

        // ── Step 7: Apply Opus-style width penalty for final pulse counts ──
        for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            pulses[bandIndex] = GetOpusPulses(pulses[bandIndex], Math.Max(1, bandWidths[bandIndex]));
        }

        return pulses;
    }

    private static float[] ComputeAsymmetricFollower(float[] energyDb, int bandCount)
    {
        // Opus CELT dynalloc uses asymmetric slopes:
        //   forward:  follower[i] = min(follower[i-1] + 1.5, energy[i])
        //   backward: follower[i] = min(follower[i+1] + 2.0, energy[i])
        // Then takes the max of forward and backward at each band.
        // Plus a median-of-5 floor to prevent false peaks.

        float[] forward = new float[bandCount];
        float[] backward = new float[bandCount];

        // Forward pass
        forward[0] = energyDb[0];
        for (int i = 1; i < bandCount; i++)
        {
            forward[i] = Math.Min(forward[i - 1] + FollowerSlopeForward, energyDb[i]);
        }

        // Backward pass
        backward[bandCount - 1] = energyDb[bandCount - 1];
        for (int i = bandCount - 2; i >= 0; i--)
        {
            backward[i] = Math.Min(backward[i + 1] + FollowerSlopeBackward, energyDb[i]);
        }

        // Combine: max of forward and backward, plus median-of-5 floor
        float[] result = new float[bandCount];
        for (int i = 0; i < bandCount; i++)
        {
            float envValue = Math.Max(forward[i], backward[i]);
            float medianFloor = MedianOf5(energyDb, i, bandCount) - 1.0f;
            result[i] = Math.Max(envValue, medianFloor);
        }

        return result;
    }

    private static float MedianOf5(float[] values, int center, int count)
    {
        // Gather up to 5 values centered at 'center'
        Span<float> window = stackalloc float[5];
        int n = 0;
        for (int offset = -2; offset <= 2; offset++)
        {
            int idx = center + offset;
            if (idx >= 0 && idx < count)
            {
                window[n++] = values[idx];
            }
        }

        // Sort the small window and return median
        Span<float> used = window.Slice(0, n);
        used.Sort();
        return used[n / 2];
    }

    private static int ComputePulseCap(int width, int framePulseBudget)
    {
        int widthCap = Math.Max(1, width * 256);
        int budgetCap = Math.Max(1, framePulseBudget);
        return Math.Min(widthCap, budgetCap);
    }

    private static int GetOpusPulses(int budget, int width)
    {
        if (budget <= 0 || width <= 0)
        {
            return 0;
        }

        return Math.Min(budget, width * 24);
    }
}
