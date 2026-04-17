# PULSAR

![PULSAR banner](Pictures/PulsarBanner.png)

PULSAR is an experimental offline transform codec prototype. It is designed for archive-quality audio processing, not realtime low-latency streaming. The codec focuses on global analysis, adaptive block switching, psychoacoustic budgeting, and a clean transform/quantization workflow. The current implementation supports only `-V 0` through `-V 9` and targets PCM output rather than enforcing a fixed bitrate.

## Purpose

PULSAR aims to be a high-quality offline codec that saves bits where the audio is easy and invests them where the signal is perceptually demanding. It treats analysis, block selection, allocation, and reconstruction as a unified system rather than independent steps.

## Philosophy

- Offline analysis and multi-pass planning are acceptable.
- Large lookahead is a feature, not a bug.
- Transient-aware block switching should follow the signal, not a fixed grid.
- Psychoacoustic and masking models should guide allocation, not arbitrary heuristics.
- The system should reuse proven reference ideas from existing open-source codecs instead of inventing weak substitutes.

## Core Architecture

### Current focus

- `Logic/PulsarPlanner.cs` performs full-song analysis and chooses an adaptive block path with cost-based switching.
- `Psycho/PulsarPsychoCore.cs` generates psychoacoustic frame data, including band energies, tonality, masking thresholds, SMR, and perceptual entropy.
- `Logic/PulsarAllocator.cs` turns psycho data into frame budgets and band bit distributions.
- `Core/PulsarTransformEngine.cs` renders the planned blocks with MDCT/IMDCT and a block-ladder switching path.
- `Core/PulsarQuantizer.cs` converts bandbits and psycho results into quantized bands and dequantizes them for rendering.
- `Core/PulsarSuperframeArchiveCodec.cs` and `IO/PulsarRangeCoder.cs` drive the experimental archive/PLSR path.

### Current workflow

1. Read PCM input.
2. `PulsarPlanner` analyzes the full signal and selects block sizes.
3. `PulsarPsychoCore` performs psychoacoustic analysis on the same frames.
4. `PulsarAllocator` produces budgets and bandbit allocations.
5. `PulsarTransformEngine` renders the chosen block path.
6. `PulsarQuantizer` quantizes and dequantizes spectral bands.
7. `Program.cs` writes output WAV files and logs results.

## Experimental Modes

The current CLI supports:

- `--legacy`
- `--legacyP`
- `--legacyP-fast`
- `--vbr`
- `--vbrplsr`
- `--vbrplsrpcm`
- `--decodeplsr`
- `--compare`

Supported quality levels are currently `-V 1` through `-V 3`. These are approximate VBR targets, not hard CBR promises:

| Level | Nominal target |
| --- | ---: |
| `-V 1` | ~320 kbps |
| `-V 2` | ~256 kbps |
| `-V 3` | ~128 kbps |

The focus is on PCM rendering and quality-controlled VBR behavior, not on a fixed bitrate mode.

## What is implemented today

- A prototype transform/render path with MDCT/IMDCT and block switching.
- Psychoacoustic analysis including PE, masking spreading, tonality, and SMR.
- A first psycho-driven VBR allocation model.
- An experimental archive codec path for spectral PCM and PLSR bitstream experiments.

## Limitations

- This is still an experimental prototype.
- There is no final Pulsar bitstream format yet.
- Quantization is currently a reference path, not a finalized codec engine.
- The system is optimized for research and validation, not production distribution.

## Project layout

- `Core/` transform engine, quantizer, archive codec, packers
- `Logic/` planner, allocator, demand modelling
- `Psycho/` perceptual analysis and transient detection
- `IO/` bitstream reader/writer and archive scaffolding
- `Pictures/` artwork and README images
- `TestWAVs/` sample WAV files and experimental inputs
- `Reference Open Source/` source references from external codec implementations

## Build

```powershell
dotnet build PulsarCodec.csproj
```

## Notes

- This repository is private and tracks experimental data, reference material, and development artifacts.
- `Reference Open Source/` and `TestWAVs/` are intentionally included in the repo for inspection.
- `Tools/compare_audio_metrics.py` compares RAW tracks against lossy outputs and Pulsar renders, reporting SNR, PSNR, residuals, bitrate, and file size. It expects `ffmpeg` and `ffprobe` on `PATH`.
- The current focus is on architecture validation, not final packaging.
