using System;

// Full port of Opus CELT quant_bands.c energy coder. Encodes per-band log2
// amplitude in three stages:
//   1) coarse: integer log2 steps, Laplace-coded, with inter-frame prediction
//      (two-pass intra/inter selection).
//   2) fine: up to extra_quant[i] raw bits per band, refining the coarse quant.
//   3) finalise: one priority-ordered bit per band from any leftover budget.
//
// Stateful across blocks via CeltEnergyState (oldEBands + delayedIntra). The
// state must be initialised once per channel at the start of a stream and
// mutated in-frame order on both encoder and decoder.
public static class CeltEnergy
{
    public const int MaxBands = 25;

    // Opus e_prob_model[LM][intra][2*band + {freq_0, decay}]. We select LM=3
    // (960-sample / 20 ms frames) which matches our block cadence best.
    // Tables are copied verbatim from quant_bands.c.
    private static readonly byte[] EProbInter =
    {
        42, 121, 96,  66, 108,  43, 111,  40, 117,  44, 123,  32, 120,  36,
        119,  33, 127,  33, 134,  34, 139,  21, 147,  23, 152,  20, 158,  25,
        154,  26, 166,  21, 173,  16, 184,  13, 184,  10, 150,  13, 139,  15,
    };

    private static readonly byte[] EProbIntra =
    {
        22, 178,  63, 114,  74,  82,  84,  83,  92,  82, 103,  62,  96,  72,
        96,  67, 101,  73, 107,  72, 113,  55, 118,  52, 125,  52, 118,  52,
        117,  55, 135,  49, 137,  39, 157,  32, 145,  29,  97,  33,  77,  40,
    };

    // pred_coef[3] = 16384/32768 = 0.5, beta_coef[3] = 6554/32768 ≈ 0.2,
    // beta_intra = 4915/32768 ≈ 0.15. LM=3 entries.
    private const float PredCoef = 16384.0f / 32768.0f;
    private const float BetaCoef = 6554.0f / 32768.0f;
    private const float BetaIntra = 4915.0f / 32768.0f;

    // Opus eMeans[] in float form. Index 0..24 used; extra HF bands use interpolated values.
    private static readonly float[] EMeans =
    {
        6.4375f, 6.2500f, 5.7500f, 5.3125f, 5.0625f,
        4.8125f, 4.5000f, 4.3750f, 4.8750f, 4.6875f,
        4.5625f, 4.4375f, 4.8750f, 4.6250f, 4.3125f,
        4.5000f, 4.3750f, 4.6250f, 4.7500f, 4.4375f,
        3.7500f, 3.5000f, 3.2500f, 3.0000f, 2.7500f,
    };

    private static readonly byte[] SmallEnergyIcdf = { 2, 1, 0 };

    public sealed class State
    {
        public float[] OldEBands;
        public float DelayedIntra;

        public State(int bandCount)
        {
            OldEBands = new float[bandCount];
            DelayedIntra = 1.0f;
        }
    }

    /// <summary>
    /// Encode coarse energy for one block. Returns whether intra was chosen.
    /// <paramref name="eBands"/> contains log2-amplitude per band; on return
    /// <paramref name="error"/> contains the residual (pre-fine) and state is
    /// updated with the decoded value (encoder and decoder must agree).
    /// </summary>
    public static bool QuantCoarse(
        PulsarRangeEncoder enc,
        int start, int end,
        ReadOnlySpan<float> eBands,
        State state,
        Span<float> error,
        int budgetBits,
        bool forceIntra)
    {
        // Mean-subtract to match Opus domain (oldEBands stores x - eMeans).
        Span<float> xMinusMean = stackalloc float[MaxBands];
        for (int i = start; i < end; i++) xMinusMean[i] = eBands[i] - EMeans[i];

        // Try intra first into scratch, then inter into real state; pick shorter.
        var scratchOld = new float[MaxBands];
        var scratchErr = new float[MaxBands];
        Array.Copy(state.OldEBands, scratchOld, MaxBands);

        // Quick decision: if forceIntra or the leaky predictor has drifted far,
        // pick intra directly and skip two-pass to avoid wasting bits.
        bool useIntra = forceIntra || state.DelayedIntra > 2 * (end - start);

        // Emit the intra flag (3-bit logp — matches Opus ec_enc_bit_logp(...,3)).
        enc.EncodeBitLogp(useIntra, 3);

        EncodeCoarseImpl(enc, start, end, xMinusMean, useIntra ? scratchOld : state.OldEBands,
            error, budgetBits, useIntra);

        // Track prediction-gain decay for the next-block intra decision.
        if (useIntra)
        {
            Array.Copy(scratchOld, state.OldEBands, MaxBands);
            state.DelayedIntra = 0.0f;
        }
        else
        {
            float predGain = PredCoef * PredCoef;
            state.DelayedIntra = predGain * state.DelayedIntra + 1.0f;
        }

        _ = scratchErr;
        return useIntra;
    }

    private static void EncodeCoarseImpl(
        PulsarRangeEncoder enc,
        int start, int end,
        ReadOnlySpan<float> eBands,
        float[] oldEBands,
        Span<float> error,
        int budgetBits,
        bool intra)
    {
        byte[] probModel = intra ? EProbIntra : EProbInter;
        float coef = intra ? 0.0f : PredCoef;
        float beta = intra ? BetaIntra : BetaCoef;
        float prev = 0.0f;

        for (int i = start; i < end; i++)
        {
            float x = eBands[i];
            float oldE = Math.Max(-9.0f, oldEBands[i]);
            float f = x - coef * oldE - prev;
            int qi = (int)MathF.Floor(0.5f + f);

            int tellBits = enc.TellBits;
            int bitsLeft = budgetBits - tellBits - 3 * (end - i);
            if (i != start && bitsLeft < 30)
            {
                if (bitsLeft < 24) qi = Math.Min(1, qi);
                if (bitsLeft < 16) qi = Math.Max(-1, qi);
            }

            int budgetMinusTell = budgetBits - tellBits;
            if (budgetMinusTell >= 15)
            {
                int pi = 2 * Math.Min(i, 20);
                qi = enc.LaplaceEncode(qi, (uint)probModel[pi] << 7, probModel[pi + 1] << 6);
            }
            else if (budgetMinusTell >= 2)
            {
                qi = Math.Max(-1, Math.Min(qi, 1));
                int sym = (2 * qi) ^ -(qi < 0 ? 1 : 0);
                enc.EncodeIcdfSymbol(sym, SmallEnergyIcdf, 15);
            }
            else if (budgetMinusTell >= 1)
            {
                enc.EncodeBitLogp(-qi == 1, 1);
                qi = Math.Min(0, qi);
            }
            else
            {
                qi = -1;
            }

            error[i] = f - qi;
            float q = qi;
            float tmp = coef * oldE + prev + q;
            oldEBands[i] = tmp;
            prev = prev + q - beta * q;
        }
    }

    public static bool UnquantCoarse(
        PulsarRangeDecoder dec,
        int start, int end,
        State state,
        int budgetBits)
    {
        bool intra = dec.DecodeBitLogp(3);
        byte[] probModel = intra ? EProbIntra : EProbInter;
        float coef = intra ? 0.0f : PredCoef;
        float beta = intra ? BetaIntra : BetaCoef;
        float prev = 0.0f;

        for (int i = start; i < end; i++)
        {
            int qi;
            int tellBits = dec.TellBits;
            int budgetMinusTell = budgetBits - tellBits;
            if (budgetMinusTell >= 15)
            {
                int pi = 2 * Math.Min(i, 20);
                qi = dec.LaplaceDecode((uint)probModel[pi] << 7, probModel[pi + 1] << 6);
            }
            else if (budgetMinusTell >= 2)
            {
                int sym = dec.DecodeIcdfSymbol(SmallEnergyIcdf, 15);
                qi = (sym >> 1) ^ -(sym & 1);
            }
            else if (budgetMinusTell >= 1)
            {
                qi = -(dec.DecodeBitLogp(1) ? 1 : 0);
            }
            else
            {
                qi = -1;
            }

            float oldE = Math.Max(-9.0f, state.OldEBands[i]);
            float tmp = coef * oldE + prev + qi;
            state.OldEBands[i] = tmp;
            prev = prev + qi - beta * qi;
        }

        if (intra) state.DelayedIntra = 0.0f;
        else state.DelayedIntra = PredCoef * PredCoef * state.DelayedIntra + 1.0f;
        return intra;
    }

    /// <summary>
    /// Encode fine energy bits. extraQuant[i] in {0..7}. Updates state.OldEBands
    /// and error in place so both sides converge to the same reconstructed logE.
    /// </summary>
    public static void QuantFine(
        PulsarRangeEncoder enc,
        int start, int end,
        State state,
        Span<float> error,
        ReadOnlySpan<int> extraQuant)
    {
        for (int i = start; i < end; i++)
        {
            int extra = extraQuant[i];
            if (extra <= 0) continue;
            int levels = 1 << extra;
            int q2 = (int)MathF.Floor((error[i] + 0.5f) * levels);
            if (q2 < 0) q2 = 0;
            if (q2 > levels - 1) q2 = levels - 1;
            enc.EncodeBits((uint)q2, extra);
            float offset = (q2 + 0.5f) / levels - 0.5f;
            state.OldEBands[i] += offset;
            error[i] -= offset;
        }
    }

    public static void UnquantFine(
        PulsarRangeDecoder dec,
        int start, int end,
        State state,
        ReadOnlySpan<int> extraQuant)
    {
        for (int i = start; i < end; i++)
        {
            int extra = extraQuant[i];
            if (extra <= 0) continue;
            int levels = 1 << extra;
            int q2 = (int)dec.DecodeBits(extra);
            float offset = (q2 + 0.5f) / levels - 0.5f;
            state.OldEBands[i] += offset;
        }
    }

    /// <summary>Reconstruct log2-amplitude per band from state.OldEBands.</summary>
    public static void ToLogAmplitude(State state, int start, int end, Span<float> logAmp)
    {
        for (int i = start; i < end; i++)
            logAmp[i] = state.OldEBands[i] + EMeans[i];
    }
}
