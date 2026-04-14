using System;

public class PulsarEncoder
{
    public byte[] EncodeSpectralArchive(float[] inputSamples, int sampleRate, int channels, int targetKbps, int quality = 4)
    {
        if (inputSamples is null) throw new ArgumentNullException(nameof(inputSamples));
        return PulsarSuperframeArchiveCodec.EncodeSpectralArchive(inputSamples, sampleRate, channels, targetKbps, quality);
    }

    public byte[] EncodePcmArchive(float[] inputSamples, int sampleRate, int channels)
    {
        if (inputSamples is null) throw new ArgumentNullException(nameof(inputSamples));
        return PulsarSuperframeArchiveCodec.EncodePcmArchive(inputSamples, sampleRate, channels);
    }
}
