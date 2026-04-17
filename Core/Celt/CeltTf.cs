using System;

/// <summary>
/// Temporal folding (TF) analysis, encoding, and decoding — faithful port of Opus CELT's
/// tf_analysis(), tf_encode(), and tf_decode() from celt_encoder.c / celt_decoder.c.
/// 
/// Positive tf_change means better frequency resolution (longer effective window),
/// negative tf_change means better time resolution (shorter effective window).
/// </summary>
public static class CeltTf
{
    /// <summary>
    /// Opus tf_select_table[4][8].  Index: [LM][4*isTransient + 2*tf_select + tf_res_i].
    /// LM: 0=2.5ms, 1=5ms, 2=10ms, 3=20ms.
    /// </summary>
    public static readonly int[,] TfSelectTable = new int[4, 8]
    {
        // isTransient=0           isTransient=1
        {  0, -1,  0, -1,          0, -1,  0, -1 }, // LM=0 (2.5 ms)
        {  0, -1,  0, -2,          1,  0,  1, -1 }, // LM=1 (5 ms)
        {  0, -2,  0, -3,          2,  0,  1, -1 }, // LM=2 (10 ms)
        {  0, -2,  0, -3,          3,  0,  1, -1 }, // LM=3 (20 ms)
    };

    /// <summary>
    /// L1-based metric for Haar decomposition comparison.  Port of Opus l1_metric().
    /// </summary>
    private static float L1Metric(ReadOnlySpan<float> tmp, int N, int LM, float bias)
    {
        float L1 = 0;
        for (int i = 0; i < N; i++)
            L1 += MathF.Abs(tmp[i]);
        // When in doubt, prefer good freq resolution
        L1 += LM * bias * L1;
        return L1;
    }

    /// <summary>
    /// In-place Haar wavelet transform (matches Opus haar1() and CeltBand.Haar1).
    /// N0 is halved internally — pass the full count, not half.
    /// </summary>
    private static void Haar1(Span<float> X, int N0, int stride)
    {
        const float S = 0.7071067811865476f;
        N0 >>= 1; // Opus convention: N0 is the full count, halved for pair processing
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < N0; j++)
            {
                int idx0 = stride * 2 * j + i;
                int idx1 = stride * (2 * j + 1) + i;
                float a = S * X[idx0];
                float b = S * X[idx1];
                X[idx0] = a + b;
                X[idx1] = a - b;
            }
        }
    }

    /// <summary>
    /// Encoder-side TF analysis — port of Opus tf_analysis().
    /// Determines per-band tf_res[] (0 or 1) and the global tf_select.
    /// </summary>
    /// <param name="bandOffsets">Band offset table (eBands scaled by M), length = nbBands+1.</param>
    /// <param name="nbBands">Number of active bands to analyse.</param>
    /// <param name="isTransient">True if this block is a short/transient block.</param>
    /// <param name="tfRes">Output: per-band tf resolution flag (0 or 1), length >= nbBands.</param>
    /// <param name="lambda">Lagrange multiplier for switching cost (higher = less switching).</param>
    /// <param name="X">Normalised MDCT spectrum (all bands concatenated).</param>
    /// <param name="N0">Total spectrum length.</param>
    /// <param name="LM">Log2 of the overlap multiplier (0-3).</param>
    /// <param name="tfEstimate">Tonality estimate [0..1], 0 = tonal, 1 = noisy. Use 0 if unavailable.</param>
    /// <param name="importance">Per-band importance weights, length >= nbBands. null → uniform (1).</param>
    /// <returns>tf_select (0 or 1).</returns>
    public static int TfAnalysis(
        int[] bandOffsets, int nbBands, bool isTransient,
        int[] tfRes, int lambda, float[] X, int N0, int LM,
        float tfEstimate, int[]? importance)
    {
        int isTrans = isTransient ? 1 : 0;
        // bias: slight preference for frequency resolution when tonal
        float bias = 0.04f * Math.Max(-0.25f, 0.5f - tfEstimate);

        int[] metric = new int[nbBands];
        // Temp buffers for Haar decomposition (sized to largest band)
        int maxBandN = 0;
        for (int i = 0; i < nbBands; i++)
        {
            int n = bandOffsets[i + 1] - bandOffsets[i];
            if (n > maxBandN) maxBandN = n;
        }
        float[] tmp = new float[maxBandN];
        float[] tmp1 = new float[maxBandN];

        for (int i = 0; i < nbBands; i++)
        {
            int N = bandOffsets[i + 1] - bandOffsets[i];
            int narrow = (N >> LM) == 1 ? 1 : 0; // band is only 1 bin at base resolution

            // Copy band data to tmp
            X.AsSpan(bandOffsets[i], N).CopyTo(tmp.AsSpan(0, N));

            float bestL1 = L1Metric(tmp.AsSpan(0, N), N, isTransient ? LM : 0, bias);
            int bestLevel = 0;

            // Check -1 case for transients (one more Haar level)
            if (isTransient && narrow == 0)
            {
                tmp.AsSpan(0, N).CopyTo(tmp1.AsSpan(0, N));
                Haar1(tmp1.AsSpan(0, N), N >> LM, 1 << LM);
                float L1 = L1Metric(tmp1.AsSpan(0, N), N, LM + 1, bias);
                if (L1 < bestL1)
                {
                    bestL1 = L1;
                    bestLevel = -1;
                }
            }

            int kMax = LM + ((isTransient || narrow != 0) ? 0 : 1);
            for (int k = 0; k < kMax; k++)
            {
                int B = isTransient ? (LM - k - 1) : (k + 1);
                Haar1(tmp.AsSpan(0, N), N >> k, 1 << k);
                float L1 = L1Metric(tmp.AsSpan(0, N), N, B, bias);
                if (L1 < bestL1)
                {
                    bestL1 = L1;
                    bestLevel = k + 1;
                }
            }

            // metric is in Q1 (multiplied by 2) to allow half-point for narrow bands
            if (isTransient)
                metric[i] = 2 * bestLevel;
            else
                metric[i] = -2 * bestLevel;

            // For narrow bands that can't be fully split, bias to avoid favouring one extreme
            if (narrow != 0 && (metric[i] == 0 || metric[i] == -2 * LM))
                metric[i] -= 1;
        }

        // --- Viterbi search for optimal tf_res[] path ---
        int[] selcost = new int[2];
        int tfSelect = 0;

        for (int sel = 0; sel < 2; sel++)
        {
            int imp0 = importance != null ? importance[0] : 1;
            int cost0 = imp0 * Math.Abs(metric[0] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * sel + 0]);
            int cost1 = imp0 * Math.Abs(metric[0] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * sel + 1])
                        + (isTransient ? 0 : lambda);
            for (int i = 1; i < nbBands; i++)
            {
                int imp = importance != null ? importance[i] : 1;
                int curr0 = Math.Min(cost0, cost1 + lambda);
                int curr1 = Math.Min(cost0 + lambda, cost1);
                cost0 = curr0 + imp * Math.Abs(metric[i] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * sel + 0]);
                cost1 = curr1 + imp * Math.Abs(metric[i] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * sel + 1]);
            }
            selcost[sel] = Math.Min(cost0, cost1);
        }

        // Conservative: only allow tf_select=1 for transients (matches Opus)
        if (selcost[1] < selcost[0] && isTransient)
            tfSelect = 1;

        // Final Viterbi forward pass with chosen tf_select
        int[] path0 = new int[nbBands];
        int[] path1 = new int[nbBands];
        {
            int imp0 = importance != null ? importance[0] : 1;
            int cost0 = imp0 * Math.Abs(metric[0] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + 0]);
            int cost1 = imp0 * Math.Abs(metric[0] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + 1])
                        + (isTransient ? 0 : lambda);

            for (int i = 1; i < nbBands; i++)
            {
                int imp = importance != null ? importance[i] : 1;
                int from0, from1;

                from0 = cost0;
                from1 = cost1 + lambda;
                int curr0, curr1;
                if (from0 < from1) { curr0 = from0; path0[i] = 0; }
                else { curr0 = from1; path0[i] = 1; }

                from0 = cost0 + lambda;
                from1 = cost1;
                if (from0 < from1) { curr1 = from0; path1[i] = 0; }
                else { curr1 = from1; path1[i] = 1; }

                cost0 = curr0 + imp * Math.Abs(metric[i] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + 0]);
                cost1 = curr1 + imp * Math.Abs(metric[i] - 2 * TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + 1]);
            }

            // Terminal decision
            tfRes[nbBands - 1] = cost0 < cost1 ? 0 : 1;
        }

        // Backward pass to reconstruct path
        for (int i = nbBands - 2; i >= 0; i--)
        {
            tfRes[i] = tfRes[i + 1] == 1 ? path1[i + 1] : path0[i + 1];
        }

        return tfSelect;
    }

    /// <summary>
    /// Encode tf_res[] and tf_select into the range coder, then apply the lookup table
    /// to convert tf_res[] from 0/1 flags to actual tf_change values.
    /// Port of Opus tf_encode().
    /// </summary>
    public static void TfEncode(
        int start, int end, bool isTransient, int[] tfRes, int LM, int tfSelect,
        PulsarRangeEncoder enc, int budgetBits)
    {
        int isTrans = isTransient ? 1 : 0;
        int tell = enc.TellBits;
        int logp = isTransient ? 2 : 4;

        // Reserve space for tf_select bit
        bool tfSelectRsv = LM > 0 && tell + logp + 1 <= budgetBits;
        int effectiveBudget = budgetBits - (tfSelectRsv ? 1 : 0);

        int curr = 0;
        int tfChanged = 0;
        for (int i = start; i < end; i++)
        {
            if (enc.TellBits + logp <= effectiveBudget)
            {
                enc.EncodeBitLogp(tfRes[i] != curr, logp);
                curr = tfRes[i];
                tfChanged |= curr;
            }
            else
            {
                tfRes[i] = curr; // Out of bits: revert to previous
            }
            logp = isTransient ? 4 : 5;
        }

        // Only code tf_select if it makes a difference
        if (tfSelectRsv &&
            TfSelectTable[LM, 4 * isTrans + 0 + tfChanged] !=
            TfSelectTable[LM, 4 * isTrans + 2 + tfChanged])
        {
            enc.EncodeBitLogp(tfSelect != 0, 1);
        }
        else
        {
            tfSelect = 0;
        }

        // Apply lookup table: convert flags to actual tf_change values
        for (int i = start; i < end; i++)
            tfRes[i] = TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + tfRes[i]];
    }

    /// <summary>
    /// Decode tf_res[] from the range coder and apply the lookup table.
    /// Port of Opus tf_decode().
    /// </summary>
    public static void TfDecode(
        int start, int end, bool isTransient, int[] tfRes, int LM,
        PulsarRangeDecoder dec, int budgetBits)
    {
        int isTrans = isTransient ? 1 : 0;
        int tell = dec.TellBits;
        int logp = isTransient ? 2 : 4;

        bool tfSelectRsv = LM > 0 && tell + logp + 1 <= budgetBits;
        int effectiveBudget = budgetBits - (tfSelectRsv ? 1 : 0);

        int curr = 0;
        int tfChanged = 0;
        for (int i = start; i < end; i++)
        {
            if (dec.TellBits + logp <= effectiveBudget)
            {
                bool bit = dec.DecodeBitLogp(logp);
                if (bit) curr ^= 1;
                tfChanged |= curr;
            }
            tfRes[i] = curr;
            logp = isTransient ? 4 : 5;
        }

        int tfSelect = 0;
        if (tfSelectRsv &&
            TfSelectTable[LM, 4 * isTrans + 0 + tfChanged] !=
            TfSelectTable[LM, 4 * isTrans + 2 + tfChanged])
        {
            tfSelect = dec.DecodeBitLogp(1) ? 1 : 0;
        }

        for (int i = start; i < end; i++)
            tfRes[i] = TfSelectTable[LM, 4 * isTrans + 2 * tfSelect + tfRes[i]];
    }
}
