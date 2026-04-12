# PULSAR

![PULSAR banner](Pictures/PulsarBanner.png)

PULSAR is an experimental offline transform codec prototype focused on adaptive block switching, transient-aware planning, and transparent reconstruction.

The current project is built around three ideas:

- analyze the whole signal offline instead of making purely local decisions
- choose block structure from the signal itself, especially around transients
- keep the transform path mathematically clean enough to null-test near float precision

## Current State

PULSAR is still a prototype, but the core pieces are already in place:

- `Logic/PulsarPlanner.cs` performs full-song analysis and chooses an adaptive block path
- `Core/PulsarTransformEngine.cs` renders the transform path with MDCT/IMDCT
- `Logic/PulsarAllocator.cs` contains an early bitrate allocation model
- `Program.cs` exposes the experimental CLI modes

Recent work moved the planner to a 2048-superframe context and fixed the transform edge handling, which dramatically improved planner speed and restored near-perfect reconstruction.

## Why It Exists

PULSAR is not trying to be a low-latency realtime codec first. It is aimed at offline quality:

- large lookahead is acceptable
- multi-pass analysis is acceptable
- planner complexity is acceptable if it improves audible results

That makes it a good playground for global path planning, transient handling, and future bitstream research.

## Project Layout

- `Core/` transform engine, block ladder, packing primitives
- `Logic/` planner, allocator, crossover logic
- `Psycho/` transient and perceptual analysis
- `IO/` bitstream and container scaffolding
- `Pictures/` repository artwork used by this README

## CLI

Current test entry points live in `Program.cs`.

Examples:

```powershell
dotnet run --project PulsarCodec.csproj -- --legacy "input.wav" "output.wav" 1024
dotnet run --project PulsarCodec.csproj -- --legacyP "input.wav" "output.wav"
dotnet run --project PulsarCodec.csproj -- --legacyP-fast "input.wav" "output.wav"
dotnet run --project PulsarCodec.csproj -- --vbr 128 "input.wav" "output.wav"
dotnet run --project PulsarCodec.csproj -- --compare "original.wav" "processed.wav"
```

## Notes

- This repository intentionally tracks the codec code, not large generated outputs.
- The current WAV render path is mainly for experimentation and validation.
- A final Pulsar bitstream/container format is still a work in progress.

## Build

```powershell
dotnet build PulsarCodec.csproj
```

## Roadmap

- connect planner output directly to a real Pulsar bitstream
- tighten quantization and allocation around perceptual targets
- make the render path superframe-native end to end
- evolve the prototype into a complete encoder/decoder flow
