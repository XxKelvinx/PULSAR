using System;
using System.Collections.Generic;

public class PulsarEncoder
{
    public List<PulsarEncodedFrame> Encode(float[] inputSamples)
    {
        throw new NotSupportedException("Pulsar currently tunes the direct WAV render path. Frame encoding returns in a later bitstream phase.");
    }
}
