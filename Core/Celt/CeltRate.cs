using System;
using System.Collections.Generic;
using System.Numerics;

/*
   Derived from the Opus CELT sources (celt/rate.c, celt/rate.h,
   celt/cwrs.c, celt/modes.c).

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2007-2009 Timothy B. Terriberry
   Copyright (c) 2007-2009 Jean-Marc Valin

   Redistribution and use in source and binary forms, with or without
   modification, are permitted under the BSD-style Opus license. See
   THIRD_PARTY_NOTICES.md and opus-main/COPYING for the full text.
*/

public sealed class CeltPulseCache
{
    public required short[] Index { get; init; }
    public required byte[] Bits { get; init; }
    public int Size => Bits.Length;
}

public sealed class CeltMode
{
    public const int BitAllocSize = 11;

    public static readonly short[] StandardEBands5Ms =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100
    ];

    public static readonly byte[] StandardBandAllocation =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        90, 80, 75, 69, 63, 56, 49, 40, 34, 29, 20, 18, 10, 0, 0, 0, 0, 0, 0, 0, 0,
        110, 100, 90, 84, 78, 71, 65, 58, 51, 45, 39, 32, 26, 20, 12, 0, 0, 0, 0, 0, 0,
        118, 110, 103, 93, 86, 80, 75, 70, 65, 59, 53, 47, 40, 31, 23, 15, 4, 0, 0, 0, 0,
        126, 119, 112, 104, 95, 89, 83, 78, 72, 66, 60, 54, 47, 39, 32, 25, 17, 12, 1, 0, 0,
        134, 127, 120, 114, 103, 97, 91, 85, 78, 72, 66, 60, 54, 47, 41, 35, 29, 23, 16, 10, 1,
        144, 137, 130, 124, 113, 107, 101, 95, 88, 82, 76, 70, 64, 57, 51, 45, 39, 33, 26, 15, 1,
        152, 145, 138, 132, 123, 117, 111, 105, 98, 92, 86, 80, 74, 67, 61, 55, 49, 43, 36, 20, 1,
        162, 155, 148, 142, 133, 127, 121, 115, 108, 102, 96, 90, 84, 77, 71, 65, 59, 53, 46, 30, 1,
        172, 165, 158, 152, 143, 137, 131, 125, 118, 112, 106, 100, 94, 87, 81, 75, 69, 63, 56, 45, 20,
        200, 200, 200, 200, 200, 200, 200, 200, 198, 193, 188, 183, 178, 173, 168, 163, 158, 153, 148, 129, 104
    ];

    private CeltMode(short[] eBands, CeltPulseCache pulseCache)
    {
        EBands = eBands;
        PulseCache = pulseCache;
        int nb = eBands.Length - 1;
        LogN = new short[nb];
        for (int i = 0; i < nb; i++)
        {
            int w = eBands[i + 1] - eBands[i];
            LogN[i] = w > 0 ? (short)CeltRate.Log2Frac((uint)w, CeltRate.BitRes) : (short)0;
        }
    }

    public short[] EBands { get; }

    public int NbEBands => EBands.Length - 1;

    public CeltPulseCache PulseCache { get; }

    /// <summary>log2(bandWidth) in BITRES=3 fractional bits, one entry per band.</summary>
    public short[] LogN { get; }

    public static CeltMode CreateStandard5Ms(int maxLm = 6)
    {
        short[] eBands = (short[])StandardEBands5Ms.Clone();
        return new CeltMode(eBands, CeltRate.ComputePulseCache(eBands, maxLm));
    }

    public static CeltMode CreateFromBandWidths(ReadOnlySpan<int> bandWidths, int maxLm = 1)
    {
        if (bandWidths.Length == 0)
        {
            return new CeltMode([0], new CeltPulseCache { Index = [], Bits = [] });
        }

        short[] eBands = new short[bandWidths.Length + 1];
        int cursor = 0;
        for (int i = 0; i < bandWidths.Length; i++)
        {
            cursor += Math.Max(1, bandWidths[i]);
            eBands[i + 1] = (short)Math.Clamp(cursor, 0, short.MaxValue);
        }

        return new CeltMode(eBands, CeltRate.ComputePulseCache(eBands, maxLm));
    }
}

public static class CeltRate
{
    public const int BitRes = 3;
    public const int MaxPseudo = 40;
    public const int LogMaxPseudo = 6;
    public const int CeltMaxPulses = 128;

    private static readonly short[] MaxN =
    [
        32767, 32767, 32767, 1476, 283, 109, 60, 40, 29, 24, 20, 18, 16, 14, 13
    ];

    private static readonly short[] MaxK =
    [
        32767, 32767, 32767, 32767, 1172, 238, 95, 53, 36, 27, 22, 18, 16, 15, 13
    ];

    public static int GetPulses(int i)
    {
        return i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);
    }

    public static bool FitsIn32(int n, int k)
    {
        if (n < 0 || k < 0)
        {
            return false;
        }

        if (n >= 14)
        {
            return k < 14 && n <= MaxN[k];
        }

        return k <= MaxK[n];
    }

    public static int Bits2Pulses(CeltMode mode, int band, int lm, int bits)
    {
        ArgumentNullException.ThrowIfNull(mode);
        int offset = GetCacheOffset(mode, band, lm);
        ReadOnlySpan<byte> cache = mode.PulseCache.Bits;
        int lo = 0;
        int hi = cache[offset];
        bits--;

        for (int i = 0; i < LogMaxPseudo; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[offset + mid] >= bits)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return bits - cache[offset + lo] <= cache[offset + hi] - bits ? lo : hi;
    }

    public static int Pulses2Bits(CeltMode mode, int band, int lm, int pulses)
    {
        ArgumentNullException.ThrowIfNull(mode);
        int offset = GetCacheOffset(mode, band, lm);
        if (pulses == 0) return 0;
        int maxPseudo = mode.PulseCache.Bits[offset];
        pulses = Math.Min(pulses, maxPseudo);
        return pulses == 0 ? 0 : mode.PulseCache.Bits[offset + pulses] + 1;
    }

    public static CeltPulseCache ComputePulseCache(ReadOnlySpan<short> eBands, int lm)
    {
        if (eBands.Length < 2)
        {
            throw new ArgumentException("At least two eBands entries are required.", nameof(eBands));
        }

        int nbEBands = eBands.Length - 1;
        short[] cindex = new short[nbEBands * (lm + 2)];
        Array.Fill(cindex, (short)-1);

        var entries = new List<(int N, int K, int Offset)>(100);
        int curr = 0;

        for (int i = 0; i <= lm + 1; i++)
        {
            for (int j = 0; j < nbEBands; j++)
            {
                int n = (eBands[j + 1] - eBands[j]) << i >> 1;

                for (int k = 0; k <= i && cindex[(i * nbEBands) + j] == -1; k++)
                {
                    for (int previousBand = 0; previousBand < nbEBands && (k != i || previousBand < j); previousBand++)
                    {
                        if (n == (eBands[previousBand + 1] - eBands[previousBand]) << k >> 1)
                        {
                            cindex[(i * nbEBands) + j] = cindex[(k * nbEBands) + previousBand];
                            break;
                        }
                    }
                }

                if (cindex[(i * nbEBands) + j] != -1 || n == 0)
                {
                    continue;
                }

                int pseudo = 0;
                while (pseudo < MaxPseudo && FitsIn32(n, GetPulses(pseudo + 1)))
                {
                    pseudo++;
                }

                cindex[(i * nbEBands) + j] = (short)curr;
                entries.Add((n, pseudo, curr));
                curr += pseudo + 1;
            }
        }

        byte[] bits = new byte[curr];
        foreach ((int n, int maxPseudo, int offset) in entries)
        {
            short[] requiredBits = GetRequiredBits(n, GetPulses(maxPseudo), BitRes);
            bits[offset] = (byte)maxPseudo;
            for (int j = 1; j <= maxPseudo; j++)
            {
                bits[offset + j] = (byte)(requiredBits[GetPulses(j)] - 1);
            }
        }

        return new CeltPulseCache
        {
            Index = cindex,
            Bits = bits
        };
    }

    public static short[] GetRequiredBits(int n, int maxK, int frac)
    {
        if (maxK <= 0)
        {
            return new short[maxK + 1];
        }

        var bits = new short[maxK + 1];
        if (n == 1)
        {
            for (int k = 1; k <= maxK; k++)
            {
                bits[k] = (short)(1 << frac);
            }

            return bits;
        }

        uint[] u = new uint[maxK + 2];
        NcWrsURow((uint)n, (uint)maxK, u);
        for (int k = 1; k <= maxK; k++)
        {
            bits[k] = (short)Log2Frac(u[k] + u[k + 1], frac);
        }

        return bits;
    }

    public static int Log2Frac(uint value, int frac)
    {
        int l = 32 - BitOperations.LeadingZeroCount(value);
        if ((value & (value - 1)) != 0)
        {
            if (l > 16)
            {
                value = ((value - 1) >> (l - 16)) + 1;
            }
            else
            {
                value <<= 16 - l;
            }

            l = (l - 1) << frac;
            do
            {
                int b = (int)(value >> 16);
                l += b << frac;
                value = (value + (uint)b) >> b;
                value = ((value * value) + 0x7FFF) >> 15;
            }
            while (frac-- > 0);

            return l + (value > 0x8000 ? 1 : 0);
        }

        return (l - 1) << frac;
    }

    private static int GetCacheOffset(CeltMode mode, int band, int lm)
    {
        int cacheLm = lm + 1;
        if ((uint)band >= (uint)mode.NbEBands || cacheLm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(band));
        }

        int indexOffset = (cacheLm * mode.NbEBands) + band;
        if ((uint)indexOffset >= (uint)mode.PulseCache.Index.Length || mode.PulseCache.Index[indexOffset] < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lm));
        }

        return mode.PulseCache.Index[indexOffset];
    }

    private static uint NcWrsURow(uint n, uint k, Span<uint> u)
    {
        uint len = k + 2;
        u[0] = 0;
        u[1] = 1;
        for (uint i = 2; i < len; i++)
        {
            u[(int)i] = (i << 1) - 1;
        }

        for (uint i = 2; i < n; i++)
        {
            Unext(u[1..], (int)k + 1, 1);
        }

        return u[(int)k] + u[(int)k + 1];
    }

    private static void Unext(Span<uint> u, int len, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = u[j] + u[j - 1] + ui0;
            u[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < len);

        u[j - 1] = ui0;
    }
}

public static class CeltCwrs
{
    public static bool TryEncode(ReadOnlySpan<int> pulses, int pulseCount, out uint index, out uint codebookSize)
    {
        index = 0;
        codebookSize = 0;

        int n = pulses.Length;
        if (n <= 0 || pulseCount <= 0 || !CeltRate.FitsIn32(n, pulseCount))
        {
            return false;
        }

        if (n == 1)
        {
            index = pulses[0] < 0 ? 1u : 0u;
            codebookSize = 2;
            return true;
        }

        uint[] u = new uint[pulseCount + 2];
        uint encoded = IcWrs(n, pulseCount, pulses, u, out uint nc);
        index = encoded;
        codebookSize = nc;
        return true;
    }

    public static int[] Decode(int n, int pulseCount, uint index)
    {
        if (n <= 0)
        {
            return Array.Empty<int>();
        }

        if (pulseCount <= 0)
        {
            return new int[n];
        }

        if (!CeltRate.FitsIn32(n, pulseCount))
        {
            throw new ArgumentOutOfRangeException(nameof(pulseCount), "CELT CWRS codebook does not fit in 32 bits.");
        }

        if (n == 1)
        {
            return [index == 0 ? pulseCount : -pulseCount];
        }

        int[] y = new int[n];
        uint[] u = new uint[pulseCount + 2];
        uint nc = NcWrsURow((uint)n, (uint)pulseCount, u);
        if (index >= nc)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "CELT CWRS index is outside the codebook.");
        }

        CwrsI(n, pulseCount, index, y, u);
        return y;
    }

    public static uint Count(int n, int pulseCount)
    {
        if (n <= 0 || pulseCount <= 0 || !CeltRate.FitsIn32(n, pulseCount))
        {
            return 0;
        }

        if (n == 1)
        {
            return 2;
        }

        uint[] u = new uint[pulseCount + 2];
        return NcWrsURow((uint)n, (uint)pulseCount, u);
    }

    private static uint IcWrs(int n, int maxPulseCount, ReadOnlySpan<int> y, Span<uint> u, out uint nc)
    {
        u[0] = 0;
        for (int kCursor = 1; kCursor <= maxPulseCount + 1; kCursor++)
        {
            u[kCursor] = (uint)((kCursor << 1) - 1);
        }

        int k = Math.Abs(y[n - 1]);
        uint i = y[n - 1] < 0 ? 1u : 0u;
        int j = n - 2;

        i += u[k];
        k += Math.Abs(y[j]);
        if (y[j] < 0)
        {
            i += u[k + 1];
        }

        while (j-- > 0)
        {
            Unext(u, maxPulseCount + 2, 0);
            i += u[k];
            k += Math.Abs(y[j]);
            if (y[j] < 0)
            {
                i += u[k + 1];
            }
        }

        nc = u[k] + u[k + 1];
        return i;
    }

    private static int CwrsI(int n, int k, uint i, Span<int> y, Span<uint> u)
    {
        int j = 0;
        int yy = 0;

        do
        {
            uint p = u[k + 1];
            bool negative = i >= p;
            if (negative)
            {
                i -= p;
            }

            int yj = k;
            p = u[k];
            while (p > i)
            {
                p = u[--k];
            }

            i -= p;
            yj -= k;
            int value = negative ? -yj : yj;
            y[j] = value;
            yy += value * value;
            Uprev(u, k + 2, 0);
        }
        while (++j < n);

        return yy;
    }

    private static uint NcWrsURow(uint n, uint k, Span<uint> u)
    {
        uint len = k + 2;
        u[0] = 0;
        u[1] = 1;
        for (uint cursor = 2; cursor < len; cursor++)
        {
            u[(int)cursor] = (cursor << 1) - 1;
        }

        for (uint cursor = 2; cursor < n; cursor++)
        {
            Unext(u[1..], (int)k + 1, 1);
        }

        return u[(int)k] + u[(int)k + 1];
    }

    private static void Unext(Span<uint> u, int len, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = u[j] + u[j - 1] + ui0;
            u[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < len);

        u[j - 1] = ui0;
    }

    private static void Uprev(Span<uint> u, int len, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = u[j] - u[j - 1] - ui0;
            u[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < len);

        u[j - 1] = ui0;
    }
}
