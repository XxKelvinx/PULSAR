using System;

/*
   Allocation glue derived from the Opus CELT rate model (celt/rate.c,
   celt/rate.h, celt/modes.c). This maps a bit budget to CELT pulse counts
   with the same pulse cache and bits2pulses/pulses2bits tables used by CELT.

   Port of clt_compute_allocation() and interp_bits2pulses() from Opus.
   Reference: opus-main/celt/rate.c
*/

public static class CeltAllocation
{
    private const int AllocSteps = 6;
    private const int QThetaOffset = 4;
    private const int MaxFineBits = 8;
    // Max fine bits for absorbing remaining budget after PVQ caps
    private const int MaxFineBitsTotal = 24;
    private const float SilenceFloorDb = -96.0f;
    private const float NoiseFloorOffsetDb = 52.0f;

    /// <summary>
    /// Opus-faithful per-band bit allocation (port of clt_compute_allocation).
    /// Returns per-band PVQ bit budgets in Q3 and fine energy bit counts.
    /// Bits that can't be used by PVQ (above cap) go to fine energy refinement.
    /// </summary>
    public static (int[] BitsQ3, int[] FineBits) ComputeAllocation(CeltMode mode, int totalBitsQ3, int LM, int allocTrim = 5, int endBand = -1)
    {
        ArgumentNullException.ThrowIfNull(mode);
        int nbEBands = mode.NbEBands;
        if (nbEBands <= 0 || totalBitsQ3 <= 0)
            return (new int[Math.Max(0, nbEBands)], new int[Math.Max(0, nbEBands)]);

        int C = 1; // mono — each channel allocated independently
        int start = 0;
        int end = endBand > 0 && endBand < nbEBands ? endBand : nbEBands;
        int len = nbEBands;

        int total = Math.Max(0, totalBitsQ3);
        int allocFloor = C << CeltRate.BitRes;

        // Per-band caps, thresholds, and trim offsets — exactly as Opus rate.c
        int[] cap = ComputeCaps(mode, LM, C);
        int[] thresh = new int[len];
        int[] trimOffset = new int[len];
        for (int j = start; j < end; j++)
        {
            int N = mode.EBands[j + 1] - mode.EBands[j];
            // Below this threshold, we won't allocate PVQ bits
            thresh[j] = Math.Max(C << CeltRate.BitRes, (3 * N * (1 << LM) * (1 << CeltRate.BitRes)) >> 4);
            // Tilt of the allocation curve — matches Opus rate.c:
            // (end-j-1) gives high bands tiltFactor=0, low bands get large factor.
            // Positive (allocTrim-5-LM) → more bits to low bands.
            // Normalized to 21-band range so extra bands don't amplify tilt.
            int tiltFactor = (end - j - 1) * 20 / Math.Max(1, end - 1);
            trimOffset[j] = C * N * (allocTrim - 5 - LM) * tiltFactor *
                (1 << (LM + CeltRate.BitRes)) >> 6;
            if (N * (1 << LM) == 1)
                trimOffset[j] -= C << CeltRate.BitRes;
        }

        // Binary search for the right alloc vector interpolation bracket
        int nbAllocVectors = CeltMode.BitAllocSize;
        int lo = 1;
        int hi = nbAllocVectors - 1;
        do
        {
            int done = 0;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int N = mode.EBands[j + 1] - mode.EBands[j];
                int bitsj = C * N * GetAllocVector(mid, j, nbEBands) * (1 << LM) >> 2;
                if (bitsj > 0)
                    bitsj = Math.Max(0, bitsj + trimOffset[j]);
                if (bitsj >= thresh[j] || done != 0)
                {
                    done = 1;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else
                {
                    if (bitsj >= allocFloor)
                        psum += allocFloor;
                }
            }
            if (psum > total)
                hi = mid - 1;
            else
                lo = mid + 1;
        } while (lo <= hi);
        hi = lo--;

        // Compute bits1 and bits2 for the two bracketing alloc vectors
        int[] bits1 = new int[len];
        int[] bits2 = new int[len];
        for (int j = start; j < end; j++)
        {
            int N = mode.EBands[j + 1] - mode.EBands[j];
            int bits1j = C * N * GetAllocVector(lo, j, nbEBands) * (1 << LM) >> 2;
            int bits2j = hi >= nbAllocVectors
                ? cap[j]
                : C * N * GetAllocVector(hi, j, nbEBands) * (1 << LM) >> 2;
            if (bits1j > 0)
                bits1j = Math.Max(0, bits1j + trimOffset[j]);
            if (bits2j > 0)
                bits2j = Math.Max(0, bits2j + trimOffset[j]);
            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }

        // interp_bits2pulses: interpolate and distribute bits
        int[] pvqBits = InterpBits2Pulses(mode, start, end, bits1, bits2, thresh, cap,
            total, allocFloor, C, LM);

        // Distribute remaining bits as fine energy refinement (like Opus)
        int pvqTotal = 0;
        for (int j = 0; j < len; j++) pvqTotal += pvqBits[j];
        int remaining = total - pvqTotal;

        int[] fineBits = new int[len];
        if (remaining > 0)
        {
            // Round-robin: give 1 fine bit (= 8 Q3) to each band, up to MAX_FINE_BITS per band
            bool progress = true;
            while (remaining > 0 && progress)
            {
                progress = false;
                for (int j = start; j < end && remaining > 0; j++)
                {
                    if (fineBits[j] < MaxFineBitsTotal)
                    {
                        int cost = 1 << CeltRate.BitRes; // 8 Q3 = 1 bit
                        if (cost <= remaining)
                        {
                            fineBits[j]++;
                            remaining -= cost;
                            progress = true;
                        }
                    }
                }
            }
        }

        return (pvqBits, fineBits);
    }

    /// <summary>
    /// Compute an adaptive alloc trim from the spectral tilt of reconstructed band energies.
    /// Mirrors Opus's alloc_trim_analysis: measures slope of energy across bands.
    /// Lower trim (0-4) biases bits to low bands (tonal/speech), higher (6-10) to high bands.
    /// Both encoder and decoder can compute this identically from reconstructed energies.
    /// </summary>
    public static int ComputeAllocTrim(float[] bandEnergyDb, int bandCount, int endBand, int LM = 0)
    {
        if (bandEnergyDb == null || bandCount < 2 || endBand < 2)
            return 5; // default neutral

        int n = Math.Min(bandCount, endBand);

        // Compute spectral tilt as weighted linear regression slope of energy vs band index
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        int activeBands = 0;
        for (int i = 0; i < n; i++)
        {
            float e = bandEnergyDb[i];
            if (e < -90f) continue; // skip silence
            sumX += i;
            sumY += e;
            sumXY += i * e;
            sumXX += i * i;
            activeBands++;
        }
        if (activeBands < 3) return 5;

        double denom = activeBands * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-9) return 5;
        double slope = (activeBands * sumXY - sumX * sumY) / denom;

        // Map slope to trim: negative slope (energy falling with freq) → higher trim.
        // trimOffset uses (trim - 5 - LM), so the effective tilt depends on LM.
        // Target: effective tilt of -1 to 0 for typical falling-spectrum music,
        // matching Opus CELT behavior at its native LM=3.
        int trim = (int)Math.Round(4.0 + LM * 0.5 - slope * 1.5);
        // Keep capped at reasonable range
        return Math.Clamp(trim, 2, 10);
    }

    /// <summary>
    /// Compute per-band dynalloc boosts (in Q3 bits) from reconstructed band energies.
    /// Bands that stick above the spectral envelope get extra bits.
    /// Uses asymmetric envelope follower like Opus's dynalloc_analysis.
    /// Both encoder and decoder compute this identically from reconstructed energies.
    /// </summary>
    public static int[] ComputeDynallocBoosts(float[] bandEnergyDb, int bandCount, int endBand, int totalBitsQ3)
    {
        int n = Math.Min(bandCount, endBand);
        int[] boosts = new int[bandCount];
        if (bandEnergyDb == null || n < 2 || totalBitsQ3 <= 0)
            return boosts;

        // Asymmetric envelope follower (Opus pattern)
        float[] follower = new float[n];
        follower[0] = bandEnergyDb[0];
        // Forward pass: slow rise (1.5 dB/band)
        for (int i = 1; i < n; i++)
            follower[i] = Math.Max(bandEnergyDb[i], follower[i - 1] - 1.5f);
        // Backward pass: fast fall (2.0 dB/band)
        for (int i = n - 2; i >= 0; i--)
            follower[i] = Math.Max(follower[i], follower[i + 1] - 2.0f);

        // Cap total boosts at 1/8 of total budget
        int maxTotalBoost = totalBitsQ3 >> 3;
        int totalBoost = 0;

        for (int i = 0; i < n; i++)
        {
            float deviation = bandEnergyDb[i] - follower[i];
            if (deviation > 1.0f) // only boost bands clearly above envelope
            {
                // Convert dB deviation to Q3 bits: ~4 Q3 bits per dB of boost
                int boost = (int)(deviation * 4.0f);
                boost = Math.Min(boost, 16 * 8); // max 16 bits per band
                boosts[i] = boost;
                totalBoost += boost;
            }
        }

        // Scale down if total exceeds budget cap
        if (totalBoost > maxTotalBoost && totalBoost > 0)
        {
            float scale = (float)maxTotalBoost / totalBoost;
            for (int i = 0; i < n; i++)
                boosts[i] = (int)(boosts[i] * scale);
        }

        return boosts;
    }

    /// <summary>
    /// Allocation with dynalloc boosts: applies per-band boosts after the base allocation.
    /// Boost bits come from the total budget (pre-subtracted).
    /// </summary>
    public static (int[] BitsQ3, int[] FineBits) ComputeAllocationWithBoosts(
        CeltMode mode, int totalBitsQ3, int LM, int allocTrim,
        int[] dynBoosts, int endBand = -1)
    {
        // Sum boosts and subtract from total budget for base allocation
        int nbEBands = mode.NbEBands;
        int totalBoost = 0;
        if (dynBoosts != null)
        {
            for (int i = 0; i < Math.Min(dynBoosts.Length, nbEBands); i++)
                totalBoost += dynBoosts[i];
        }
        int baseBudget = Math.Max(nbEBands * 8, totalBitsQ3 - totalBoost);

        var (bitsQ3, fineBits) = ComputeAllocation(mode, baseBudget, LM, allocTrim, endBand);

        // Apply boosts on top
        if (dynBoosts != null)
        {
            int[] cap = ComputeCaps(mode, LM, 1);
            for (int i = 0; i < Math.Min(dynBoosts.Length, bitsQ3.Length); i++)
            {
                if (dynBoosts[i] > 0)
                    bitsQ3[i] = Math.Min(bitsQ3[i] + dynBoosts[i], cap[i]);
            }
        }

        return (bitsQ3, fineBits);
    }

    /// <summary>
    /// Port of Opus interp_bits2pulses() from rate.c.
    /// </summary>
    private static int[] InterpBits2Pulses(CeltMode mode, int start, int end,
        int[] bits1, int[] bits2, int[] thresh, int[] cap,
        int total, int allocFloor, int C, int LM)
    {
        int[] bits = new int[mode.NbEBands];

        // Binary search for interpolation factor (0..64)
        int lo = 0;
        int hi = 1 << AllocSteps;
        for (int iter = 0; iter < AllocSteps; iter++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            int done = 0;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (int)((long)mid * bits2[j] >> AllocSteps);
                if (tmp >= thresh[j] || done != 0)
                {
                    done = 1;
                    psum += Math.Min(tmp, cap[j]);
                }
                else
                {
                    if (tmp >= allocFloor)
                        psum += allocFloor;
                }
            }
            if (psum > total)
                hi = mid;
            else
                lo = mid;
        }

        // Compute final per-band allocation with the chosen interpolation point
        int pTotal = 0;
        {
            int done = 0;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (int)((long)lo * bits2[j] >> AllocSteps);
                if (tmp < thresh[j] && done == 0)
                {
                    tmp = tmp >= allocFloor ? allocFloor : 0;
                }
                else
                {
                    done = 1;
                }
                tmp = Math.Min(tmp, cap[j]);
                bits[j] = tmp;
                pTotal += tmp;
            }
        }

        // Distribute leftover bits evenly across coded bands (Opus balance)
        int left = total - pTotal;
        if (left > 0)
        {
            int codedBins = 0;
            for (int j = start; j < end; j++)
            {
                if (bits[j] > 0)
                    codedBins += mode.EBands[j + 1] - mode.EBands[j];
            }
            if (codedBins > 0)
            {
                int percoeff = left / codedBins;
                int rem = left - percoeff * codedBins;
                int acc = 0;
                for (int j = start; j < end; j++)
                {
                    if (bits[j] <= 0) continue;
                    int N = mode.EBands[j + 1] - mode.EBands[j];
                    int extra = percoeff * N + Math.Min(N, Math.Max(0, rem - acc));
                    acc += N;
                    bits[j] = Math.Min(bits[j] + extra, cap[j]);
                }
            }
        }

        return bits;
    }

    /// <summary>
    /// Compute per-band caps: the maximum number of bits (in Q3) that each band
    /// can usefully consume through the full recursive split tree.
    /// Uses the leaf-level cache capacity and multiplies up through splits.
    /// </summary>
    public static int[] ComputeCaps(CeltMode mode, int LM, int C)
    {
        int nbEBands = mode.NbEBands;
        int[] caps = new int[nbEBands];

        for (int j = 0; j < nbEBands; j++)
        {
            int baseBW = mode.EBands[j + 1] - mode.EBands[j];
            caps[j] = C * ComputeRecursiveCap(mode, j, baseBW << LM, LM);
        }

        return caps;
    }

    /// <summary>
    /// Recursively compute the maximum bits (Q3) a band can use through splits.
    /// Mirrors the actual split structure in CeltBand.QuantPartitionEncode.
    /// </summary>
    private static int ComputeRecursiveCap(CeltMode mode, int band, int N, int lm)
    {
        if (N <= 0) return 0;

        // Leaf condition: LM==-1 or N<=2
        if (lm == -1 || N <= 2)
        {
            // Look up cache at leaf level
            int cacheLm = lm + 1;
            if (cacheLm < 0) cacheLm = 0;
            int idx = cacheLm * mode.NbEBands + band;
            if ((uint)idx < (uint)mode.PulseCache.Index.Length && mode.PulseCache.Index[idx] >= 0)
            {
                int off = mode.PulseCache.Index[idx];
                int maxPseudo = mode.PulseCache.Bits[off];
                return maxPseudo > 0 ? (mode.PulseCache.Bits[off + maxPseudo] + 1) << CeltRate.BitRes : 0;
            }
            return 0;
        }

        // Split: two halves plus theta overhead
        int halfN = N >> 1;
        int halfCap = ComputeRecursiveCap(mode, band, halfN, lm - 1);
        // Theta overhead: approximately log2(N) + 3 bits per split level
        int thetaOverheadQ3 = Math.Max(8, (mode.LogN[band] + (lm << CeltRate.BitRes)) >> 1);
        return 2 * halfCap + thetaOverheadQ3;
    }

    /// <summary>
    /// Get alloc vector value. Maps band index to the standard 21-band Opus table.
    /// Bands 0-19 are identical to Opus; bands 20+ (HF extension) use Opus band 20's weight.
    /// </summary>
    private static int GetAllocVector(int vectorIndex, int band, int nbEBands)
    {
        int opusBand = Math.Min(band, 20);
        return CeltMode.StandardBandAllocation[vectorIndex * 21 + opusBand];
    }

    // ---- Legacy API kept for compatibility ----

    public static int[] AllocateBandBitsQ3(float[] quantizedEnergyDb, int[] bandWidths, int totalFrameBits,
        CeltMode? mode = null, int LM = 0)
    {
        ArgumentNullException.ThrowIfNull(quantizedEnergyDb);
        ArgumentNullException.ThrowIfNull(bandWidths);

        int bandCount = Math.Min(quantizedEnergyDb.Length, bandWidths.Length);
        if (bandCount <= 0 || totalFrameBits <= 0)
            return new int[Math.Max(0, bandCount)];

        if (mode != null)
        {
            int totalQ3 = totalFrameBits << CeltRate.BitRes;
            return ComputeAllocation(mode, totalQ3, LM).BitsQ3;
        }

        return AllocateBandBitsQ3Legacy(quantizedEnergyDb, bandWidths, totalFrameBits);
    }

    private static int[] AllocateBandBitsQ3Legacy(float[] quantizedEnergyDb, int[] bandWidths, int totalFrameBits)
    {
        int bandCount = Math.Min(quantizedEnergyDb.Length, bandWidths.Length);
        int totalQ3 = totalFrameBits << CeltRate.BitRes;

        int[] widths = new int[bandCount];
        for (int i = 0; i < bandCount; i++)
            widths[i] = Math.Max(1, bandWidths[i]);

        double[] weights = BuildWeights(quantizedEnergyDb, widths, bandCount);
        double totalWeight = 0.0;
        for (int i = 0; i < bandCount; i++)
            totalWeight += weights[i];

        int[] bitsQ3 = new int[bandCount];
        if (totalWeight <= 0.0)
            return bitsQ3;

        const int minBandQ3 = 8 << CeltRate.BitRes;
        int floorSum = 0;
        for (int band = 0; band < bandCount; band++)
        {
            if (weights[band] > 0.0)
            {
                bitsQ3[band] = minBandQ3;
                floorSum += minBandQ3;
            }
        }

        int distributableQ3 = Math.Max(0, totalQ3 - floorSum);
        double[] remainders = new double[bandCount];
        int assignedQ3 = 0;

        for (int band = 0; band < bandCount; band++)
        {
            if (weights[band] <= 0.0) continue;
            double exactQ3 = distributableQ3 * (weights[band] / totalWeight);
            int floorQ3 = Math.Max(0, (int)Math.Floor(exactQ3));
            remainders[band] = exactQ3 - floorQ3;
            bitsQ3[band] += floorQ3;
            assignedQ3 += floorQ3;
        }

        int leftoverQ3 = distributableQ3 - assignedQ3;
        while (leftoverQ3 > 0)
        {
            int bestBand = -1;
            double bestScore = double.NegativeInfinity;
            for (int band = 0; band < bandCount; band++)
            {
                if (weights[band] <= 0.0) continue;
                double score = remainders[band] + (weights[band] / totalWeight * 0.001);
                if (score > bestScore) { bestScore = score; bestBand = band; }
            }
            if (bestBand < 0) break;
            bitsQ3[bestBand]++;
            remainders[bestBand] = 0.0;
            leftoverQ3--;
        }

        return bitsQ3;
    }

    public static int[] AllocatePulseCounts(float[] quantizedEnergyDb, int[] bandWidths, int frameBitBudget)
    {
        ArgumentNullException.ThrowIfNull(quantizedEnergyDb);
        ArgumentNullException.ThrowIfNull(bandWidths);

        int bandCount = Math.Min(quantizedEnergyDb.Length, bandWidths.Length);
        if (bandCount <= 0 || frameBitBudget <= 0)
            return new int[Math.Max(0, bandCount)];

        int[] widths = new int[bandCount];
        for (int i = 0; i < bandCount; i++)
            widths[i] = Math.Max(1, bandWidths[i]);

        CeltMode mode = CeltMode.CreateFromBandWidths(widths);
        double[] weights = BuildWeights(quantizedEnergyDb, widths, bandCount);
        double totalWeight = 0.0;
        for (int i = 0; i < bandCount; i++)
            totalWeight += weights[i];

        if (totalWeight <= 0.0)
            return new int[bandCount];

        int[] pulses = new int[bandCount];
        double[] remainders = new double[bandCount];
        int assignedBits = 0;

        for (int band = 0; band < bandCount; band++)
        {
            if (weights[band] <= 0.0) continue;
            double exactBits = frameBitBudget * (weights[band] / totalWeight);
            int wholeBits = Math.Max(0, (int)Math.Floor(exactBits));
            remainders[band] = exactBits - wholeBits;
            assignedBits += wholeBits;
            pulses[band] = BitsToSafePulses(mode, band, wholeBits);
        }

        int remainingBits = Math.Max(0, frameBitBudget - assignedBits);
        while (remainingBits > 0)
        {
            int bestBand = -1;
            double bestScore = double.NegativeInfinity;
            for (int band = 0; band < bandCount; band++)
            {
                if (weights[band] <= 0.0) continue;
                double score = remainders[band] + (weights[band] / totalWeight * 0.01);
                if (score > bestScore) { bestScore = score; bestBand = band; }
            }
            if (bestBand < 0) break;
            int currentBits = CeltRate.Pulses2Bits(mode, bestBand, 0, PulsesToPseudo(pulses[bestBand]));
            pulses[bestBand] = BitsToSafePulses(mode, bestBand, currentBits + 1);
            remainders[bestBand] = 0.0;
            remainingBits--;
        }

        return pulses;
    }

    private static int BitsToSafePulses(CeltMode mode, int band, int wholeBits)
    {
        if (wholeBits <= 0) return 0;
        int bitsQ3 = Math.Max(1, wholeBits << CeltRate.BitRes);
        int pseudo = CeltRate.Bits2Pulses(mode, band, 0, bitsQ3);
        return CeltRate.GetPulses(pseudo);
    }

    private static int PulsesToPseudo(int pulses)
    {
        int best = 0;
        for (int i = 1; i <= CeltRate.MaxPseudo; i++)
        {
            if (CeltRate.GetPulses(i) <= pulses) best = i;
        }
        return best;
    }

    private static double[] BuildWeights(float[] energyDb, int[] widths, int bandCount)
    {
        var weights = new double[bandCount];
        float peakDb = SilenceFloorDb;
        for (int i = 0; i < bandCount; i++)
            peakDb = Math.Max(peakDb, energyDb[i]);

        float noiseFloor = peakDb - NoiseFloorOffsetDb;
        for (int band = 0; band < bandCount; band++)
        {
            if (energyDb[band] < noiseFloor || energyDb[band] < SilenceFloorDb) continue;
            int opusBand = Math.Clamp((band * (CeltMode.StandardEBands5Ms.Length - 2)) / Math.Max(1, bandCount - 1), 0, CeltMode.StandardEBands5Ms.Length - 2);
            double opusShape = 1.0 + (CeltMode.StandardBandAllocation[(7 * (CeltMode.StandardEBands5Ms.Length - 1)) + opusBand] / 200.0);
            double energyWeight = Math.Pow(10.0, Math.Clamp((energyDb[band] + 48.0f) / 20.0f, -3.5f, 5.0f));
            double widthWeight = Math.Sqrt(Math.Max(1, widths[band]));
            weights[band] = Math.Max(1e-9, opusShape * energyWeight * widthWeight);
        }

        return weights;
    }
}
