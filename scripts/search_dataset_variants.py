#!/usr/bin/python3

"""Run a two-stage search loop across a curated multi-song dataset."""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np


@dataclass(frozen=True)
class Song:
    name: str
    pool: str
    wav: Path


@dataclass(frozen=True)
class Candidate:
    name: str
    planner_args: tuple[str, ...]
    segment_mix: float = 0.0
    segment_scope: str = "all"
    planner_script: str = "make_transient_bitrate_plan.py"


def read_manifest(path: Path) -> list[Song]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    return [Song(name=item["name"], pool=item["pool"], wav=Path(item["wav"])) for item in data]


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


def combine_plans(
    transient_rows: list[dict[str, float]],
    segment_rows: list[dict[str, float]],
    mix: float,
    attack_mask: np.ndarray | None = None,
    segment_scope: str = "all",
) -> list[dict[str, float]]:
    if mix <= 0.0:
        return transient_rows

    count = min(len(transient_rows), len(segment_rows))
    if attack_mask is not None:
        count = min(count, len(attack_mask))
    transient_rows = transient_rows[:count]
    segment_rows = segment_rows[:count]
    if attack_mask is None:
        frame_mask = np.zeros(count, dtype=bool)
    else:
        frame_mask = attack_mask[:count]
    combined: list[dict[str, float]] = []
    for index, (transient_row, segment_row) in enumerate(zip(transient_rows, segment_rows)):
        apply_segment_mix = segment_scope == "all" or (segment_scope == "calm" and not bool(frame_mask[index]))
        ratio = ((1.0 - mix) + mix * segment_row["scale"]) if apply_segment_mix else 1.0
        combined.append(
            {
                "frame_index": transient_row["frame_index"],
                "target_bitrate_bps": transient_row["target_bitrate_bps"] * ratio,
                "scale": transient_row["scale"] * ratio,
            }
        )

    mean_scale = sum(row["scale"] for row in combined) / len(combined)
    for row in combined:
        row["scale"] /= mean_scale
        row["target_bitrate_bps"] /= mean_scale
    return combined


def run_command(args: list[str], cwd: Path) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def ensure_pcm_from_wav(wav_path: Path, pcm_path: Path) -> None:
    if pcm_path.exists():
        return
    with wave.open(str(wav_path), "rb") as handle:
        if handle.getnchannels() != 2 or handle.getframerate() != 48000 or handle.getsampwidth() != 2:
            raise ValueError(f"Unexpected WAV format for {wav_path}")
        pcm_path.write_bytes(handle.readframes(handle.getnframes()))


def load_pcm(path: Path, channels: int = 2) -> np.ndarray:
    data = np.fromfile(path, dtype=np.int16).astype(np.float64)
    return data.reshape(-1, channels) / 32768.0


def align_signals(reference: np.ndarray, test: np.ndarray, lag: int) -> tuple[np.ndarray, np.ndarray]:
    count = min(len(reference), len(test) - lag)
    return reference[:count], test[lag : lag + count]


def frame_mse(reference: np.ndarray, test: np.ndarray, hop: int = 960) -> np.ndarray:
    count = (min(len(reference), len(test)) // hop) * hop
    ref = reference[:count].reshape(-1, hop, reference.shape[1])
    tst = test[:count].reshape(-1, hop, test.shape[1])
    return np.mean((ref - tst) ** 2, axis=(1, 2))


def snr_db(reference: np.ndarray, test: np.ndarray) -> float:
    error = reference - test
    return float(10.0 * np.log10(np.mean(reference**2) / max(np.mean(error**2), 1e-20)))


def env_corr(reference: np.ndarray, test: np.ndarray, hop: int = 960) -> float:
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


def perceptual_frequency_weights(freqs_hz: np.ndarray) -> np.ndarray:
    # A-weighting style emphasis plus a presence bump where engineers are especially sensitive.
    freqs_sq = np.maximum(freqs_hz, 1.0) ** 2
    ra_num = (12194.0**2) * (freqs_sq**2)
    ra_den = (freqs_sq + 20.6**2) * np.sqrt((freqs_sq + 107.7**2) * (freqs_sq + 737.9**2)) * (freqs_sq + 12194.0**2)
    a_db = 20.0 * np.log10(np.maximum(ra_num / np.maximum(ra_den, 1e-20), 1e-20)) + 2.0
    a_linear = np.power(10.0, a_db / 20.0)
    presence_bump = 1.0 + 0.45 * np.exp(-0.5 * ((freqs_hz - 3200.0) / 1400.0) ** 2)
    weights = a_linear * presence_bump
    return weights / np.max(weights)


def perceptual_spectral_error(reference: np.ndarray, test: np.ndarray, fft_size: int = 2048, hop: int = 1024) -> float:
    window = np.hanning(fft_size)
    freqs = np.fft.rfftfreq(fft_size, d=1.0 / 48000.0)
    base_weights = perceptual_frequency_weights(freqs)
    ref = reference.mean(axis=1)
    tst = test.mean(axis=1)
    values: list[float] = []

    for start in range(0, min(len(ref), len(tst)) - fft_size, hop):
        ref_spec = np.abs(np.fft.rfft(ref[start : start + fft_size] * window))
        tst_spec = np.abs(np.fft.rfft(tst[start : start + fft_size] * window))
        ref_log = np.log10(ref_spec**2 + 1e-12)
        tst_log = np.log10(tst_spec**2 + 1e-12)
        masking = 1.0 / np.sqrt(np.maximum(ref_spec, 1e-6))
        weights = base_weights * masking
        weights /= np.mean(weights)
        values.append(float(np.sqrt(np.mean(((ref_log - tst_log) * weights) ** 2))))

    return float(np.mean(values))


def stereo_image_error(reference: np.ndarray, test: np.ndarray, hop: int = 960) -> float:
    count = (min(len(reference), len(test)) // hop) * hop
    ref = reference[:count].reshape(-1, hop, reference.shape[1])
    tst = test[:count].reshape(-1, hop, test.shape[1])

    ref_mid = 0.5 * (ref[:, :, 0] + ref[:, :, 1])
    ref_side = 0.5 * (ref[:, :, 0] - ref[:, :, 1])
    tst_mid = 0.5 * (tst[:, :, 0] + tst[:, :, 1])
    tst_side = 0.5 * (tst[:, :, 0] - tst[:, :, 1])

    ref_ratio = np.mean(ref_side**2, axis=1) / np.maximum(np.mean(ref_mid**2, axis=1), 1e-12)
    tst_ratio = np.mean(tst_side**2, axis=1) / np.maximum(np.mean(tst_mid**2, axis=1), 1e-12)
    return float(np.mean(np.abs(np.log1p(ref_ratio) - np.log1p(tst_ratio))))


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


def compute_flux_mask(reference: np.ndarray, frame_samples: int = 960, quantile: float = 0.95) -> np.ndarray:
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


def rank_score(metrics: dict[str, float]) -> float:
    # FOCUS ON SPECTRAL PSYCHOACOUSTICS
    # Lower time-domain MSE, massive boost for Spectral Error, LSD and Stereo
    return (
        1.5 * metrics["attack_mean_delta"]
        + 1.0 * metrics["calm_mean_delta"]
        + 18.0 * metrics["perceptual_spectral_delta"]
        + 7.5 * metrics["lsd_delta"]
        + 3.0 * metrics["stereo_image_delta"]
        + 0.8 * metrics["flux_mse_delta"]
        - 0.5 * metrics["snr_delta"]
    )


def average_kbps(csv_path: Path, sample_rate: int = 48000, frame_samples: int = 960) -> float:
    with csv_path.open("r", newline="", encoding="utf-8") as handle:
        packet_bytes = [int(row["packet_bytes"]) for row in csv.DictReader(handle)]
    return sum(packet_bytes) * 8 * sample_rate / len(packet_bytes) / frame_samples / 1000.0


def prepare_baseline(song: Song, song_dir: Path, workspace: Path, python_exe: Path, opus_demo: Path, lag: int) -> dict[str, object]:
    pcm_dir = workspace / "Test-Songs" / "pcm"
    pcm_dir.mkdir(parents=True, exist_ok=True)
    pcm_input = pcm_dir / f"{song.name}.pcm"
    ensure_pcm_from_wav(song.wav, pcm_input)

    baseline_bit = song_dir / "baseline.bit"
    baseline_csv = song_dir / "baseline.csv"
    baseline_pcm = song_dir / "baseline-decoded.pcm"
    segment_plan = song_dir / "baseline.segment-plan.csv"

    if not baseline_csv.exists() or not baseline_pcm.exists():
        run_command(
            [
                str(opus_demo),
                "-e",
                "audio",
                "48000",
                "2",
                "96000",
                "-dump_csv",
                str(baseline_csv),
                str(pcm_input),
                str(baseline_bit),
            ],
            workspace,
        )
        run_command([str(opus_demo), "-d", "48000", "2", str(baseline_bit), str(baseline_pcm)], workspace)

    if not segment_plan.exists():
        run_command(
            [str(python_exe), str(workspace / "scripts" / "make_bitrate_plan.py"), str(baseline_csv), "--output", str(segment_plan)],
            workspace,
        )

    reference = load_pcm(pcm_input)
    baseline_decoded = load_pcm(baseline_pcm)
    reference_aligned, baseline_aligned = align_signals(reference, baseline_decoded, lag)
    baseline_frame_mse = frame_mse(reference_aligned, baseline_aligned)
    attack_mask = compute_flux_mask(reference)
    comparable_frames = min(len(baseline_frame_mse), len(attack_mask))

    return {
        "pcm_input": pcm_input,
        "baseline_csv": baseline_csv,
        "segment_plan": segment_plan,
        "reference": reference,
        "reference_aligned": reference_aligned,
        "baseline_aligned": baseline_aligned,
        "baseline_frame_mse": baseline_frame_mse[:comparable_frames],
        "attack_mask": attack_mask[:comparable_frames],
        "baseline_metrics": {
            "snr": snr_db(reference_aligned, baseline_aligned),
            "env_corr": env_corr(reference_aligned, baseline_aligned),
            "lsd": lsd_db(reference_aligned, baseline_aligned),
            "perceptual_spectral": perceptual_spectral_error(reference_aligned, baseline_aligned),
            "stereo_image_error": stereo_image_error(reference_aligned, baseline_aligned),
            "flux_mse": transient_flux_mse(reference_aligned, baseline_aligned),
        },
    }


def evaluate_candidate(song: Song, candidate: Candidate, song_dir: Path, baseline: dict[str, object], workspace: Path, python_exe: Path, opus_demo: Path, lag: int) -> dict[str, float | str]:
    candidate_dir = song_dir / candidate.name
    candidate_dir.mkdir(parents=True, exist_ok=True)
    
    metrics_cache = candidate_dir / "metrics.json"
    if metrics_cache.exists():
        import json
        with open(metrics_cache, "r", encoding="utf-8") as f:
            return json.load(f)

    transient_plan = candidate_dir / "transient.csv"
    final_plan = candidate_dir / "final.csv"
    bit_path = candidate_dir / "candidate.bit"
    csv_path = candidate_dir / "candidate.csv"
    pcm_path = candidate_dir / "candidate-decoded.pcm"

    run_command(
        (
            [
                str(python_exe),
                str(workspace / "scripts" / candidate.planner_script),
                str(baseline["pcm_input"]),
                str(baseline["baseline_csv"]),
                "--output",
                str(transient_plan),
                *candidate.planner_args,
            ]
            if candidate.planner_script in ("make_transient_bitrate_plan.py", "make_symmetric_transient_plan.py", "make_envelope_plan.py", "make_viterbi_flux_plan.py", "make_asym_envelope_plan.py", "make_savgol_envelope_plan.py", "make_wavelet_envelope_plan.py")
            else [
                str(python_exe),
                str(workspace / "scripts" / candidate.planner_script),
                str(baseline["baseline_csv"]),
                "--output",
                str(transient_plan),
                *candidate.planner_args,
            ]
        ),
        workspace,
    )
    transient_rows = read_plan(transient_plan)
    segment_rows = read_plan(baseline["segment_plan"])
    final_rows = combine_plans(
        transient_rows,
        segment_rows,
        candidate.segment_mix,
        baseline["attack_mask"],
        candidate.segment_scope,
    )
    write_plan(final_plan, final_rows)

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
            str(baseline["pcm_input"]),
            str(bit_path),
        ],
        workspace,
    )
    run_command([str(opus_demo), "-d", "48000", "2", str(bit_path), str(pcm_path)], workspace)

    decoded = load_pcm(pcm_path)
    reference_aligned, decoded_aligned = align_signals(baseline["reference"], decoded, lag)
    mse = frame_mse(reference_aligned, decoded_aligned)
    comparable_frames = min(len(mse), len(baseline["baseline_frame_mse"]))
    mse = mse[:comparable_frames]
    baseline_frame_mse = baseline["baseline_frame_mse"][:comparable_frames]
    attack_mask = baseline["attack_mask"][:comparable_frames]
    calm_mask = ~attack_mask
    baseline_metrics = baseline["baseline_metrics"]

    metrics: dict[str, float | str] = {
        "song": song.name,
        "pool": song.pool,
        "candidate": candidate.name,
        "avg_kbps": average_kbps(csv_path),
        "snr": snr_db(reference_aligned, decoded_aligned),
        "env_corr": env_corr(reference_aligned, decoded_aligned),
        "lsd": lsd_db(reference_aligned, decoded_aligned),
        "perceptual_spectral": perceptual_spectral_error(reference_aligned, decoded_aligned),
        "stereo_image_error": stereo_image_error(reference_aligned, decoded_aligned),
        "flux_mse": transient_flux_mse(reference_aligned, decoded_aligned),
        "overall_mean_delta": float(np.mean(mse - baseline_frame_mse)),
        "attack_mean_delta": float(np.mean(mse[attack_mask] - baseline_frame_mse[attack_mask])),
        "calm_mean_delta": float(np.mean(mse[calm_mask] - baseline_frame_mse[calm_mask])) if np.any(calm_mask) else 0.0,
        "frames_better_than_baseline": int(np.sum(mse < baseline_frame_mse)),
        "frames_worse_than_baseline": int(np.sum(baseline_frame_mse < mse)),
    }
    metrics["snr_delta"] = float(metrics["snr"] - baseline_metrics["snr"])
    metrics["lsd_delta"] = float(metrics["lsd"] - baseline_metrics["lsd"])
    metrics["perceptual_spectral_delta"] = float(metrics["perceptual_spectral"] - baseline_metrics["perceptual_spectral"])
    metrics["stereo_image_delta"] = float(metrics["stereo_image_error"] - baseline_metrics["stereo_image_error"])
    metrics["flux_mse_delta"] = float(metrics["flux_mse"] - baseline_metrics["flux_mse"])
    metrics["score"] = rank_score(metrics)  # type: ignore[arg-type]
    
    import json
    with open(candidate_dir / "metrics.json", "w", encoding="utf-8") as f:
        json.dump(metrics, f, indent=4)
        
    return metrics


def aggregate_stage(song_metrics: list[dict[str, float | str]]) -> dict[str, float | str]:
    candidate = song_metrics[0]["candidate"]
    numeric_keys = [
        "avg_kbps",
        "snr_delta",
        "lsd_delta",
        "perceptual_spectral_delta",
        "stereo_image_delta",
        "flux_mse_delta",
        "overall_mean_delta",
        "attack_mean_delta",
        "calm_mean_delta",
        "score",
        "env_corr",
    ]
    aggregate: dict[str, float | str] = {"candidate": candidate, "songs": len(song_metrics)}
    for key in numeric_keys:
        aggregate[key] = float(np.mean([float(metric[key]) for metric in song_metrics]))
    aggregate["frames_better_than_baseline"] = int(sum(int(metric["frames_better_than_baseline"]) for metric in song_metrics))
    aggregate["frames_worse_than_baseline"] = int(sum(int(metric["frames_worse_than_baseline"]) for metric in song_metrics))
    
    # Neues, toleranteres Gate: Der Durchschnitt muss die Baseline schlagen (< 0.0),
    # und kein einzelner Song darf schlechter als +0.02 performen.
    avg_score = float(aggregate["score"])
    max_song_score = max(float(metric["score"]) for metric in song_metrics)
    aggregate["passed_gate_all_songs"] = (avg_score < 0.0) and (max_song_score < 0.02)
    
    return aggregate


def main() -> None:
    parser = argparse.ArgumentParser(description="Run a two-stage bitrate-variant search over the curated test-song set.")
    parser.add_argument("--workspace", default=".")
    parser.add_argument("--manifest", default="Test-Songs/manifest.json")
    parser.add_argument("--output-dir", default="Output/dataset-search")
    parser.add_argument("--lag", type=int, default=312)
    parser.add_argument("--gate-songs", nargs="*", default=["04_geilo_dua_lipa_dance_the_night", "09_tokyo_creepy_nuts_otonoke"])
    parser.add_argument("--full-finalists", type=int, default=3)
    args = parser.parse_args()

    workspace = Path(args.workspace).resolve()
    manifest_path = (workspace / args.manifest).resolve()
    output_dir = (workspace / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    python_exe = Path(r"c:/Users/eller/Desktop/ASTRAL/Pulsar/.venv/Scripts/python.exe")
    opus_demo = workspace / "opus_demo.exe"
    songs = read_manifest(manifest_path)
    song_map = {song.name: song for song in songs}
    gate_songs = [song_map[name] for name in args.gate_songs if name in song_map]
    full_songs = songs

    candidates = [
        Candidate('gemini_original', ('--max-filter-size', '4', '--sigma', '1.2', '--strong-peak', '1.45', '--medium-peak', '1.25', '--base-scale', '0.86', '--max-scale', '1.40', '--min-scale', '0.85'), planner_script='make_envelope_plan.py'),
        Candidate('gemini_audiophile', ('--max-filter-size', '4', '--sigma', '1.2', '--strong-peak', '1.40', '--medium-peak', '1.20', '--base-scale', '0.89', '--max-scale', '1.35', '--min-scale', '0.88'), planner_script='make_envelope_plan.py'),
        Candidate('gemini_smooth_detail', ('--max-filter-size', '6', '--sigma', '1.6', '--strong-peak', '1.40', '--medium-peak', '1.20', '--base-scale', '0.88', '--max-scale', '1.35', '--min-scale', '0.87'), planner_script='make_envelope_plan.py'),
        Candidate('gemini_air_extreme', ('--max-filter-size', '4', '--sigma', '1.2', '--strong-peak', '1.30', '--medium-peak', '1.15', '--base-scale', '0.93', '--max-scale', '1.25', '--min-scale', '0.92'), planner_script='make_envelope_plan.py')
    ]

    baselines: dict[str, dict[str, object]] = {}

    def run_stage(stage_name: str, stage_songs: list[Song], stage_candidates: list[Candidate]) -> tuple[list[dict[str, float | str]], list[dict[str, float | str]]]:
        stage_dir = output_dir / stage_name
        stage_dir.mkdir(parents=True, exist_ok=True)
        song_results: list[dict[str, float | str]] = []

        for song in stage_songs:
            song_dir = stage_dir / song.name
            song_dir.mkdir(parents=True, exist_ok=True)
            baseline = baselines.get(song.name)
            if baseline is None:
                baseline = prepare_baseline(song, song_dir, workspace, python_exe, opus_demo, args.lag)
                baselines[song.name] = baseline
            for candidate in stage_candidates:
                song_results.append(evaluate_candidate(song, candidate, song_dir, baseline, workspace, python_exe, opus_demo, args.lag))

        grouped: dict[str, list[dict[str, float | str]]] = {}
        for result in song_results:
            grouped.setdefault(str(result["candidate"]), []).append(result)

        stage_summary = [aggregate_stage(grouped[name]) for name in grouped]
        stage_summary.sort(key=lambda item: float(item["score"]))
        (stage_dir / "song-results.json").write_text(json.dumps(song_results, indent=2), encoding="utf-8")
        (stage_dir / "summary.json").write_text(json.dumps(stage_summary, indent=2), encoding="utf-8")
        return song_results, stage_summary

    gate_song_results, gate_summary = run_stage("gate", gate_songs, candidates)
    promoted_names = [str(item["candidate"]) for item in gate_summary if bool(item["passed_gate_all_songs"])]
    for item in gate_summary:
        candidate_name = str(item["candidate"])
        if candidate_name not in promoted_names:
            promoted_names.append(candidate_name)
        if len(promoted_names) >= args.full_finalists:
            break
    promoted_names = promoted_names[: args.full_finalists]
    promoted = [candidate for candidate in candidates if candidate.name in promoted_names]

    _, full_summary = run_stage("full", full_songs, promoted)

    report = {
        "gate_songs": [song.name for song in gate_songs],
        "all_songs": [song.name for song in full_songs],
        "promoted_candidates": promoted_names,
        "gate_summary": gate_summary,
        "full_summary": full_summary,
    }
    (output_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    print(f"Gate songs: {[song.name for song in gate_songs]}")
    print("Gate winners:")
    for item in gate_summary[: min(3, len(gate_summary))]:
        print(
            f"{item['candidate']}: score={float(item['score']):.6f} attack_mean_delta={float(item['attack_mean_delta']):.9f} "
            f"overall_mean_delta={float(item['overall_mean_delta']):.9f} passed_all={item['passed_gate_all_songs']}"
        )
    print(f"Promoted: {promoted_names}")
    print("Full winners:")
    for item in full_summary[: min(3, len(full_summary))]:
        print(
            f"{item['candidate']}: score={float(item['score']):.6f} attack_mean_delta={float(item['attack_mean_delta']):.9f} "
            f"overall_mean_delta={float(item['overall_mean_delta']):.9f} better={item['frames_better_than_baseline']} worse={item['frames_worse_than_baseline']}"
        )
    print(f"Wrote staged report to {output_dir / 'report.json'}")


if __name__ == "__main__":
    main()