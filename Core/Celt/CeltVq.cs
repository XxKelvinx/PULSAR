using System;
using System.Numerics;

/*
   Derived from the Opus CELT source (celt/vq.c).

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2007-2009 Timothy B. Terriberry
   Copyright (c) 2007-2009 Jean-Marc Valin

   Redistribution and use in source and binary forms, with or without
   modification, are permitted under the BSD-style Opus license. See
   THIRD_PARTY_NOTICES.md and opus-main/COPYING for the full text.
*/

public readonly record struct CeltPvqResult(int[] Pulses, float PulseNorm, uint CollapseMask);

public static class CeltVq
{
    public const int SpreadNone = 0;
    public const int SpreadLight = 1;
    public const int SpreadNormal = 2;
    public const int SpreadAggressive = 3;

    private const float Epsilon = 1e-12f;

    public static CeltPvqResult AlgQuant(ReadOnlySpan<float> input, int pulseCount, int spread, int blocks = 1)
    {
        if (input.Length <= 0 || pulseCount <= 0)
        {
            return new CeltPvqResult(Array.Empty<int>(), 1.0f, 0);
        }

        float[] work = input.ToArray();
        RenormaliseVector(work, 1.0f);
        ExpRotation(work, dir: 1, stride: Math.Max(1, blocks), pulseCount, spread);
        int[] pulses = OpusPvqSearch(work, pulseCount);
        float yy = ComputePulseNorm(pulses);
        return new CeltPvqResult(pulses, yy, ExtractCollapseMask(pulses, Math.Max(1, blocks)));
    }

    public static CeltPvqResult QuantPartition(ReadOnlySpan<float> input, int pulseCount, int spread, int splitThreshold = 16)
    {
        if (input.Length <= 0 || pulseCount <= 0)
        {
            return new CeltPvqResult(Array.Empty<int>(), 1.0f, 0);
        }

        float[] work = input.ToArray();
        RenormaliseVector(work, 1.0f);
        // Match AlgQuant: forward ExpRotation in encoder space so the decoder's
        // inverse rotation (dir=-1) lands in the correct domain.
        ExpRotation(work, dir: 1, stride: 1, pulseCount, spread);
        int[] pulses = new int[work.Length];
        QuantPartitionCore(work, pulses, 0, work.Length, pulseCount, splitThreshold);
        return new CeltPvqResult(pulses, ComputePulseNorm(pulses), ExtractCollapseMask(pulses, 1));
    }

    public static void ExpRotation(Span<float> x, int dir, int stride, int pulseCount, int spread)
    {
        int len = x.Length;
        if (len <= 1 || stride <= 0 || 2 * pulseCount >= len || spread == SpreadNone)
        {
            return;
        }

        ReadOnlySpan<int> spreadFactor = [15, 10, 5];
        int factor = spreadFactor[Math.Clamp(spread - 1, 0, spreadFactor.Length - 1)];
        float gain = len / (float)(len + (factor * pulseCount));
        float theta = 0.5f * gain * gain;
        float c = MathF.Cos(0.5f * MathF.PI * theta);
        float s = MathF.Cos(0.5f * MathF.PI * (1.0f - theta));

        int stride2 = 0;
        if (len >= 8 * stride)
        {
            stride2 = 1;
            while (((stride2 * stride2) + stride2) * stride + (stride >> 2) < len)
            {
                stride2++;
            }
        }

        int subLength = len / stride;
        for (int i = 0; i < stride; i++)
        {
            Span<float> lane = x.Slice(i * subLength, subLength);
            if (dir < 0)
            {
                if (stride2 != 0)
                {
                    ExpRotation1(lane, stride2, s, c);
                }

                ExpRotation1(lane, 1, c, s);
            }
            else
            {
                ExpRotation1(lane, 1, c, -s);
                if (stride2 != 0)
                {
                    ExpRotation1(lane, stride2, s, -c);
                }
            }
        }
    }

    public static int[] OpusPvqSearch(ReadOnlySpan<float> input, int pulseCount)
    {
        int n = input.Length;
        int[] iy = new int[n];
        if (n == 0 || pulseCount <= 0)
        {
            return iy;
        }

        float[] x = new float[n];
        int[] signX = new int[n];
        int[] y = new int[n];

        for (int j = 0; j < n; j++)
        {
            signX[j] = input[j] < 0.0f ? -1 : 0;
            x[j] = MathF.Abs(input[j]);
        }

        double xy = 0.0;
        double yy = 0.0;
        int pulsesLeft = pulseCount;

        if (pulseCount > (n >> 1))
        {
            double sum = 0.0;
            for (int j = 0; j < n; j++)
            {
                sum += x[j];
            }

            if (!(sum > Epsilon && sum < 64.0))
            {
                x[0] = 1.0f;
                for (int j = 1; j < n; j++)
                {
                    x[j] = 0.0f;
                }

                sum = 1.0;
            }

            double rcp = (pulseCount + 0.8) / sum;
            for (int j = 0; j < n; j++)
            {
                iy[j] = (int)Math.Floor(rcp * x[j]);
                y[j] = iy[j];
                yy += y[j] * (double)y[j];
                xy += x[j] * y[j];
                y[j] *= 2;
                pulsesLeft -= iy[j];
            }
        }

        while (pulsesLeft < 0)
        {
            int best = 0;
            double bestLoss = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (iy[j] <= 0)
                {
                    continue;
                }

                double loss = x[j] / iy[j];
                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    best = j;
                }
            }

            iy[best]--;
            y[best] -= 2;
            yy -= (2 * iy[best]) + 1;
            xy -= x[best];
            pulsesLeft++;
        }

        if (pulsesLeft > n + 3)
        {
            yy += pulsesLeft * (double)pulsesLeft;
            yy += pulsesLeft * (double)y[0];
            iy[0] += pulsesLeft;
            y[0] += 2 * pulsesLeft;
            pulsesLeft = 0;
        }

        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            double bestNum = double.NegativeInfinity;
            double bestDen = 1.0;

            yy += 1.0;
            for (int j = 0; j < n; j++)
            {
                double rxy = xy + x[j];
                double ryy = yy + y[j];
                double num = rxy * rxy;
                if (num * bestDen > bestNum * ryy)
                {
                    bestDen = ryy;
                    bestNum = num;
                    bestId = j;
                }
            }

            xy += x[bestId];
            yy += y[bestId];
            y[bestId] += 2;
            iy[bestId]++;
        }

        for (int j = 0; j < n; j++)
        {
            iy[j] = (iy[j] ^ signX[j]) - signX[j];
        }

        return iy;
    }

    public static void RenormaliseVector(Span<float> x, float gain)
    {
        double energy = Epsilon;
        int i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            Vector<float> sum = Vector<float>.Zero;
            int vectorEnd = x.Length - Vector<float>.Count;
            for (; i <= vectorEnd; i += Vector<float>.Count)
            {
                var values = new Vector<float>(x.Slice(i, Vector<float>.Count));
                sum += values * values;
            }

            for (int lane = 0; lane < Vector<float>.Count; lane++)
            {
                energy += sum[lane];
            }
        }

        for (; i < x.Length; i++)
        {
            energy += x[i] * (double)x[i];
        }

        float scale = gain / MathF.Sqrt((float)energy);
        for (i = 0; i < x.Length; i++)
        {
            x[i] *= scale;
        }
    }

    private static void QuantPartitionCore(ReadOnlySpan<float> x, Span<int> pulses, int offset, int width, int pulseCount, int splitThreshold)
    {
        if (width <= 0 || pulseCount <= 0)
        {
            return;
        }

        if (width <= splitThreshold || pulseCount < 4)
        {
            int[] local = OpusPvqSearch(x.Slice(offset, width), pulseCount);
            local.AsSpan().CopyTo(pulses.Slice(offset, width));
            return;
        }

        int leftWidth = width >> 1;
        int rightWidth = width - leftWidth;
        double leftEnergy = Energy(x.Slice(offset, leftWidth));
        double rightEnergy = Energy(x.Slice(offset + leftWidth, rightWidth));
        double totalEnergy = leftEnergy + rightEnergy;
        if (totalEnergy <= Epsilon)
        {
            pulses[offset] = pulseCount;
            return;
        }

        int leftPulses = (int)Math.Round(pulseCount * (leftEnergy / totalEnergy));
        if (leftEnergy > Epsilon && rightEnergy > Epsilon)
        {
            leftPulses = Math.Clamp(leftPulses, 1, pulseCount - 1);
        }
        else
        {
            leftPulses = leftEnergy >= rightEnergy ? pulseCount : 0;
        }

        int rightPulses = pulseCount - leftPulses;
        QuantPartitionCore(x, pulses, offset, leftWidth, leftPulses, splitThreshold);
        QuantPartitionCore(x, pulses, offset + leftWidth, rightWidth, rightPulses, splitThreshold);
    }

    private static void ExpRotation1(Span<float> x, int stride, float c, float s)
    {
        float ms = -s;
        for (int i = 0; i < x.Length - stride; i++)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) + (ms * x2);
        }

        for (int i = x.Length - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) + (ms * x2);
        }
    }

    public static uint ExtractCollapseMask(ReadOnlySpan<int> pulses, int blocks)
    {
        if (blocks <= 1)
        {
            return 1;
        }

        int n0 = pulses.Length / blocks;
        uint collapseMask = 0;
        for (int i = 0; i < blocks; i++)
        {
            int tmp = 0;
            for (int j = 0; j < n0; j++)
            {
                tmp |= pulses[(i * n0) + j];
            }

            collapseMask |= (uint)(tmp != 0 ? 1 << i : 0);
        }

        return collapseMask;
    }

    private static double Energy(ReadOnlySpan<float> values)
    {
        double energy = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            energy += values[i] * (double)values[i];
        }

        return energy;
    }

    private static float ComputePulseNorm(ReadOnlySpan<int> pulses)
    {
        double yy = 0.0;
        for (int i = 0; i < pulses.Length; i++)
        {
            yy += pulses[i] * (double)pulses[i];
        }

        return MathF.Sqrt(Math.Max(1.0f, (float)yy));
    }
}
