using System;

// Bit-exact math helpers ported from Opus celt/mathops.h and bands.c. These
// must stay byte-identical with Opus: they drive the bit allocation decisions
// via compute_qn / compute_theta, so any drift corrupts the stream.
public static class CeltMath
{
    // Opus FRAC_MUL16: (16384 + (int16_a * int16_b)) >> 15. Saturating Q15 mul.
    public static int FracMul16(int a, int b)
    {
        short sa = (short)a;
        short sb = (short)b;
        return (16384 + sa * sb) >> 15;
    }

    public static int EcIlog(uint v)
    {
        int r = 0;
        while (v > 0)
        {
            r++;
            v >>= 1;
        }
        return r;
    }

    // Integer sqrt of a 32-bit unsigned value, matching Opus isqrt32().
    public static uint Isqrt32(uint val)
    {
        if (val == 0) return 0;
        uint g = 0;
        int bshift = (EcIlog(val) - 1) >> 1;
        uint b = 1u << bshift;
        do
        {
            uint t = ((g << 1) + b) << bshift;
            if (t <= val)
            {
                g += b;
                val -= t;
            }
            b >>= 1;
            bshift--;
        } while (bshift >= 0);
        return g;
    }

    public static short BitexactCos(short x)
    {
        int tmp = (4096 + x * x) >> 13;
        int x2 = tmp;
        x2 = (32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2)));
        return (short)(1 + x2);
    }

    public static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = EcIlog((uint)icos);
        int ls = EcIlog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return (ls - lc) * (1 << 11)
             + FracMul16(isin, FracMul16(isin, -2597) + 7932)
             - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    // Opus celt_sudiv: signed/signed division that traps on 0 denom in debug.
    public static int CeltSudiv(int n, int d) => n / d;
}
