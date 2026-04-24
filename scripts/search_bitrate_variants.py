#!/usr/bin/python3

"""Run an automated search loop for offline bitrate-planning variants."""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import subprocess
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np


@dataclass(frozen=True)
class Candidate:
    name: str
    transient_args: tuple[str, ...]
    segment_mix: float = 0.0
    script_name: str = "make_transient_bitrate_plan.py"


def read_plan(path: Path) -> list[dict[str, float]]:
    rows: list[dict[str, float]] = []
    with path.open("r", newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            rows.append(
                {
                    "frame_index": int(row["frame_index"]),
                    "target_bitrate_bps": float(row["target_bitrate_bps"]),
                    "scale": float(row["scale"]),
                }
            )
    return rows


def write_plan(path: Path, rows: list[dict[str, float]]) -> None:
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["frame_index", "target_bitrate_bps", "scale"])
        for row in rows:
            writer.writerow([row["frame_index"], int(round(row["target_bitrate_bps"])), f"{row['scale']:.6f}"])


def combine_plans(transient_rows: list[dict[str, float]], segment_rows: list[dict[str, float]], mix: float) -> list[dict[str, float]]:
    if mix <= 0.0:
        return transient_rows

    count = min(len(transient_rows), len(segment_rows))
    transient_rows = transient_rows[:count]
    segment_rows = segment_rows[:count]

    combined: list[dict[str, float]] = []
    for transient_row, segment_row in zip(transient_rows, segment_rows):
        scale = transient_row["scale"] * ((1.0 - mix) + mix * segment_row["scale"])
        combined.append(
            {
                "frame_index": transient_row["frame_index"],
                "target_bitrate_bps": transient_row["target_bitrate_bps"] * ((1.0 - mix) + mix * segment_row["scale"]),
                "scale": scale,
            }
        )

    mean_scale = sum(row["scale"] for row in combined) / len(combined)
    for row in combined:
        row["scale"] /= mean_scale
        row["target_bitrate_bps"] /= mean_scale
    return combined


def run_command(args: list[str], cwd: Path) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def load_pcm(path: Path, channels: int = 2) -> np.ndarray:
    pcm = np.fromfile(path, dtype=np.int16).astype(np.float64)
    return pcm.reshape(-1, channels) / 32768.0


def align_signals(reference: np.ndarray, test: np.ndarray, lag: int) -> tuple[np.ndarray, np.ndarray]:
    count = min(len(reference), len(test) - lag)
    return reference[:count], test[lag : lag + count]


def snr_db(reference: np.ndarray, test: np.ndarray) -> float:
    error = reference - test
    return float(10.0 * np.log10(np.mean(reference**2) / max(np.mean(error**2), 1e-20)))


def env_corr(reference: np.ndarray, test: np.ndarray, hop: int) -> float:
    ref = reference.mean(axis=1)
    tst = test.mean(axis=1)
    count = (min(len(ref), len(tst)) // hop) * hop
    ref_env = np.sqrt(np.mean(ref[:count].reshape(-1, hop) ** 2, axis=1))
    tst_env = np.sqrt(np.mean(tst[:count].reshape(-1, hop) ** 2, axis=1))
    return float(np.corrcoef(ref_env, tst_env)[0, 1])


def lsd_db(reference: np.ndarray, test: np.ndarray, fft_size: int = 2048, hop: int = 1024) -> float:
    window = np.hanning(fft_size)
    ref = reference.mean(axis=1)
    tst = test.mean(axis=1)
    values: list[float] = []
    for start in range(0, min(len(ref), len(tst)) - fft_size, hop):
        ref_spec = np.fft.rfft(ref[start : start + fft_size] * window)
        tst_spec = np.fft.rfft(tst[start : start + fft_size] * window)
        ref_log = 10.0 * np.log10(np.abs(ref_spec) ** 2 + 1e-12)
        tst_log = 10.0 * np.log10(np.abs(tst_spec) ** 2 + 1e-12)
        values.append(float(np.sqrt(np.mean((ref_log - tst_log) ** 2))))
    return float(np.mean(values))


def transient_flux_mse(reference: np.ndarray, test: np.ndarray, fft_size: int = 2048, hop: int = 960) -> float:
    window = np.hanning(fft_size)
    ref = reference.mean(axis=1)
    tst = test.mean(axis=1)
    previous_ref = None
    previous_tst = None
    values: list[float] = []
    for start in range(0, min(len(ref), len(tst)) - fft_size, hop):
        ref_spec = np.abs(np.fft.rfft(ref[start : start + fft_size] * window))
        tst_spec = np.abs(np.fft.rfft(tst[start : start + fft_size] * window))
        if previous_ref is not None:
            ref_flux = np.maximum(ref_spec - previous_ref, 0.0)
            tst_flux = np.maximum(tst_spec - previous_tst, 0.0)
            values.append(float(np.mean((ref_flux - tst_flux) ** 2)))
        previous_ref = ref_spec
        previous_tst = tst_spec
    return float(np.mean(values))


def frame_mse(reference: np.ndarray, test: np.ndarray, hop: int = 960) -> np.ndarray:
    count = (min(len(reference), len(test)) // hop) * hop
    ref = reference[:count].reshape(-1, hop, reference.shape[1])
    tst = test[:count].reshape(-1, hop, test.shape[1])
    return np.mean((ref - tst) ** 2, axis=(1, 2))


def compute_flux_mask(reference: np.ndarray, frame_samples: int, quantile: float = 0.95) -> np.ndarray:
    mono = reference.mean(axis=1)
    window = np.hanning(2048)
    spectra: list[np.ndarray] = []
    for start in range(0, len(mono) - 2048, frame_samples):
        spectra.append(np.abs(np.fft.rfft(mono[start : start + 2048] * window)))
    flux = [0.0]
    for previous, current in zip(spectra, spectra[1:]):
        flux.append(float(np.sum(np.maximum(current - previous, 0.0))))
    threshold = float(np.quantile(flux, quantile))
    return np.array([value >= threshold for value in flux], dtype=bool)


def write_wav_from_pcm(pcm_path: Path, wav_path: Path, sample_rate: int, channels: int) -> None:
    raw = pcm_path.read_bytes()
    with wave.open(str(wav_path), "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(raw)


def rank_score(metrics: dict[str, float]) -> float:
    return (
        6.0 * metrics["attack_mean_delta"]
        + 3.0 * metrics["overall_mean_delta"]
        + 0.6 * metrics["lsd_delta"]
        + 0.8 * metrics["flux_mse_delta"]
        - 0.25 * metrics["snr_delta"]
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Search offline bitrate-planning variants against a fixed baseline.")
    parser.add_argument("--workspace", default=".")
    parser.add_argument("--output-dir", default="Output/search-loop")
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--lag", type=int, default=312)
    args = parser.parse_args()

    workspace = Path(args.workspace).resolve()
    output_dir = (workspace / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    python_exe = Path(r"c:/Users/eller/Desktop/ASTRAL/Pulsar/.venv/Scripts/python.exe")
    opus_demo = workspace / "opus_demo.exe"
    pcm_input = workspace / "Output" / "adrenalina-48k-stereo-s16.pcm"
    baseline_csv = workspace / "Output" / "baseline-96-fresh.csv"
    baseline_pcm = workspace / "Output" / "baseline-96-fresh-decoded.pcm"
    segment_plan = workspace / "Output" / "baseline-96.plan.csv"

    segment_rows = read_plan(segment_plan)
    baseline_reference = load_pcm(pcm_input)
    baseline_decoded = load_pcm(baseline_pcm)
    reference_aligned, baseline_aligned = align_signals(baseline_reference, baseline_decoded, args.lag)
    baseline_frame_mse = frame_mse(reference_aligned, baseline_aligned)
    attack_mask = compute_flux_mask(baseline_reference, 960)
    comparable_frames = min(len(baseline_frame_mse), len(attack_mask))
    baseline_frame_mse = baseline_frame_mse[:comparable_frames]
    attack_mask = attack_mask[:comparable_frames]

    baseline_metrics = {
        "snr": snr_db(reference_aligned, baseline_aligned),
        "env_corr": env_corr(reference_aligned, baseline_aligned, 960),
        "lsd": lsd_db(reference_aligned, baseline_aligned),
        "flux_mse": transient_flux_mse(reference_aligned, baseline_aligned),
    }

    candidates = [
        Candidate("gemini_envelope_balanced", ("--max-filter-size", "3", "--sigma", "1.5", "--strong-peak", "1.35", "--medium-peak", "1.20", "--base-scale", "0.88", "--max-scale", "1.35", "--min-scale", "0.87"), 0.0, "make_envelope_plan.py"),
        Candidate("gemini_envelope_wide", ("--max-filter-size", "5", "--sigma", "2.0", "--strong-peak", "1.40", "--medium-peak", "1.25", "--base-scale", "0.88", "--max-scale", "1.35", "--min-scale", "0.86"), 0.0, "make_envelope_plan.py"),
        Candidate("gemini_envelope_attackmax", ("--max-filter-size", "4", "--sigma", "1.2", "--strong-peak", "1.45", "--medium-peak", "1.25", "--base-scale", "0.86", "--max-scale", "1.40", "--min-scale", "0.85"), 0.0, "make_envelope_plan.py"),
        Candidate("gemini_zero_phase", ("--sigma", "3.0", "--strong-peak", "1.9", "--medium-peak", "1.4", "--base-scale", "0.88", "--max-scale", "1.28", "--min-scale", "0.85"), 0.0, "make_symmetric_transient_plan.py"),
        Candidate("gemini_zero_phase_wide", ("--sigma", "4.5", "--strong-peak", "2.1", "--medium-peak", "1.5", "--base-scale", "0.87", "--max-scale", "1.26", "--min-scale", "0.84"), 0.0, "make_symmetric_transient_plan.py"),
        Candidate("gemini_zero_phase_extreme", ("--sigma", "2.0", "--strong-peak", "2.0", "--medium-peak", "1.4", "--base-scale", "0.86", "--max-scale", "1.32", "--min-scale", "0.86"), 0.0, "make_symmetric_transient_plan.py"),
        Candidate("viterbi_flux_hybrid", tuple(), 0.0, "make_viterbi_flux_plan.py"),
        Candidate("transient_mild", tuple()),
        Candidate("transient_strong", ("--strong-boost", "1.24", "--medium-boost", "1.12", "--recovery-scale", "0.90", "--pre-roll", "3", "--post-roll", "1", "--max-scale", "1.24", "--min-scale", "0.84")),
        Candidate("transient_wide", ("--strong-boost", "1.20", "--medium-boost", "1.10", "--recovery-scale", "0.91", "--pre-roll", "4", "--post-roll", "2", "--max-scale", "1.22", "--min-scale", "0.86")),
        Candidate("transient_wide_balanced", ("--strong-boost", "1.22", "--medium-boost", "1.11", "--recovery-scale", "0.91", "--pre-roll", "3", "--post-roll", "2", "--max-scale", "1.22", "--min-scale", "0.85")),
        Candidate("transient_wide_attackmax", ("--strong-boost", "1.26", "--medium-boost", "1.13", "--recovery-scale", "0.91", "--pre-roll", "4", "--post-roll", "2", "--max-scale", "1.26", "--min-scale", "0.84")),
        Candidate("transient_strong_balanced", ("--strong-boost", "1.23", "--medium-boost", "1.11", "--recovery-scale", "0.91", "--pre-roll", "2", "--post-roll", "1", "--max-scale", "1.22", "--min-scale", "0.85")),
        Candidate("transient_strong_seg25", ("--strong-boost", "1.24", "--medium-boost", "1.12", "--recovery-scale", "0.90", "--pre-roll", "3", "--post-roll", "1", "--max-scale", "1.24", "--min-scale", "0.84"), 0.25),
        Candidate("transient_strong_seg40", ("--strong-boost", "1.24", "--medium-boost", "1.12", "--recovery-scale", "0.90", "--pre-roll", "3", "--post-roll", "1", "--max-scale", "1.24", "--min-scale", "0.84"), 0.40),
        Candidate("transient_fast", ("--strong-boost", "1.22", "--medium-boost", "1.10", "--recovery-scale", "0.91", "--pre-roll", "1", "--post-roll", "1", "--ema-alpha", "0.50", "--max-step", "0.020", "--max-scale", "1.22", "--min-scale", "0.86")),
    ]

    results: list[dict[str, float | str]] = []
    for candidate in candidates:
        transient_plan = output_dir / f"{candidate.name}.plan.csv"
        run_command(
            [
                str(python_exe),
                str(workspace / "scripts" / candidate.script_name),
                str(pcm_input),
                str(baseline_csv),
                "--output",
                str(transient_plan),
                *candidate.transient_args,
            ],
            workspace,
        )

        transient_rows = read_plan(transient_plan)
        final_rows = combine_plans(transient_rows, segment_rows, candidate.segment_mix)
        final_plan = output_dir / f"{candidate.name}.final.csv"
        write_plan(final_plan, final_rows)

        bit_path = output_dir / f"{candidate.name}.bit"
        csv_path = output_dir / f"{candidate.name}.csv"
        pcm_path = output_dir / f"{candidate.name}.pcm"
        wav_path = output_dir / f"{candidate.name}.wav"

        run_command(
            [
                str(opus_demo),
                "-e",
                "audio",
                "48000",
                "2",
                "96000",
                "-bitrate_plan",
                str(final_plan),
                "-dump_csv",
                str(csv_path),
                str(pcm_input),
                str(bit_path),
            ],
            workspace,
        )
        run_command([str(opus_demo), "-d", "48000", "2", str(bit_path), str(pcm_path)], workspace)
        write_wav_from_pcm(pcm_path, wav_path, args.sample_rate, 2)

        decoded = load_pcm(pcm_path)
        reference_aligned, decoded_aligned = align_signals(baseline_reference, decoded, args.lag)
        mse = frame_mse(reference_aligned, decoded_aligned)

        mse = mse[:comparable_frames]

        metrics = {
            "name": candidate.name,
            "avg_kbps": 0.0,
            "snr": snr_db(reference_aligned, decoded_aligned),
            "env_corr": env_corr(reference_aligned, decoded_aligned, 960),
            "lsd": lsd_db(reference_aligned, decoded_aligned),
            "flux_mse": transient_flux_mse(reference_aligned, decoded_aligned),
            "overall_mean_delta": float(np.mean(mse - baseline_frame_mse)),
            "attack_mean_delta": float(np.mean(mse[attack_mask] - baseline_frame_mse[attack_mask])),
            "frames_better_than_baseline": int(np.sum(mse < baseline_frame_mse)),
            "frames_worse_than_baseline": int(np.sum(baseline_frame_mse < mse)),
        }
        metrics["snr_delta"] = metrics["snr"] - baseline_metrics["snr"]
        metrics["lsd_delta"] = metrics["lsd"] - baseline_metrics["lsd"]
        metrics["flux_mse_delta"] = metrics["flux_mse"] - baseline_metrics["flux_mse"]

        with csv_path.open("r", newline="", encoding="utf-8") as handle:
            packet_bytes = [int(row["packet_bytes"]) for row in csv.DictReader(handle)]
        metrics["avg_kbps"] = sum(packet_bytes) * 8 * args.sample_rate / len(packet_bytes) / 960.0 / 1000.0
        metrics["score"] = rank_score(metrics)
        results.append(metrics)

    results.sort(key=lambda item: item["score"])

    summary_path = output_dir / "results.json"
    summary_path.write_text(json.dumps(results, indent=2), encoding="utf-8")

    print(f"Baseline SNR: {baseline_metrics['snr']:.6f}")
    print(f"Baseline LSD: {baseline_metrics['lsd']:.6f}")
    print(f"Baseline flux MSE: {baseline_metrics['flux_mse']:.6f}")
    print("Top candidates:")
    for result in results[:3]:
        print(
            f"{result['name']}: score={result['score']:.6f} avg_kbps={result['avg_kbps']:.3f} "
            f"snr_delta={result['snr_delta']:.6f} lsd_delta={result['lsd_delta']:.6f} "
            f"flux_mse_delta={result['flux_mse_delta']:.6f} attack_mean_delta={result['attack_mean_delta']:.9f}"
        )
    print(f"Wrote summary to {summary_path}")


if __name__ == "__main__":
    main()