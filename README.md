# Pulsar Encoder (Opus Psychoacoustic VBR)

**Pulsar Encoder** is an experimental open-source offline VBR (Variable Bitrate) planner built on top of the [Opus Audio Codec](https://opus-codec.org/).

Unlike standard Opus VBR—which often uses a generalized symmetric approach for bit allocation—**Pulsar** uses a specialized **Asymmetric Psychoacoustic Envelope** designed specifically to protect transients (like drum hits) and preserve low-end punch, while smartly saving bits during post-transient masking phases.

## Why Pulsar?

Standard VBR algorithms sometimes starve sub-bass or smear transients because their temporal smoothing is symmetric (they look forward and backward equally).
Pulsar introduces psychoacoustic offline planning:
- **150Hz High-Pass Filtering:** The offline transient detector ignores heavy sub-bass so it doesn't falsely trigger aggressive bit-stealing from low-frequency content.
- **Asymmetric Smoothing:** We allocate bits aggressively before the transient (pre-echo protection) but drastically cut allocations after the transient (post-transient masking, as the human ear is temporarily deafened by loud hits).
- **Offline Multipass:** By analyzing telemetry data via `opus_demo` first, Pulsar computes the perfect mathematical bit-allocation map for the entire song before the final encode.

## Usage

### Prerequisites
1. **Python 3.10+**
2. **FFmpeg** (must be in your system PATH)
3. Compiled `opus_demo` binary (with the offline planner patches included in this repo)

### Installation
Clone the repository and install the Python requirements:
```bash
git clone https://github.com/yourusername/pulsar-encoder.git
cd pulsar-encoder
pip install -r requirements.txt
```

### Quick Start Encode
Run the wrapper script on any WAV file:
```bash
python encode_pulsar.py -i "my_song.wav" -b 128
```

You can customize the bitrate and output directory:
```bash
python encode_pulsar.py --input "C:\Music\Track.wav" --bitrate 256 --output-dir "Output/My_Track"
```

Use `python encode_pulsar.py -h` for all available options.
This script handles the full 3-step workflow automatically:
1. Converting your WAV to PCM.
2. Generating a flat 128kbps Opus baseline (for telemetry analysis).
3. Passing the telemetry to `pulsar_planner.py` to generate the VBR bit-map.
4. Encoding the final `Pulsar.wav` output using the custom map.

## Contributing (Help Wanted!)

We want to make Pulsar the best offline audio encoder available. We heavily welcome contributions!
Here is the current roadmap and where you can help:

*   **Port Python logic to C:** Currently, the psychoacoustic planner (`pulsar_planner.py`) requires an external Python script. The ultimate goal is to integrate the 150Hz High-Pass and Asymmetric Envelope directly into the native Opus C codebase.
*   **Performance Tuning:** Optimizing the SciPy/NumPy array manipulations in the planner.
*   **Dataset Evaluation:** Test Pulsar against Baseline Opus on different genres (Classical, Electronic, Speech) and help us tune the hyperparameters (`--pre-echo-decay`, `--post-mask-decay`, `--highpass-hz`).
*   **GUI / CLI Wrapper:** Building a better User Interface or CLI tool rather than editing the `encode_pulsar.py` file manually.

Check out `CONTRIBUTING.md` for more details!

## License
This project is built upon the Opus Codec and inherits its [BSD 3-Clause License](LICENSE_PLEASE_READ.txt). The Pulsar Python tools and wrappers are provided under the MIT License.
