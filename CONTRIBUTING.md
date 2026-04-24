# Contributing to the Pulsar Encoder

First off, **thank you** for considering contributing to Pulsar! This project is an experimental open-source undertaking—a specialized psychoacoustic offline VBR planner—and we need the community to make it better.

If you are a DSP engineer, C-developer, psychoacoustics researcher, or Python optimization wizard, there is a place for you here.

## Where We Need Your Help

Here is a list of the biggest areas for improvement!

### 1. Hardening the C-Port (`cli/pulsar_planner.c`) (Highest Priority)
**The Problem:** I have just porteted the python prototype into an initial standalone C program (`cli/pulsar_planner.c`). It implements the 150Hz Butterworth highpass filter and the asymmetric envelope array-math. However, currently it assumes a very naive Spectral Flux substitution (basic energy diff instead of a proper FFT-based spectral flux to avoid heavy FFTW dependencies).
**The Solution:** 
1. We need C developers to refine the exact FFT magnitude diff logic used in NumPy into `pulsar_planner.c` (perhaps using KISS FFT which is already in the Opus tree `celt/kiss_fft.c`).
2. Integrate this final C logic natively into `opus_encoder` so we don't have to compile `pulsar_planner.c` separately!
**Skills Needed:** C programming, DSP.

### 2. Tuning the Psychoacoustic Envelope (Parameters)
**The Problem:** We derived our sweet-spot parameters (`150Hz HPF`, `0.85 pre-echo`, `0.30 post-mask`) on Electronic/Dance tracks with heavy bass transients.
**The Solution:** Run the testing scripts (`scripts/search_dataset_variants.py`) across Classical, Jazz, Speech, and Acoustic datasets to see if these parameters hold up. Should the High-Pass shift dynamically based on track frequency? Suggest parameter branches via PRs!
**Skills Needed:** Critical listening, dataset analysis, running Python evaluation loops.

### 3. A Real C++ Binary Wrapper
**The Problem:** The current `encode_pulsar.py` provides Python CLI arguments, but people compiling Opus usually just expect a single `opusenc` executable instead of pip dependencies.
**The Solution:** Wrap the logic natively so people can type `pulsar -i song.wav -b 128` instead of orchestrating FFmpeg + Python.

## How to Submit a Pull Request (PR)

1. **Fork the repo** and create your branch from `main`.
2. Ensure you have tested your changes. If you tweak the psychoacoustic VBR map in `pulsar_planner.py`, please use the evaluation scripts inside `scripts/` (e.g., `score_telemetry.py`) to prove the Transient Flux MSE loss decreased and provide those numbers in your PR.
3. Make sure your Python code is nicely formatted (`black` / `flake8`).
4. Update the README or documentation with any new command-line flags.

We look forward to seeing what you build. Happy encoding!
