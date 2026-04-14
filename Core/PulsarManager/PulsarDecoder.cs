using System;

public class PulsarDecoder
{
    public (float[] Samples, int SampleRate, int Channels) DecodeArchive(byte[] archiveData)
    {
        if (archiveData is null) throw new ArgumentNullException(nameof(archiveData));
        return PulsarSuperframeArchiveCodec.DecodeArchive(archiveData);
    }
}
