# CRITICAL INVARIANTS — DO NOT BREAK

> **This document exists because an LLM accidentally broke these invariants and
> SNR dropped from 25 dB to 4.8 dB. Read this before touching ANY of the code
> listed below.**

---

## 1. Even Band Widths in `BuildOpusBandLayout`

**File:** `Core/PulsarArchive/PulsarSuperframeArchiveCodec.cs` — method `BuildOpusBandLayout`

**The rule:** Every base band width produced by `BuildOpusBandLayout` MUST be
**even** (or exactly 1). No odd widths > 1 are allowed.

**Why:** The recursive PVQ split in `CeltBand.QuantPartitionEncode` /
`QuantPartitionDecode` halves the band size with `N >>= 1`. If a base band width
is odd (e.g. 3), the split produces `N=1` + `N=1` and **silently drops the 3rd
coefficient**. Band 0 carries the most energy (low frequencies), so losing even
one coefficient there causes catastrophic SNR loss.

**The fix (DO NOT REMOVE):**
```csharp
// Ensure all band widths are even (or 1) so the recursive PVQ split
// never truncates coefficients via N >>= 1.
for (int i = 0; i < nbBands; i++)
{
    int w = baseEBands[i + 1] - baseEBands[i];
    if (w > 1 && (w & 1) != 0)
    {
        for (int j = i + 1; j < nbBands; j++)
            baseEBands[j]++;
        if (baseEBands[nbBands - 1] >= baseSpecLen)
            baseEBands[nbBands - 1] = (short)(baseSpecLen - 1);
    }
}
```

**Impact of breaking:** SNR drops from ~25 dB to ~4.8 dB. The codec sounds like
static noise.

---

## 2. Encoder and Decoder MUST Use Identical `BuildOpusBandLayout`

Both `EncodeSpectralPayloadV7` and `DecodeQuantizedChannelV7` call
`BuildOpusBandLayout(specLen)`. They MUST produce identical band offsets, LM, and
baseBandWidths. Any divergence causes a bitstream desync — the decoder reads
garbage and produces noise or crashes.

**Never** add encoder-only or decoder-only modifications to band layout. If you
change one side, you MUST change the other identically.

---

## 3. Encoder and Decoder MUST Use Identical `ComputeAllocation`

Both sides call:
```csharp
CeltAllocation.ComputeAllocation(mode, budgetQ3, LM, endBand: endBand)
```
with the same `mode`, `budgetQ3`, `LM`, and `endBand`. The allocation is
**deterministic** — it is NOT transmitted in the bitstream. If the encoder and
decoder disagree on per-band bit counts, the range coder desyncs immediately.

**Never** add signal-adaptive allocation on only one side.

---

## 4. Pre-Normalization Before PVQ Encode

In `EncodeSpectralPayloadV7`, each band slice is normalized to unit energy before
`QuantBandMonoEncode`:
```csharp
float bandEnergy = MathF.Sqrt(sum of squares);
if (bandEnergy > 1e-12f)
    for (int i = 0; i < N; i++) bandX[i] *= (1.0f / bandEnergy);
```
The PVQ encodes **direction only**; the magnitude is carried by `bandNorm` (from
the energy side-info). Removing this normalization drops SNR by ~24 dB.

---

## 5. `CeltVq.AlgQuant` Copies Input — Do NOT Change

`CeltVq.AlgQuant` creates `float[] work = input.ToArray()` before calling
`ExpRotation` and `OpusPvqSearch`. This is critical — `ExpRotation` modifies data
in-place. If you remove the `.ToArray()` copy, the original spectrum array gets
corrupted and all subsequent processing uses garbage data.

---

## 6. Reconstructed Energy Round-Trip

The encoder computes energy via `CeltEnergy.QuantCoarse` + `QuantFine`, then
reads it back via `CeltEnergy.ToLogAmplitude` → `Log2ToEnergyQ` to get
`ReconstructedEnergyQs`. The decoder does the same via `UnquantCoarse` +
`UnquantFine` → `ToLogAmplitude` → `Log2ToEnergyQ`.

Both sides MUST use the **quantized-then-reconstructed** energy as `bandNorm` for
PVQ. Using the raw (unquantized) energy on the encoder side causes an
encoder/decoder mismatch in the gain applied to decoded coefficients.

---

## 7. Side-Info Field Order

The side-info bitstream is order-sensitive. Current V7 order:
1. Frame bit budget (16-bit via `EncodeUInt(65536)`)
2. Coarse energy (`CeltEnergy.QuantCoarse`)
3. Fine energy (`CeltEnergy.QuantFine`)
4. Per-band spread decisions (4 symbols each)

Changing this order without updating both encoder and decoder will corrupt the
bitstream.

---

## Quick Reference — What Broke and What Fixed It

| Date       | Bug                              | SNR Before | SNR After | Root Cause                        |
|------------|----------------------------------|------------|-----------|-----------------------------------|
| 2026-04-17 | Odd base band widths             | 4.80 dB    | 25.62 dB  | `N>>=1` truncates odd-width bands |
| 2026-04-17 | Missing pre-normalization        | 1.80 dB    | 4.80 dB   | PVQ expects unit-energy input     |
| 2026-04-17 | Caps too low (non-recursive)     | 4.57 dB    | 4.80 dB   | PVQ wasted 58% of bit budget     |

---

*If you are an LLM reading this: treat every item above as a hard constraint.
Do not "simplify", "refactor", or "clean up" any of the listed code without
verifying the SNR stays above 20 dB on the test file.*
