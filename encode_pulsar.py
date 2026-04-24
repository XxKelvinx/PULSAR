# Copyright (c) 2026 Kelvin Eller
# Licensed under the BSD 3-Clause License (see COPYING)
import subprocess
from pathlib import Path
import argparse
import sys
import os

def check_dependencies():
    # Make sure ffmpeg is in path
    try:
        subprocess.run(["ffmpeg", "-version"], capture_output=True, check=True)
    except Exception:
        print("Error: FFmpeg not found in PATH. Please install FFmpeg.")
        sys.exit(1)

    opus_demo = Path("opus_demo.exe") if os.name == "nt" else Path("./opus_demo")
    if not opus_demo.exists():
        print(f"Error: {opus_demo} not found in the current directory.")
        sys.exit(1)
        
    return opus_demo

def main():
    parser = argparse.ArgumentParser(description="Encode a WAV file using the Pulsar Psychoacoustic VBR Planner.")
    parser.add_argument("-i", "--input", type=str, required=True, help="Input WAV file to encode.")
    parser.add_argument("-b", "--bitrate", type=int, default=128, help="Target bitrate in kbps (default: 128).")
    parser.add_argument("-o", "--output-dir", type=str, default="Output/Pulsar_Run", help="Directory for output files.")
    parser.add_argument("--skip-blindtest", action="store_true", help="Skip generating the 30s + 30s blind test file.")
    
    args = parser.parse_args()

    input_wav = Path(args.input)
    if not input_wav.exists():
        print(f"Error: Input file '{input_wav}' does not exist.")
        sys.exit(1)

    bitrate_bps = args.bitrate * 1000
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    
    opus_demo = check_dependencies()
    
    # We use sys.executable to ensure we call the python environment running this script
    python_exe = sys.executable

    print(f"\nStarting Pulsar Encoding Pipeline for '{input_wav.name}' at {args.bitrate} kbps!")

    pcm_input = output_dir / "input.pcm"

    print(f"\n[1/4] Converting WAV to PCM (with 48kHz resampling)...")
    subprocess.run([
        "ffmpeg", "-y", "-i", str(input_wav), 
        "-f", "s16le", "-ac", "2", "-ar", "48000", str(pcm_input)
    ], capture_output=True, check=True)

    print(f"\n[2/4] Generating Opus Baseline at {args.bitrate} kbps for Telemetry Analysis...")
    baseline_csv = output_dir / f"baseline_{args.bitrate}.csv"
    baseline_bit = output_dir / f"baseline_{args.bitrate}.bit"
    baseline_pcm = output_dir / f"baseline_{args.bitrate}.pcm"
    baseline_wav = output_dir / f"baseline_{args.bitrate}.wav"

    subprocess.run([str(opus_demo), "-e", "audio", "48000", "2", str(bitrate_bps), "-dump_csv", str(baseline_csv), str(pcm_input), str(baseline_bit)], check=True)
    subprocess.run([str(opus_demo), "-d", "48000", "2", str(baseline_bit), str(baseline_pcm)], check=True)
    subprocess.run(["ffmpeg", "-y", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", str(baseline_pcm), str(baseline_wav)], capture_output=True, check=True)

    print("\n[3/4] Generating Pulsar Psychoacoustic Plan...")
    god_plan = output_dir / "plan_Pulsar.csv"
    god_bit = output_dir / "Pulsar.bit"
    god_pcm = output_dir / "Pulsar.pcm"
    god_wav = output_dir / "Pulsar.wav"

    god_args = [
        "--strong-peak", "1.45",
        "--medium-peak", "1.25",
        "--base-scale", "0.88",
        "--min-scale", "0.80",
        "--max-scale", "1.50",
        "--highpass-hz", "150.0",
        "--pre-echo-decay", "0.85",
        "--post-mask-decay", "0.30"
    ]

    subprocess.run([str(python_exe), "scripts/pulsar_planner.py", str(pcm_input), str(baseline_csv), "--output", str(god_plan)] + god_args, check=True)

    print(f"\n[4/4] Encoding Final Pulsar Audio at {args.bitrate} kbps...")
    subprocess.run([str(opus_demo), "-e", "audio", "48000", "2", str(bitrate_bps), "-bitrate_plan", str(god_plan), str(pcm_input), str(god_bit)], check=True)
    subprocess.run([str(opus_demo), "-d", "48000", "2", str(god_bit), str(god_pcm)], check=True)
    subprocess.run(["ffmpeg", "-y", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", str(god_pcm), str(god_wav)], capture_output=True, check=True)

    comparison_wav = None
    if not args.skip_blindtest:
        print("\n[Extras] Creating Blind Test Comparison (30s Baseline -> 3s Silence -> 30s Pulsar)...")
        short_baseline = output_dir / "short_baseline.wav"
        short_god = output_dir / "short_pulsar.wav"
        silence_wav = output_dir / "silence.wav"

        subprocess.run(["ffmpeg", "-y", "-i", str(baseline_wav), "-t", "30", str(short_baseline)], capture_output=True, check=True)
        subprocess.run(["ffmpeg", "-y", "-i", str(god_wav), "-t", "30", str(short_god)], capture_output=True, check=True)
        subprocess.run(["ffmpeg", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=3", str(silence_wav)], capture_output=True, check=True)

        concat_list = output_dir / "concat_list.txt"
        with open(concat_list, "w") as f:
            f.write(f"file '{short_baseline.name}'\n")
            f.write(f"file '{silence_wav.name}'\n")
            f.write(f"file '{short_god.name}'\n")

        comparison_wav = output_dir / "Pulsar_Blindtest.wav"
        # We need to run ffmpeg inside the output_dir to properly handle concat text files paths
        subprocess.run(["ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", str(concat_list.name), "-c", "copy", str(comparison_wav.name)], capture_output=True, check=True, cwd=str(output_dir))

    print(f"\n✨ DONE! The Pulsar Track is ready in: {output_dir}")
    print(f"👉 Baseline   (Stock Opus): {baseline_wav}")
    print(f"👉 Pulsar     (Psycho VBR): {god_wav}")
    if comparison_wav:
        print(f"👉 Blind Test (Base->Pulse): {comparison_wav}")
    
if __name__ == "__main__":
    main()
