using System;
using System.Collections.Concurrent;
using System.Numerics;

public static class PulsarPvqCombinatorics
{
    private static readonly ConcurrentDictionary<(int Width, uint MaxCodebookSize), int> SafePulseCapCache = new();

    // Opus CELT uses the symmetric helper U(N, K) and derives the codebook size from
    // V(N, K) = U(N, K) + U(N, K + 1).

    public static BigInteger GetU(int n, int k)
    {
        ValidateDimensions(n, k);
        int max = Math.Max(n, k);
        BigInteger[,] table = BuildUTable(max);
        return GetTableValue(table, n, k);
    }

    public static BigInteger GetV(int n, int k)
    {
        ValidateDimensions(n, k);
        if (k == 0)
        {
            return 1;
        }

        int max = Math.Max(n, k + 1);
        BigInteger[,] table = BuildUTable(max);
        return GetTableValue(table, n, k) + GetTableValue(table, n, k + 1);
    }

    public static int GetSafePulseCap(int width, uint maxCodebookSize = uint.MaxValue)
    {
        if (width <= 0)
        {
            return 0;
        }

        return SafePulseCapCache.GetOrAdd((width, maxCodebookSize), key => ComputeSafePulseCap(key.Width, key.MaxCodebookSize));
    }

    public static BigInteger RankPvq(ReadOnlySpan<int> levels)
    {
        int n = levels.Length;
        if (n == 0)
        {
            return 0;
        }

        int pulseCount = 0;
        for (int i = 0; i < n; i++)
        {
            pulseCount += Math.Abs(levels[i]);
        }

        if (pulseCount == 0)
        {
            return 0;
        }

        BigInteger[,] table = BuildUTable(Math.Max(n, pulseCount + 1));
        if (n == 1)
        {
            return levels[0] < 0 ? 1 : 0;
        }

        int j = n - 1;
        BigInteger index = levels[j] < 0 ? 1 : 0;
        int suffixPulseCount = Math.Abs(levels[j]);

        while (j > 0)
        {
            j--;
            int remainingDimensions = n - j;
            index += GetTableValue(table, remainingDimensions, suffixPulseCount);
            suffixPulseCount += Math.Abs(levels[j]);
            if (levels[j] < 0)
            {
                index += GetTableValue(table, remainingDimensions, suffixPulseCount + 1);
            }
        }

        return index;
    }

    public static int[] UnrankPvq(int n, int pulseCount, BigInteger index)
    {
        ValidateDimensions(n, pulseCount);
        if (n == 0)
        {
            return Array.Empty<int>();
        }

        if (pulseCount == 0)
        {
            return new int[n];
        }

        BigInteger total = GetV(n, pulseCount);
        if (index < 0 || index >= total)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "PVQ rank is outside the codebook range.");
        }

        int[] levels = new int[n];
        BigInteger[,] table = BuildUTable(Math.Max(n, pulseCount + 1));
        BigInteger remainingIndex = index;

        if (n == 1)
        {
            levels[0] = remainingIndex.IsZero ? pulseCount : -pulseCount;
            return levels;
        }

        int remainingDimensions = n;
        int remainingPulses = pulseCount;
        int outputIndex = 0;

        while (remainingDimensions > 2)
        {
            if (remainingPulses >= remainingDimensions)
            {
                BigInteger p = GetTableValue(table, remainingDimensions, remainingPulses + 1);
                bool negative = remainingIndex >= p;
                if (negative)
                {
                    remainingIndex -= p;
                }

                int k0 = remainingPulses;
                BigInteger q = GetTableValue(table, remainingDimensions, remainingDimensions);
                if (q > remainingIndex)
                {
                    remainingPulses = remainingDimensions;
                    do
                    {
                        remainingPulses--;
                        p = GetTableValue(table, remainingPulses, remainingDimensions);
                    }
                    while (p > remainingIndex);
                }
                else
                {
                    p = GetTableValue(table, remainingDimensions, remainingPulses);
                    while (p > remainingIndex)
                    {
                        remainingPulses--;
                        p = GetTableValue(table, remainingDimensions, remainingPulses);
                    }
                }

                remainingIndex -= p;
                int value = k0 - remainingPulses;
                levels[outputIndex++] = negative ? -value : value;
            }
            else
            {
                BigInteger p = GetTableValue(table, remainingPulses, remainingDimensions);
                BigInteger q = GetTableValue(table, remainingPulses + 1, remainingDimensions);
                if (p <= remainingIndex && remainingIndex < q)
                {
                    remainingIndex -= p;
                    levels[outputIndex++] = 0;
                }
                else
                {
                    bool negative = remainingIndex >= q;
                    if (negative)
                    {
                        remainingIndex -= q;
                    }

                    int k0 = remainingPulses;
                    do
                    {
                        remainingPulses--;
                        p = GetTableValue(table, remainingPulses, remainingDimensions);
                    }
                    while (p > remainingIndex);

                    remainingIndex -= p;
                    int value = k0 - remainingPulses;
                    levels[outputIndex++] = negative ? -value : value;
                }
            }

            remainingDimensions--;
        }

        // remainingDimensions == 2
        BigInteger lastBranch = (remainingPulses * 2) + 1;
        bool negativeLast = remainingIndex >= lastBranch;
        if (negativeLast)
        {
            remainingIndex -= lastBranch;
        }

        int firstMagnitudeCount = remainingPulses;
        remainingPulses = (int)((remainingIndex + 1) / 2);
        if (remainingPulses > 0)
        {
            remainingIndex -= (remainingPulses * 2) - 1;
        }

        int firstValue = firstMagnitudeCount - remainingPulses;
        levels[outputIndex++] = negativeLast ? -firstValue : firstValue;

        bool negativeFinal = !remainingIndex.IsZero;
        levels[outputIndex] = negativeFinal ? -remainingPulses : remainingPulses;
        return levels;
    }

    private static BigInteger[,] BuildUTable(int max)
    {
        var table = new BigInteger[max + 1, max + 1];
        table[0, 0] = 1;

        for (int n = 1; n <= max; n++)
        {
            for (int k = 1; k <= max; k++)
            {
                table[n, k] = table[n - 1, k] + table[n, k - 1] + table[n - 1, k - 1];
            }
        }

        return table;
    }

    private static BigInteger GetTableValue(BigInteger[,] table, int n, int k)
    {
        int row = Math.Min(n, k);
        int col = Math.Max(n, k);
        return table[row, col];
    }

    private static void ValidateDimensions(int n, int k)
    {
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        if (k < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }
    }

    private static int ComputeSafePulseCap(int width, uint maxCodebookSize)
    {
        if (width <= 0)
        {
            return 0;
        }

        if (width == 1)
        {
            return int.MaxValue;
        }

        double maxLog2 = Math.Log2(Math.Max(1u, maxCodebookSize));
        int safeCap = 0;

        for (int pulseCount = 1; pulseCount < (1 << 20); pulseCount++)
        {
            double upperBoundLog2 = pulseCount + Log2Binomial(width + pulseCount - 1, pulseCount);
            if (upperBoundLog2 > maxLog2)
            {
                break;
            }

            safeCap = pulseCount;
        }

        return safeCap;
    }

    private static double Log2Binomial(int n, int k)
    {
        if (k < 0 || k > n)
        {
            return double.NegativeInfinity;
        }

        k = Math.Min(k, n - k);
        double log2 = 0.0;
        for (int i = 1; i <= k; i++)
        {
            log2 += Math.Log2(n - (k - i)) - Math.Log2(i);
        }

        return log2;
    }
}
