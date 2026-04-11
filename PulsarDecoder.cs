using System;
using System.Collections.Generic;

public class PulsarDecoder
{
    public float[] Decode(List<PulsarEncodedFrame> encodedFrames)
    {
        throw new NotSupportedException("Pulsar currently tunes the direct WAV render path. Frame decoding returns in a later bitstream phase.");
    }
}
