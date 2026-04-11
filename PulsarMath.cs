using System;

public static class PulsarMath
{
    public static void ApplyWindow(float[] data)
    {
        PulsarTransformEngine.ApplyWindow(data);
    }

    public static float[] Mdct(float[] input)
    {
        return PulsarTransformEngine.Mdct(input);
    }

    public static float[] Imdct(float[] input)
    {
        return PulsarTransformEngine.Imdct(input);
    }
}
