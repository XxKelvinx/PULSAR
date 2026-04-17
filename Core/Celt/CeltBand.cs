using System;

// Port of Opus CELT compute_qn + (mono) compute_theta from bands.c. Provides
// the angle-quantization primitive used by the quant_band split recursion.
// Stereo-specific paths (intensity flag, B0>1 uniform PDF, N==2 two-phase) are
// stubbed for Phase 1 mono; they'll be added alongside the stereo port.
public static class CeltBand
{
    public const int BitRes = 3;
    private const int QThetaOffset = 4;

    private static readonly short[] Exp2Table8 =
    {
        16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048,
    };

    /// <summary>
    /// Returns the number of quantization levels Opus uses for the split angle
    /// given band size <paramref name="N"/>, available fractional-bit budget
    /// <paramref name="b"/> (BITRES=3 units), pulse-cap offset, and LM/stereo
    /// context. qn==1 means no split (all energy to one side).
    /// </summary>
    public static int ComputeQn(int N, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = 2 * N - 1;
        if (stereo && N == 2) n2--;
        int qb = CeltMath.CeltSudiv(b + n2 * offset, n2);
        qb = Math.Min(b - pulseCap - (4 << BitRes), qb);
        qb = Math.Min(8 << BitRes, qb);

        int qn;
        if (qb < (1 << BitRes >> 1))
        {
            qn = 1;
        }
        else
        {
            qn = Exp2Table8[qb & 0x7] >> (14 - (qb >> BitRes));
            qn = (qn + 1) >> 1 << 1;
        }
        return qn;
    }

    public struct SplitCtx
    {
        public int Inv;
        public int IMid;
        public int ISide;
        public int Delta;
        public int ITheta;
        public int QAlloc;
        public int Qn;
    }

    /// <summary>
    /// Encodes a pre-computed iTheta into the bitstream using Opus's mono split
    /// PDFs (triangular for B0==1, uniform for B0>1). Caller supplies iTheta in
    /// [0, 16384] (e.g. from its own atan2 of mid/side energies) and a budget
    /// <paramref name="b"/> in BITRES units which will be decremented by the
    /// actual bits consumed (Opus semantics).
    /// </summary>
    public static SplitCtx ComputeThetaMonoEncode(PulsarRangeEncoder enc, int N, int B0, int LM,
        ref int b, int iTheta16384, int logN, bool avoidSplitNoise = false)
    {
        var sctx = default(SplitCtx);
        int pulseCap = logN + LM * (1 << BitRes);
        int offset = (pulseCap >> 1) - QThetaOffset;
        int qn = ComputeQn(N, b, offset, pulseCap, stereo: false);
        sctx.Qn = qn;

        uint tellBefore = enc.TellFractionalBits;
        int iTheta = iTheta16384;

        if (qn != 1)
        {
            iTheta = (iTheta * qn + 8192) >> 14;

            // Opus avoid_split_noise: if quantized theta would cause one side
            // to get zero bits (injecting noise), force theta to 0 or qn.
            if (avoidSplitNoise && iTheta > 0 && iTheta < qn)
            {
                int unquantized = (iTheta * 16384) / qn;
                int tryImid = CeltMath.BitexactCos((short)unquantized);
                int tryIside = CeltMath.BitexactCos((short)(16384 - unquantized));
                int tryDelta = CeltMath.FracMul16((N - 1) << 7,
                    CeltMath.BitexactLog2Tan(tryIside, tryImid));
                if (tryDelta > b)
                    iTheta = qn;
                else if (tryDelta < -b)
                    iTheta = 0;
            }

            if (B0 > 1)
            {
                // Uniform PDF over qn+1 symbols.
                enc.EncodeUInt((uint)iTheta, (uint)(qn + 1));
            }
            else
            {
                // Triangular PDF.
                int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                int fs = iTheta <= (qn >> 1) ? iTheta + 1 : qn + 1 - iTheta;
                int fl = iTheta <= (qn >> 1)
                    ? (iTheta * (iTheta + 1)) >> 1
                    : ft - (((qn + 1 - iTheta) * (qn + 2 - iTheta)) >> 1);
                enc.EncodeSymbol((uint)fl, (uint)(fl + fs), (uint)ft);
            }
            iTheta = (iTheta * 16384) / qn;
        }

        FinaliseSplit(ref sctx, iTheta, N);
        sctx.QAlloc = (int)(enc.TellFractionalBits - tellBefore);
        b -= sctx.QAlloc;
        return sctx;
    }

    public static SplitCtx ComputeThetaMonoDecode(PulsarRangeDecoder dec, int N, int B0, int LM,
        ref int b, int logN)
    {
        var sctx = default(SplitCtx);
        int pulseCap = logN + LM * (1 << BitRes);
        int offset = (pulseCap >> 1) - QThetaOffset;
        int qn = ComputeQn(N, b, offset, pulseCap, stereo: false);
        sctx.Qn = qn;

        uint tellBefore = dec.TellFractionalBits;
        int iTheta = 0;

        if (qn != 1)
        {
            if (B0 > 1)
            {
                iTheta = (int)dec.DecodeUInt((uint)(qn + 1));
            }
            else
            {
                int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                int fm = (int)dec.GetTarget((uint)ft);
                int fs, fl;
                if (fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1))
                {
                    iTheta = (int)((CeltMath.Isqrt32((uint)(8 * fm + 1)) - 1) >> 1);
                    fs = iTheta + 1;
                    fl = (iTheta * (iTheta + 1)) >> 1;
                }
                else
                {
                    iTheta = (int)((2 * (qn + 1)
                        - CeltMath.Isqrt32((uint)(8 * (ft - fm - 1) + 1))) >> 1);
                    fs = qn + 1 - iTheta;
                    fl = ft - (((qn + 1 - iTheta) * (qn + 2 - iTheta)) >> 1);
                }
                dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
            }
            iTheta = (iTheta * 16384) / qn;
        }

        FinaliseSplit(ref sctx, iTheta, N);
        sctx.QAlloc = (int)(dec.TellFractionalBits - tellBefore);
        b -= sctx.QAlloc;
        return sctx;
    }

    private static void FinaliseSplit(ref SplitCtx sctx, int iTheta, int N)
    {
        int imid, iside, delta;
        if (iTheta == 0)
        {
            imid = 32767;
            iside = 0;
            delta = -16384;
        }
        else if (iTheta == 16384)
        {
            imid = 0;
            iside = 32767;
            delta = 16384;
        }
        else
        {
            imid = CeltMath.BitexactCos((short)iTheta);
            iside = CeltMath.BitexactCos((short)(16384 - iTheta));
            delta = CeltMath.FracMul16((N - 1) << 7, CeltMath.BitexactLog2Tan(iside, imid));
        }

        sctx.IMid = imid;
        sctx.ISide = iside;
        sctx.Delta = delta;
        sctx.ITheta = iTheta;
    }

    // ====================================================================
    //  Phase 1 #3/#4: quant_band mono — full recursive split + PVQ leaf
    //  Port of Opus CELT bands.c quant_band / quant_partition (mono).
    // ====================================================================

    /// <summary>Recursion context carried through the band split tree.</summary>
    public struct BandCtx
    {
        public CeltMode Mode;
        public int BandIndex;
        public int Spread;
        public int TfChange;
        /// <summary>Total remaining bits in the frame (BITRES=3 fractional units).</summary>
        public int RemainingBits;
        public uint Seed;
        public bool AvoidSplitNoise;
    }

    // --- Hadamard ordering table (Opus ordery_table) ---
    private static readonly int[] OrderyTable =
    [
        1, 0,
        3, 0, 2, 1,
        7, 0, 4, 3, 6, 1, 5, 2,
        15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
    ];

    private static readonly byte[] BitInterleaveTable =
    [
        0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3,
    ];

    private static readonly byte[] BitDeinterleaveTable =
    [
        0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F,
        0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF,
    ];

    // --- Transform helpers (ported from Opus bands.c) ---

    /// <summary>In-place Haar wavelet transform. Matches Opus haar1().</summary>
    public static void Haar1(Span<float> X, int N0, int stride)
    {
        const float S = 0.7071067811865476f;
        N0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < N0; j++)
            {
                int idx0 = stride * 2 * j + i;
                int idx1 = stride * (2 * j + 1) + i;
                float tmp1 = S * X[idx0];
                float tmp2 = S * X[idx1];
                X[idx0] = tmp1 + tmp2;
                X[idx1] = tmp1 - tmp2;
            }
        }
    }

    public static void DeinterleaveHadamard(Span<float> X, int N0, int stride, bool hadamard)
    {
        int N = N0 * stride;
        float[] tmp = new float[N];
        if (hadamard)
        {
            ReadOnlySpan<int> ordery = OrderyTable.AsSpan(stride - 2);
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < N0; j++)
                    tmp[ordery[i] * N0 + j] = X[j * stride + i];
        }
        else
        {
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < N0; j++)
                    tmp[i * N0 + j] = X[j * stride + i];
        }
        tmp.AsSpan(0, N).CopyTo(X);
    }

    public static void InterleaveHadamard(Span<float> X, int N0, int stride, bool hadamard)
    {
        int N = N0 * stride;
        float[] tmp = new float[N];
        if (hadamard)
        {
            ReadOnlySpan<int> ordery = OrderyTable.AsSpan(stride - 2);
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < N0; j++)
                    tmp[j * stride + i] = X[ordery[i] * N0 + j];
        }
        else
        {
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < N0; j++)
                    tmp[j * stride + i] = X[i * N0 + j];
        }
        tmp.AsSpan(0, N).CopyTo(X);
    }

    /// <summary>
    /// Compute the split angle iTheta (Q14, [0..16384]) from the energy
    /// ratio of two half-bands. Matches Opus stereo_itheta() for the
    /// mono (stereo=0) code path: Emid=||X||², Eside=||Y||².
    /// </summary>
    public static int ComputeIThetaMono(ReadOnlySpan<float> X, ReadOnlySpan<float> Y, int N)
    {
        float Emid = 0, Eside = 0;
        for (int i = 0; i < N; i++) Emid += X[i] * X[i];
        for (int i = 0; i < N; i++) Eside += Y[i] * Y[i];
        float mid = MathF.Sqrt(Emid);
        float side = MathF.Sqrt(Eside);
        if (mid + side < 1e-15f) return 0;
        int itheta = (int)MathF.Round(16384.0f * MathF.Atan2(side, mid) * (2.0f / MathF.PI));
        return Math.Clamp(itheta, 0, 16384);
    }

    /// <summary>
    /// Writes gain * (iy / ||iy||) into X. Matches Opus normalise_residual()
    /// for the float path.
    /// </summary>
    public static void NormaliseResidual(ReadOnlySpan<int> iy, Span<float> X, int N, float gain)
    {
        float Ryy = 0;
        for (int i = 0; i < N; i++) Ryy += iy[i] * (float)iy[i];
        if (Ryy < 1e-30f) { X.Slice(0, N).Clear(); return; }
        float g = gain / MathF.Sqrt(Ryy);
        for (int i = 0; i < N; i++) X[i] = g * iy[i];
    }

    private static uint CeltLcgRand(uint seed) => 1664525 * seed + 1013904223;

    // ----------------------------------------------------------------
    //  QuantBandMonoEncode — top-level mono band encoder
    // ----------------------------------------------------------------

    /// <summary>
    /// Encodes a mono band using Opus-style recursive PVQ split.
    /// X is modified in-place with the reconstructed (resynth) output.
    /// Returns the collapse mask.
    /// </summary>
    public static uint QuantBandMonoEncode(
        ref BandCtx ctx, PulsarRangeEncoder enc,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        Span<float> lowbandOut, float gain,
        Span<float> lowbandScratch, int fill)
    {
        int N0 = N;
        int N_B = N;
        int B0 = B;
        int timeDiv = 0;
        int recombine = 0;
        bool longBlocks = B0 == 1;

        // N==1: just a sign bit
        if (N == 1)
        {
            return QuantBandN1Encode(ref ctx, enc, X, lowbandOut, gain);
        }

        N_B /= Math.Max(1, B);

        if (ctx.TfChange > 0)
            recombine = ctx.TfChange;

        // Copy lowband to scratch if transforms will reorder it
        if (!lowbandScratch.IsEmpty && !lowband.IsEmpty &&
            (recombine != 0 || ((N_B & 1) == 0 && ctx.TfChange < 0) || B0 > 1))
        {
            lowband.Slice(0, Math.Min(lowband.Length, N)).CopyTo(lowbandScratch);
            lowband = lowbandScratch;
        }

        // Band recombining (frequency resolution increase)
        for (int k = 0; k < recombine; k++)
        {
            Haar1(X.Slice(0, N), N >> k, 1 << k);
            if (!lowband.IsEmpty) Haar1(AsWritable(lowband, N), N >> k, 1 << k);
            fill = BitInterleaveTable[fill & 0xF] | (BitInterleaveTable[fill >> 4] << 2);
        }
        B >>= recombine;
        N_B <<= recombine;

        // Increasing time resolution
        int tfChange = ctx.TfChange;
        while ((N_B & 1) == 0 && tfChange < 0)
        {
            Haar1(X.Slice(0, N), N_B, B);
            if (!lowband.IsEmpty) Haar1(AsWritable(lowband, N), N_B, B);
            fill |= fill << B;
            B <<= 1;
            N_B >>= 1;
            timeDiv++;
            tfChange++;
        }
        B0 = B;
        int N_B0 = N_B;

        // Deinterleave for B0>1
        if (B0 > 1)
        {
            DeinterleaveHadamard(X.Slice(0, N), N_B >> recombine, B0 << recombine, longBlocks);
            if (!lowband.IsEmpty)
                DeinterleaveHadamard(AsWritable(lowband, N), N_B >> recombine, B0 << recombine, longBlocks);
        }

        uint cm = QuantPartitionEncode(ref ctx, enc, X, N, b, B, lowband, LM, gain, fill);

        // Resynth: undo transforms
        if (B0 > 1)
            InterleaveHadamard(X.Slice(0, N), N_B >> recombine, B0 << recombine, longBlocks);

        N_B = N_B0;
        B = B0;
        for (int k = 0; k < timeDiv; k++)
        {
            B >>= 1;
            N_B <<= 1;
            cm |= cm >> B;
            Haar1(X.Slice(0, N), N_B, B);
        }

        for (int k = 0; k < recombine; k++)
        {
            cm = BitDeinterleaveTable[cm];
            Haar1(X.Slice(0, N), N0 >> k, 1 << k);
        }
        B <<= recombine;

        // Scale output for folding
        if (!lowbandOut.IsEmpty)
        {
            float n = MathF.Sqrt(N0);
            for (int j = 0; j < N0; j++)
                lowbandOut[j] = n * X[j];
        }

        cm &= (uint)((1UL << B) - 1);
        return cm;
    }

    // ----------------------------------------------------------------
    //  QuantBandMonoDecode — top-level mono band decoder
    // ----------------------------------------------------------------

    public static uint QuantBandMonoDecode(
        ref BandCtx ctx, PulsarRangeDecoder dec,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        Span<float> lowbandOut, float gain,
        Span<float> lowbandScratch, int fill)
    {
        int N0 = N;
        int N_B = N;
        int B0 = B;
        int timeDiv = 0;
        int recombine = 0;
        bool longBlocks = B0 == 1;

        if (N == 1)
        {
            return QuantBandN1Decode(ref ctx, dec, X, lowbandOut, gain);
        }

        N_B /= Math.Max(1, B);

        if (ctx.TfChange > 0)
            recombine = ctx.TfChange;

        if (!lowbandScratch.IsEmpty && !lowband.IsEmpty &&
            (recombine != 0 || ((N_B & 1) == 0 && ctx.TfChange < 0) || B0 > 1))
        {
            lowband.Slice(0, Math.Min(lowband.Length, N)).CopyTo(lowbandScratch);
            lowband = lowbandScratch;
        }

        for (int k = 0; k < recombine; k++)
        {
            if (!lowband.IsEmpty) Haar1(AsWritable(lowband, N), N >> k, 1 << k);
            fill = BitInterleaveTable[fill & 0xF] | (BitInterleaveTable[fill >> 4] << 2);
        }
        B >>= recombine;
        N_B <<= recombine;

        int tfChange = ctx.TfChange;
        while ((N_B & 1) == 0 && tfChange < 0)
        {
            if (!lowband.IsEmpty) Haar1(AsWritable(lowband, N), N_B, B);
            fill |= fill << B;
            B <<= 1;
            N_B >>= 1;
            timeDiv++;
            tfChange++;
        }
        B0 = B;
        int N_B0 = N_B;

        if (B0 > 1 && !lowband.IsEmpty)
            DeinterleaveHadamard(AsWritable(lowband, N), N_B >> recombine, B0 << recombine, longBlocks);

        uint cm = QuantPartitionDecode(ref ctx, dec, X, N, b, B, lowband, LM, gain, fill);

        // Resynth undo
        if (B0 > 1)
            InterleaveHadamard(X.Slice(0, N), N_B >> recombine, B0 << recombine, longBlocks);

        N_B = N_B0;
        B = B0;
        for (int k = 0; k < timeDiv; k++)
        {
            B >>= 1;
            N_B <<= 1;
            cm |= cm >> B;
            Haar1(X.Slice(0, N), N_B, B);
        }

        for (int k = 0; k < recombine; k++)
        {
            cm = BitDeinterleaveTable[cm];
            Haar1(X.Slice(0, N), N0 >> k, 1 << k);
        }
        B <<= recombine;

        if (!lowbandOut.IsEmpty)
        {
            float n = MathF.Sqrt(N0);
            for (int j = 0; j < N0; j++)
                lowbandOut[j] = n * X[j];
        }

        cm &= (uint)((1UL << B) - 1);
        return cm;
    }

    // ----------------------------------------------------------------
    //  QuantPartitionEncode — recursive split (encoder side)
    // ----------------------------------------------------------------

    private static uint QuantPartitionEncode(
        ref BandCtx ctx, PulsarRangeEncoder enc,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        float gain, int fill)
    {
        int B0 = B;

        // Split check: if we need 1.5 more bits than the cache can produce, split.
        // Matches Opus quant_partition() logic exactly.
        bool doSplit = false;
        if (LM != -1 && N > 2)
        {
            int cacheLm = LM + 1;
            int idxOff = cacheLm * ctx.Mode.NbEBands + ctx.BandIndex;
            if ((uint)idxOff < (uint)ctx.Mode.PulseCache.Index.Length &&
                ctx.Mode.PulseCache.Index[idxOff] >= 0)
            {
                int cacheOff = ctx.Mode.PulseCache.Index[idxOff];
                int maxPseudo = ctx.Mode.PulseCache.Bits[cacheOff];
                int maxBits = maxPseudo > 0 ? ctx.Mode.PulseCache.Bits[cacheOff + maxPseudo] : 0;
                doSplit = b > maxBits + 12;
            }
        }

        if (doSplit)
        {
            // --- SPLIT ---
            N >>= 1;
            Span<float> Y = X.Slice(N, N);
            Span<float> Xhalf = X.Slice(0, N);
            LM -= 1;
            if (B == 1) fill = (fill & 1) | (fill << 1);
            B = (B + 1) >> 1;

            int iTheta16384 = ComputeIThetaMono(Xhalf, Y, N);
            int logN = ctx.Mode.LogN[ctx.BandIndex];
            var sctx = ComputeThetaMonoEncode(enc, N, B0, LM, ref b, iTheta16384, logN,
                ctx.AvoidSplitNoise);

            float mid = sctx.IMid / 32768.0f;
            float side = sctx.ISide / 32768.0f;
            int delta = sctx.Delta;
            int itheta = sctx.ITheta;

            // Forward-masking bias for short blocks (B0>1)
            if (B0 > 1 && (itheta & 0x3FFF) != 0)
            {
                if (itheta > 8192)
                    delta -= delta >> (4 - LM);
                else
                    delta = Math.Min(0, delta + (N << BitRes >> (5 - LM)));
            }

            int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            int sbits = b - mbits;
            ctx.RemainingBits -= sctx.QAlloc;

            ReadOnlySpan<float> nextLowband2 =
                !lowband.IsEmpty && lowband.Length >= 2 * N ? lowband.Slice(N) : default;

            uint cm;
            int rebalance = ctx.RemainingBits;
            if (mbits >= sbits)
            {
                cm = QuantPartitionEncode(ref ctx, enc, Xhalf, N, mbits, B,
                    lowband, LM, gain * mid, fill);
                rebalance = mbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 0)
                    sbits += rebalance - (3 << BitRes);
                cm |= QuantPartitionEncode(ref ctx, enc, Y, N, sbits, B,
                    nextLowband2, LM, gain * side, fill >> B) << (B0 >> 1);
            }
            else
            {
                cm = QuantPartitionEncode(ref ctx, enc, Y, N, sbits, B,
                    nextLowband2, LM, gain * side, fill >> B) << (B0 >> 1);
                rebalance = sbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 16384)
                    mbits += rebalance - (3 << BitRes);
                cm |= QuantPartitionEncode(ref ctx, enc, Xhalf, N, mbits, B,
                    lowband, LM, gain * mid, fill);
            }
            return cm;
        }

        // --- LEAF: PVQ quantize ---
        return QuantLeafEncode(ref ctx, enc, X, N, b, B, lowband, LM, gain, fill);
    }

    private static uint QuantLeafEncode(
        ref BandCtx ctx, PulsarRangeEncoder enc,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        float gain, int fill)
    {
        int q = CeltRate.Bits2Pulses(ctx.Mode, ctx.BandIndex, LM, b);
        int currBits = CeltRate.Pulses2Bits(ctx.Mode, ctx.BandIndex, LM, q);
        ctx.RemainingBits -= currBits;

        // Ensures we can never bust the budget
        while (ctx.RemainingBits < 0 && q > 0)
        {
            ctx.RemainingBits += currBits;
            q--;
            currBits = CeltRate.Pulses2Bits(ctx.Mode, ctx.BandIndex, LM, q);
            ctx.RemainingBits -= currBits;
        }

        if (q != 0)
        {
            int K = CeltRate.GetPulses(q);

            // Forward exp_rotation, PVQ search, CWRS encode, resynth
            CeltVq.ExpRotation(X.Slice(0, N), dir: 1, stride: B, K, ctx.Spread);
            int[] iy = CeltVq.OpusPvqSearch(X.Slice(0, N), K);

            if (CeltCwrs.TryEncode(iy, K, out uint rank, out uint total))
                enc.EncodeUInt(rank, total);

            // Resynth
            NormaliseResidual(iy, X, N, gain);
            CeltVq.ExpRotation(X.Slice(0, N), dir: -1, stride: B, K, ctx.Spread);
            return CeltVq.ExtractCollapseMask(iy, B);
        }

        // q==0: noise fill
        return NoiseFill(ref ctx, X, N, B, lowband, gain, fill);
    }

    // ----------------------------------------------------------------
    //  QuantPartitionDecode — recursive split (decoder side)
    // ----------------------------------------------------------------

    private static uint QuantPartitionDecode(
        ref BandCtx ctx, PulsarRangeDecoder dec,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        float gain, int fill)
    {
        int B0 = B;

        bool doSplit = false;
        if (LM != -1 && N > 2)
        {
            int cacheLm = LM + 1;
            int idxOff = cacheLm * ctx.Mode.NbEBands + ctx.BandIndex;
            if ((uint)idxOff < (uint)ctx.Mode.PulseCache.Index.Length &&
                ctx.Mode.PulseCache.Index[idxOff] >= 0)
            {
                int cacheOff = ctx.Mode.PulseCache.Index[idxOff];
                int maxPseudo = ctx.Mode.PulseCache.Bits[cacheOff];
                int maxBits = maxPseudo > 0 ? ctx.Mode.PulseCache.Bits[cacheOff + maxPseudo] : 0;
                doSplit = b > maxBits + 12;
            }
        }

        if (doSplit)
        {
            N >>= 1;
            Span<float> Y = X.Slice(N, N);
            Span<float> Xhalf = X.Slice(0, N);
            LM -= 1;
            if (B == 1) fill = (fill & 1) | (fill << 1);
            B = (B + 1) >> 1;

            int logN = ctx.Mode.LogN[ctx.BandIndex];
            var sctx = ComputeThetaMonoDecode(dec, N, B0, LM, ref b, logN);

            float mid = sctx.IMid / 32768.0f;
            float side = sctx.ISide / 32768.0f;
            int delta = sctx.Delta;
            int itheta = sctx.ITheta;

            if (B0 > 1 && (itheta & 0x3FFF) != 0)
            {
                if (itheta > 8192)
                    delta -= delta >> (4 - LM);
                else
                    delta = Math.Min(0, delta + (N << BitRes >> (5 - LM)));
            }

            int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            int sbits = b - mbits;
            ctx.RemainingBits -= sctx.QAlloc;

            ReadOnlySpan<float> nextLowband2 =
                !lowband.IsEmpty && lowband.Length >= 2 * N ? lowband.Slice(N) : default;

            uint cm;
            int rebalance = ctx.RemainingBits;
            if (mbits >= sbits)
            {
                cm = QuantPartitionDecode(ref ctx, dec, Xhalf, N, mbits, B,
                    lowband, LM, gain * mid, fill);
                rebalance = mbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 0)
                    sbits += rebalance - (3 << BitRes);
                cm |= QuantPartitionDecode(ref ctx, dec, Y, N, sbits, B,
                    nextLowband2, LM, gain * side, fill >> B) << (B0 >> 1);
            }
            else
            {
                cm = QuantPartitionDecode(ref ctx, dec, Y, N, sbits, B,
                    nextLowband2, LM, gain * side, fill >> B) << (B0 >> 1);
                rebalance = sbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 16384)
                    mbits += rebalance - (3 << BitRes);
                cm |= QuantPartitionDecode(ref ctx, dec, Xhalf, N, mbits, B,
                    lowband, LM, gain * mid, fill);
            }
            return cm;
        }

        return QuantLeafDecode(ref ctx, dec, X, N, b, B, lowband, LM, gain, fill);
    }

    private static uint QuantLeafDecode(
        ref BandCtx ctx, PulsarRangeDecoder dec,
        Span<float> X, int N, int b, int B,
        ReadOnlySpan<float> lowband, int LM,
        float gain, int fill)
    {
        int q = CeltRate.Bits2Pulses(ctx.Mode, ctx.BandIndex, LM, b);
        int currBits = CeltRate.Pulses2Bits(ctx.Mode, ctx.BandIndex, LM, q);
        ctx.RemainingBits -= currBits;

        while (ctx.RemainingBits < 0 && q > 0)
        {
            ctx.RemainingBits += currBits;
            q--;
            currBits = CeltRate.Pulses2Bits(ctx.Mode, ctx.BandIndex, LM, q);
            ctx.RemainingBits -= currBits;
        }

        if (q != 0)
        {
            int K = CeltRate.GetPulses(q);

            // CWRS decode → resynth → inverse exp_rotation
            uint total = CeltCwrs.Count(N, K);
            uint rank = dec.DecodeUInt(total);
            int[] iy = CeltCwrs.Decode(N, K, rank);

            NormaliseResidual(iy, X, N, gain);
            CeltVq.ExpRotation(X.Slice(0, N), dir: -1, stride: B, K, ctx.Spread);
            return CeltVq.ExtractCollapseMask(iy, B);
        }

        return NoiseFill(ref ctx, X, N, B, lowband, gain, fill);
    }

    // ----------------------------------------------------------------
    //  N==1 special case (sign bit only)
    // ----------------------------------------------------------------

    private static uint QuantBandN1Encode(
        ref BandCtx ctx, PulsarRangeEncoder enc,
        Span<float> X, Span<float> lowbandOut, float gain)
    {
        int sign = 0;
        if (ctx.RemainingBits >= 1 << BitRes)
        {
            sign = X[0] < 0 ? 1 : 0;
            enc.EncodeBits((uint)sign, 1);
            ctx.RemainingBits -= 1 << BitRes;
        }
        X[0] = sign != 0 ? -gain : gain;
        if (!lowbandOut.IsEmpty)
            lowbandOut[0] = X[0] * 0.0625f; // SHR32(X[0], 4)
        return 1;
    }

    private static uint QuantBandN1Decode(
        ref BandCtx ctx, PulsarRangeDecoder dec,
        Span<float> X, Span<float> lowbandOut, float gain)
    {
        int sign = 0;
        if (ctx.RemainingBits >= 1 << BitRes)
        {
            sign = (int)dec.DecodeBits(1);
            ctx.RemainingBits -= 1 << BitRes;
        }
        X[0] = sign != 0 ? -gain : gain;
        if (!lowbandOut.IsEmpty)
            lowbandOut[0] = X[0] * 0.0625f;
        return 1;
    }

    // ----------------------------------------------------------------
    //  Noise fill (q==0 leaf)
    // ----------------------------------------------------------------

    private static uint NoiseFill(
        ref BandCtx ctx, Span<float> X, int N, int B,
        ReadOnlySpan<float> lowband, float gain, int fill)
    {
        uint cmMask = (uint)((1UL << B) - 1);
        fill &= (int)cmMask;

        if (fill == 0)
        {
            X.Slice(0, N).Clear();
            return 0;
        }

        if (lowband.IsEmpty)
        {
            // Pure LCG noise
            for (int j = 0; j < N; j++)
            {
                ctx.Seed = CeltLcgRand(ctx.Seed);
                X[j] = (float)((int)ctx.Seed >> 20);
            }
        }
        else
        {
            // Folded spectrum + dither
            for (int j = 0; j < N; j++)
            {
                ctx.Seed = CeltLcgRand(ctx.Seed);
                float tmp = 1.0f / 256.0f;
                if ((ctx.Seed & 0x8000) != 0) tmp = -tmp;
                X[j] = (j < lowband.Length ? lowband[j] : 0) + tmp;
            }
        }

        CeltVq.RenormaliseVector(X.Slice(0, N), gain);
        return cmMask;
    }

    // ----------------------------------------------------------------
    //  Span helper: cast ReadOnlySpan to writable for in-place transforms
    //  on lowband buffers (which are always backed by scratch arrays).
    // ----------------------------------------------------------------

    private static Span<float> AsWritable(ReadOnlySpan<float> span, int length)
    {
        // Safe: caller guarantees span is backed by a mutable array (lowbandScratch).
        ref float r = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);
        return System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref r, Math.Min(span.Length, length));
    }
}
