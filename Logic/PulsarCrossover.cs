using System;

public class PulsarCrossover
{
    private readonly float[] _firCoefficients;
    private readonly float[] _buffer;
    private readonly int _centerTap;
    private int _bufferIndex;

    public PulsarCrossover(int numTaps = 1023, float cutoffHz = 300f, float sampleRate = 44100f)
    {
        if ((numTaps & 1) == 0)
        {
            numTaps += 1;
        }

        _centerTap = numTaps / 2;
        _firCoefficients = GenerateWindowedSinc(cutoffHz, sampleRate, numTaps);
        _buffer = new float[numTaps];
    }

    public (float[] Low, float[] High) Process(float[] input)
    {
        float[] low = new float[input.Length];
        float[] high = new float[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            _buffer[_bufferIndex] = input[i];

            float sum = _firCoefficients[_centerTap] * ReadDelayedSample(_centerTap);
            for (int tap = 0; tap < _centerTap; tap++)
            {
                float pairSample = ReadDelayedSample(tap) + ReadDelayedSample((_firCoefficients.Length - 1) - tap);
                sum += _firCoefficients[tap] * pairSample;
            }

            low[i] = sum;
            float delayedOriginal = ReadDelayedSample(_centerTap);
            high[i] = delayedOriginal - low[i];

            _bufferIndex = (_bufferIndex + 1) % _buffer.Length;
        }

        return (low, high);
    }

    private float ReadDelayedSample(int delay)
    {
        int index = _bufferIndex - delay;
        if (index < 0)
        {
            index += _buffer.Length;
        }

        return _buffer[index];
    }

    private static float[] GenerateWindowedSinc(float cutoffHz, float sampleRate, int taps)
    {
        float[] coefficients = new float[taps];
        int center = taps / 2;
        float normalizedCutoff = cutoffHz / sampleRate;
        double sum = 0.0;

        for (int i = 0; i < taps; i++)
        {
            int n = i - center;
            double sinc = n == 0
                ? 2.0 * normalizedCutoff
                : Math.Sin(2.0 * Math.PI * normalizedCutoff * n) / (Math.PI * n);

            double hamming = 0.54 - 0.46 * Math.Cos((2.0 * Math.PI * i) / (taps - 1));
            coefficients[i] = (float)(sinc * hamming);
            sum += coefficients[i];
        }

        for (int i = 0; i < taps; i++)
        {
            coefficients[i] = (float)(coefficients[i] / sum);
        }

        return coefficients;
    }
}