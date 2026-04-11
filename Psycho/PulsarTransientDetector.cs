using System;

namespace Pulsar.Psycho;

public sealed class PulsarTransientDetector
{
    private static readonly byte[] InvTable = new byte[]
    {
         255,255,156,110, 86, 70, 59, 51, 45, 40, 37, 33, 31, 28, 26, 25,
         23, 22, 21, 20, 19, 18, 17, 16, 16, 15, 15, 14, 13, 13, 12, 12,
         12, 12, 11, 11, 11, 10, 10, 10,  9,  9,  9,  9,  9,  9,  8,  8,
          8,  8,  8,  7,  7,  7,  7,  7,  7,  6,  6,  6,  6,  6,  6,  6,
          6,  6,  6,  6,  6,  6,  6,  6,  6,  5,  5,  5,  5,  5,  5,  5,
          5,  5,  5,  5,  5,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,
          4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  3,  3,
          3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  2,
    };

    private const float ForwardDecay = 0.0625f;
    private const float WeakTransientForwardDecay = 0.03125f;
    private const float ToneishnessThreshold = 0.98f;
    private const float ToneFreqThreshold = 0.026f;
    private const float Epsilon = 1e-10f;

    /// <summary>
    /// Performs the Opus CELT transient analysis ported from celt_encoder.c.
    /// </summary>
    /// <param name="input">Input samples in channel-major order: channel0[0..len-1], channel1[0..len-1], ...</param>
    /// <param name="len">Number of samples per channel.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="tfEstimate">Estimated transient strength for time-frequency switching.</param>
    /// <param name="tfChan">Index of the channel with the highest transient metric.</param>
    /// <param name="allowWeakTransients">If true, low-bitrate weak transient detection is enabled.</param>
    /// <param name="weakTransient">Output flag for weak transients.</param>
    /// <param name="toneFreq">Tone frequency indicator used to suppress low-frequency tone false positives.</param>
    /// <param name="toneishness">Toneishness measure used to suppress tone false positives.</param>
    /// <returns>True if a transient is detected, false otherwise.</returns>
    public bool TransientAnalysis(
        float[] input,
        int len,
        int channels,
        out float tfEstimate,
        out int tfChan,
        bool allowWeakTransients,
        out bool weakTransient,
        float toneFreq,
        float toneishness)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (len <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(len));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (input.Length < len * channels)
        {
            throw new ArgumentException("Input length must be at least len * channels.", nameof(input));
        }

        var tmp = new float[len];
        weakTransient = false;
        float forwardDecay = allowWeakTransients ? WeakTransientForwardDecay : ForwardDecay;

        int maskMetric = 0;
        tfChan = 0;

        for (int c = 0; c < channels; c++)
        {
            float mean = 0.0f;
            int unmask = 0;
            float norm;
            float maxE = 0.0f;
            float mem0 = 0.0f;
            float mem1 = 0.0f;

            // High-pass filter: (1 - 2*z^-1 + z^-2) / (1 - z^-1 + 0.5*z^-2)
            for (int i = 0; i < len; i++)
            {
                float x = input[i + c * len];
                float y = mem0 + x;
                float mem00 = mem0;
                mem0 = mem0 - x + 0.5f * mem1;
                mem1 = x - mem00;
                tmp[i] = y * 0.25f;
            }

            // First few samples are bad because we don't propagate the memory.
            for (int i = 0; i < 12 && i < len; i++)
            {
                tmp[i] = 0.0f;
            }

            int len2 = len / 2;
            mean = 0.0f;
            mem0 = 0.0f;

            // Forward pass to compute the post-echo threshold.
            for (int i = 0; i < len2; i++)
            {
                float x2 = (tmp[2 * i] * tmp[2 * i] + tmp[2 * i + 1] * tmp[2 * i + 1]) * 0.0625f;
                mean += x2 * (1.0f / 4096.0f);
                mem0 = x2 + (1.0f - forwardDecay) * mem0;
                tmp[i] = forwardDecay * mem0;
            }

            mem0 = 0.0f;
            maxE = 0.0f;

            // Backward pass to compute the pre-echo threshold.
            for (int i = len2 - 1; i >= 0; i--)
            {
                mem0 = tmp[i] + 0.875f * mem0;
                tmp[i] = 0.125f * mem0;
                maxE = Math.Max(maxE, 0.125f * mem0);
            }

            // Geometric-energy frame measure.
            mean = MathF.Sqrt(mean * maxE * 0.5f * len2);
            norm = len2 * 1048576.0f / (mean * 0.5f + Epsilon);

            for (int i = 12; i < len2 - 5; i += 4)
            {
                int id = (int)MathF.Floor(64.0f * norm * (tmp[i] + Epsilon));
                id = Math.Clamp(id, 0, 127);
                unmask += InvTable[id];
            }

            int channelMaskMetric = 256 * unmask / (6 * Math.Max(len2 - 17, 1));
            if (channelMaskMetric > maskMetric)
            {
                maskMetric = channelMaskMetric;
                tfChan = c;
            }
        }

        bool isTransient = maskMetric > 200;

        if (toneishness > ToneishnessThreshold && toneFreq < ToneFreqThreshold)
        {
            isTransient = false;
            maskMetric = 0;
        }

        if (allowWeakTransients && isTransient && maskMetric < 600)
        {
            isTransient = false;
            weakTransient = true;
        }

        float tfMax = MathF.Max(0.0f, MathF.Sqrt(27.0f * maskMetric) - 42.0f);
        float tfValue = MathF.Max(0.0f, 0.0069f * MathF.Min(163.0f, tfMax) - 0.139f);
        tfEstimate = MathF.Sqrt(tfValue);

        return isTransient;
    }

    private static bool PatchTransientDecision(
        float[] newE,
        float[] oldE,
        int nbEBands,
        int start,
        int end,
        int channels)
    {
        if (newE is null)
        {
            throw new ArgumentNullException(nameof(newE));
        }

        if (oldE is null)
        {
            throw new ArgumentNullException(nameof(oldE));
        }

        if (nbEBands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nbEBands));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (start < 0 || end <= start || end > nbEBands)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Invalid start/end band range.");
        }

        var spreadOld = new float[26];
        float meanDiff = 0.0f;

        if (channels == 1)
        {
            spreadOld[start] = oldE[start];
            for (int i = start + 1; i < end; i++)
            {
                spreadOld[i] = Math.Max(spreadOld[i - 1] - 1.0f, oldE[i]);
            }
        }
        else
        {
            spreadOld[start] = Math.Max(oldE[start], oldE[start + nbEBands]);
            for (int i = start + 1; i < end; i++)
            {
                float previous = spreadOld[i - 1] - 1.0f;
                float current = Math.Max(oldE[i], oldE[i + nbEBands]);
                spreadOld[i] = Math.Max(previous, current);
            }
        }

        for (int i = end - 2; i >= start; i--)
        {
            spreadOld[i] = Math.Max(spreadOld[i], spreadOld[i + 1] - 1.0f);
        }

        for (int c = 0; c < channels; c++)
        {
            for (int i = Math.Max(2, start); i < end - 1; i++)
            {
                float x1 = Math.Max(0.0f, newE[i + c * nbEBands]);
                float x2 = Math.Max(0.0f, spreadOld[i]);
                meanDiff += Math.Max(0.0f, x1 - x2);
            }
        }

        meanDiff /= channels * (end - 1 - Math.Max(2, start));
        return meanDiff > 1.0f;
    }
}
