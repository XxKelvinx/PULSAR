using System;
using Pulsar.Psycho;

public sealed class PulsarQuantizedBand
{
    public required int Start { get; init; }
    public required int Width { get; init; }
    public required int Bits { get; init; }
    public required uint ScaleQ { get; init; }
    public required uint StepQ { get; init; }
    public required uint GammaQ { get; init; }
    public required float Scale { get; init; }
    public required float NormalizedStep { get; init; }
    public required float CompandGamma { get; init; }
    public required int[] Levels { get; init; }
}

public sealed class PulsarQuantizedSpectrum
{
    public required int SpectrumLength { get; init; }
    public required PulsarQuantizedBand[] Bands { get; init; }
}

public static class PulsarQuantizer
{
    private const float GlobalGainStep = 0.015625f;

    // --- ARCHIVER SYNC LIMITS ---
    private const int ScaleQuantLevels = 4096;
    private const int StepQuantLevels = 4096;
    private const int GammaQuantLevels = 256;
    private const float MinGamma = 0.55f;
    private const float MaxGamma = 0.98f;
    private const float MinScale = 1e-12f;
    private const float MaxScale = 65536.0f;
    private const float MinNormalizedStep = 1e-9f;
    private const float MaxNormalizedStep = 32.0f;

    public static void QuantizeSpectrum(float[] spectrum, int[] bandBits, PulsarPsychoResult psycho)
    {
        PulsarQuantizedSpectrum quantized = QuantizeSpectrumDetailed(spectrum, bandBits, psycho);
        DequantizeSpectrum(spectrum, quantized);
    }

    public static void QuantizeSpectrum(float[] spectrum, int[] bandBits, PulsarPsychoResult psycho, int globalGain)
    {
        PulsarQuantizedSpectrum quantized = QuantizeSpectrumDetailed(spectrum, bandBits, psycho, globalGain);
        DequantizeSpectrum(spectrum, quantized);
    }

    public static PulsarQuantizedSpectrum QuantizeSpectrumDetailed(float[] spectrum, int[] bandBits, PulsarPsychoResult psycho)
    {
        return QuantizeSpectrumDetailed(spectrum, bandBits, psycho, 0);
    }

    public static PulsarQuantizedSpectrum QuantizeSpectrumDetailed(float[] spectrum, int[] bandBits, PulsarPsychoResult psycho, int globalGain)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(bandBits);
        ArgumentNullException.ThrowIfNull(psycho);

        if (spectrum.Length == 0 || bandBits.Length == 0 || psycho.SfbBandOffsets.Length < 2)
        {
            return new PulsarQuantizedSpectrum { SpectrumLength = spectrum.Length, Bands = Array.Empty<PulsarQuantizedBand>() };
        }

        int psychoBinCount = Math.Max(1, psycho.SfbBandOffsets[^1]);
        var bands = new PulsarQuantizedBand[bandBits.Length];

        for (int bandIndex = 0; bandIndex < bandBits.Length; bandIndex++)
        {
            int start = MapBandOffset(psycho.SfbBandOffsets[Math.Min(bandIndex, psycho.SfbBandOffsets.Length - 1)], psychoBinCount, spectrum.Length);
            int end = MapBandOffset(psycho.SfbBandOffsets[Math.Min(bandIndex + 1, psycho.SfbBandOffsets.Length - 1)], psychoBinCount, spectrum.Length);
            
            if (end <= start)
            {
                bands[bandIndex] = new PulsarQuantizedBand { Start = start, Width = 0, Bits = 0, ScaleQ = 0, StepQ = 0, GammaQ = 0, Scale = 1.0f, NormalizedStep = 1.0f, CompandGamma = 1.0f, Levels = Array.Empty<int>() };
                continue;
            }

            int width = end - start;
            int bits = Math.Max(0, bandBits[bandIndex]);

            float peak = 0.0f;
            double energy = 0.0;
            for (int i = start; i < end; i++)
            {
                float magnitude = MathF.Abs(spectrum[i]);
                peak = MathF.Max(peak, magnitude);
                energy += spectrum[i] * spectrum[i];
            }

            if (peak <= 1e-12f)
            {
                bands[bandIndex] = new PulsarQuantizedBand { Start = start, Width = 0, Bits = 0, ScaleQ = 0, StepQ = 0, GammaQ = 0, Scale = 1.0f, NormalizedStep = 1.0f, CompandGamma = 1.0f, Levels = Array.Empty<int>() };
                continue;
            }

            float rms = MathF.Sqrt((float)(energy / Math.Max(1, width)));
            
            // 1. ROHE PARAMETER BERECHNEN
            float rawScale = MathF.Max(peak * 0.65f, rms * 1.6f);
            float headroomDb = GetBandValue(psycho.SfbBandEnergiesDb, bandIndex) - GetBandValue(psycho.MaskingThresholdDb, bandIndex);
            float smrDb = GetBandValue(psycho.SmrDb, bandIndex);
            float targetSnrDb = MathF.Max(0.0f, smrDb + 12.0f);
            float bitsPerCoeff = Math.Clamp(targetSnrDb / 6.0f, 0.5f, 5.0f);
            float tonality = GetBandValue(psycho.Tonality, bandIndex);
            float centerHz = GetBandValue(psycho.SfbBandCenters, bandIndex);

            float lowBandBoost = centerHz <= 260.0f ? 1.35f : centerHz <= 1200.0f ? 1.12f : 1.0f;
            float tonalBoost = 1.0f + (0.35f * Math.Clamp(tonality, 0.0f, 1.0f));
            float maskingBoost = 1.0f + (0.22f * Math.Clamp(headroomDb / 18.0f, 0.0f, 1.5f));
            float smrBoost = 1.0f + (0.18f * Math.Clamp(smrDb / 18.0f, 0.0f, 1.5f));
            float transientPenalty = centerHz >= 3500.0f ? 1.0f + (0.12f * Math.Clamp(psycho.TransientScore, 0.0f, 1.5f)) : 1.0f;
            float effectiveDensity = Math.Clamp(bitsPerCoeff * lowBandBoost * tonalBoost * maskingBoost * smrBoost, 0.0f, 24.0f);
            float quantStrength = 1.0f / (1.0f + effectiveDensity + (0.5f * transientPenalty));
            
            float rawCompandGamma = Math.Clamp(0.72f + (0.18f * tonality) - (0.10f * Math.Clamp(psycho.TransientScore, 0.0f, 1.0f)), MinGamma, MaxGamma);
            float deadZone = rawScale * (0.008f + (0.14f * quantStrength));
            float baseStep = MathF.Max(rawScale * (0.0015f + (0.22f * quantStrength * quantStrength)), 1e-9f);
            float globalGainFactor = MathF.Pow(2.0f, Math.Max(0, globalGain) * GlobalGainStep);
            float step = MathF.Max(baseStep * globalGainFactor, 1e-9f);
            float rawNormalizedStep = step / rawScale;

            // --- 2. DER LEBENSRETTENDE SYNC-FIX ---
            // Simuliert den Archiver. Der Decoder sieht später exakt diese "synced" Werte!
            uint scaleQ = QuantizeLogValue(rawScale, MinScale, MaxScale, ScaleQuantLevels);
            float syncedScale = DequantizeLogValue(scaleQ, MinScale, MaxScale, ScaleQuantLevels);

            uint gammaQ = QuantizeLinearValue(rawCompandGamma, MinGamma, MaxGamma, GammaQuantLevels);
            float syncedGamma = DequantizeLinearValue(gammaQ, MinGamma, MaxGamma, GammaQuantLevels);

            uint stepQ = QuantizeLogValue(rawNormalizedStep, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels);
            float syncedNormalizedStep = DequantizeLogValue(stepQ, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels);

            int[] levels = new int[width];

            // 3. LEVELS BERECHNEN (Mit den synchronisierten Werten!)
            for (int i = start; i < end; i++)
            {
                float value = spectrum[i];
                if (MathF.Abs(value) < deadZone)
                {
                    levels[i - start] = 0;
                    continue;
                }

                float normalized = value / syncedScale;
                float shaped = MathF.Sign(normalized) * MathF.Pow(MathF.Abs(normalized), syncedGamma);
                levels[i - start] = (int)MathF.Round(shaped / syncedNormalizedStep);
            }

            bands[bandIndex] = new PulsarQuantizedBand
            {
                Start = start, Width = width, Bits = bits,
                ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ,
                Scale = syncedScale, NormalizedStep = syncedNormalizedStep, CompandGamma = syncedGamma, Levels = levels,
            };
        }

        return new PulsarQuantizedSpectrum { SpectrumLength = spectrum.Length, Bands = bands };
    }

    public static void DequantizeSpectrum(float[] spectrum, PulsarQuantizedSpectrum quantized)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(quantized);
        Array.Clear(spectrum, 0, spectrum.Length);

        foreach (PulsarQuantizedBand band in quantized.Bands)
        {
            if (band.Width <= 0 || band.Levels.Length == 0 || band.Start >= spectrum.Length) continue;

            int width = Math.Min(band.Width, Math.Min(band.Levels.Length, spectrum.Length - band.Start));
            float scale = MathF.Max(band.Scale, 1e-12f);
            float normalizedStep = MathF.Max(band.NormalizedStep, 1e-9f);
            float gamma = Math.Clamp(band.CompandGamma, MinGamma, MaxGamma);

            for (int i = 0; i < width; i++)
            {
                int level = band.Levels[i];
                if (level == 0) continue;

                float quantizedValue = level * normalizedStep;
                float restored = MathF.Sign(quantizedValue) * MathF.Pow(MathF.Abs(quantizedValue), 1.0f / gamma);
                spectrum[band.Start + i] = restored * scale; // KEIN CLAMP!
            }
        }
    }

    // --- HELPER ---
    private static uint QuantizeLinearValue(float value, float minValue, float maxValue, int totalLevels)
    {
        float clamped = Math.Clamp(value, minValue, maxValue);
        float normalized = (clamped - minValue) / Math.Max(1e-12f, maxValue - minValue);
        return (uint)Math.Clamp((int)MathF.Round(normalized * (totalLevels - 1)), 0, totalLevels - 1);
    }
    private static float DequantizeLinearValue(uint value, float minValue, float maxValue, int totalLevels)
    {
        float normalized = value / (float)Math.Max(1, totalLevels - 1);
        return minValue + ((maxValue - minValue) * normalized);
    }
    private static uint QuantizeLogValue(float value, float minValue, float maxValue, int totalLevels)
    {
        float clamped = Math.Clamp(value, minValue, maxValue);
        float minLog = MathF.Log2(minValue);
        float maxLog = MathF.Log2(maxValue);
        float normalized = (MathF.Log2(clamped) - minLog) / Math.Max(1e-12f, maxLog - minLog);
        return (uint)Math.Clamp((int)MathF.Round(normalized * (totalLevels - 1)), 0, totalLevels - 1);
    }
    private static float DequantizeLogValue(uint value, float minValue, float maxValue, int totalLevels)
    {
        float normalized = value / (float)Math.Max(1, totalLevels - 1);
        float minLog = MathF.Log2(minValue);
        float maxLog = MathF.Log2(maxValue);
        return MathF.Pow(2.0f, minLog + ((maxLog - minLog) * normalized));
    }
    private static int MapBandOffset(int sourceOffset, int sourceBinCount, int targetBinCount)
    {
        if (sourceBinCount <= 0 || targetBinCount <= 0) return 0;
        return Math.Clamp((int)MathF.Round(sourceOffset * (targetBinCount / (float)sourceBinCount)), 0, targetBinCount);
    }
    private static float GetBandValue(float[] values, int bandIndex)
    {
        if (values.Length == 0) return 0.0f;
        return values[Math.Min(bandIndex, values.Length - 1)];
    }
}