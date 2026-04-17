"""Fast audio comparison helper for PULSAR.

This script compares reference WAV tracks against one or more target
directories and reports file size, bitrate, SNR, PSNR, residual RMS, and
peak residual values. It uses ffprobe/ffmpeg to decode lossy formats directly
to mono float PCM, so WAV, OGG/Opus, M4A/AAC, MP3, FLAC, and similar files can
be compared in one pass.

Typical usage:

    python Tools/compare_audio_metrics.py
    python Tools/compare_audio_metrics.py --track "Spira 32s"
    python Tools/compare_audio_metrics.py --target-root "Artifacts/Output"
    python Tools/compare_audio_metrics.py --csv compare.csv
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import re
import subprocess
import sys
from array import array
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


SUPPORTED_AUDIO_EXTENSIONS = {
    ".aac",
    ".aif",
    ".aiff",
    ".flac",
    ".m4a",
    ".mp3",
    ".ogg",
    ".opus",
    ".wav",
    ".wma",
    ".wv",
}

ARCHIVE_EXTENSIONS = {".plsr", ".pulsr"}
TARGET_EXTENSIONS = SUPPORTED_AUDIO_EXTENSIONS | ARCHIVE_EXTENSIONS


@dataclass(frozen=True)
class AudioProbe:
    path: Path
    codec_name: str
    sample_rate: int | None
    channels: int | None
    duration_seconds: float | None


@dataclass(frozen=True)
class ReferenceContext:
    path: Path
    probe: AudioProbe
    samples: array


@dataclass(frozen=True)
class TargetArtifact:
    artifact_path: Path
    decode_path: Path
    codec_name: str
    label: str
    size_bytes: int
    is_archive: bool


@dataclass(frozen=True)
class ComparisonRow:
    reference_path: Path
    artifact_path: Path
    decode_path: Path
    target_label: str
    target_codec: str
    target_size_bytes: int
    target_duration_seconds: float
    duration_delta_seconds: float
    target_bitrate_kbps: float
    sample_count: int
    rms_reference: float
    rms_residual: float
    peak_residual: float
    snr_db: float | None
    psnr_db: float | None


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def normalize_name(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", text.lower())


def default_roots() -> tuple[list[Path], list[Path]]:
    root = repo_root()
    reference_candidates = [
        root / "Artifacts" / "Test Tracks" / "RAW Tracks",
        root / "TestWAVs",
    ]
    target_candidates = [
        root / "Artifacts" / "Test Tracks" / "Lossy Tracks",
        root / "Artifacts" / "Output",
    ]
    reference_roots = [candidate for candidate in reference_candidates if candidate.exists()]
    target_roots = [candidate for candidate in target_candidates if candidate.exists()]
    return reference_roots, target_roots


def collect_audio_files(paths: Iterable[Path], allowed_extensions: set[str] = SUPPORTED_AUDIO_EXTENSIONS) -> list[Path]:
    files: list[Path] = []
    for path in paths:
        if path.is_file() and path.suffix.lower() in allowed_extensions:
            files.append(path)
            continue
        if path.is_dir():
            for candidate in path.rglob("*"):
                if candidate.is_file() and candidate.suffix.lower() in allowed_extensions:
                    files.append(candidate)
    return sorted(files)


def probe_audio(path: Path, ffprobe_exe: str) -> AudioProbe:
    command = [
        ffprobe_exe,
        "-v",
        "error",
        "-select_streams",
        "a:0",
        "-show_entries",
        "stream=codec_name,sample_rate,channels,duration",
        "-of",
        "json",
        str(path),
    ]
    completed = subprocess.run(command, capture_output=True, text=True)
    if completed.returncode != 0:
        raise RuntimeError(
            f"ffprobe failed for {path}: {completed.stderr.strip() or completed.stdout.strip()}"
        )

    payload = json.loads(completed.stdout or "{}")
    streams = payload.get("streams") or []
    stream = streams[0] if streams else {}
    codec_name = str(stream.get("codec_name") or path.suffix.lstrip(".") or "unknown")

    sample_rate_value = stream.get("sample_rate")
    channels_value = stream.get("channels")
    duration_value = stream.get("duration")

    sample_rate = int(sample_rate_value) if sample_rate_value is not None else None
    channels = int(channels_value) if channels_value is not None else None

    try:
        duration_seconds = float(duration_value) if duration_value is not None else None
    except (TypeError, ValueError):
        duration_seconds = None

    return AudioProbe(
        path=path,
        codec_name=codec_name,
        sample_rate=sample_rate,
        channels=channels,
        duration_seconds=duration_seconds,
    )


def display_path(path: Path) -> str:
    root = repo_root()
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


def strip_decoded_suffix(stem: str) -> str:
    if stem.endswith("-decoded"):
        return stem[:-8]
    return stem


def find_sidecar_archive(path: Path) -> Path | None:
    for extension in (".pulsr", ".plsr"):
        candidate = path.with_suffix(extension)
        if candidate.exists():
            return candidate
    return None


def find_sidecar_decode(archive_path: Path) -> Path | None:
    base_stem = strip_decoded_suffix(archive_path.stem)
    candidates = [
        archive_path.with_name(f"{base_stem}-decoded.wav"),
        archive_path.with_name(f"{base_stem}.wav"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def build_target_artifact(path: Path, probe: AudioProbe | None = None) -> TargetArtifact | None:
    suffix = path.suffix.lower()
    if suffix in ARCHIVE_EXTENSIONS:
        decode_path = find_sidecar_decode(path)
        if decode_path is None:
            return None
        return TargetArtifact(
            artifact_path=path,
            decode_path=decode_path,
            codec_name=suffix.lstrip("."),
            label=display_path(path.with_suffix("")),
            size_bytes=path.stat().st_size,
            is_archive=True,
        )

    if suffix not in SUPPORTED_AUDIO_EXTENSIONS:
        return None

    archive_path = find_sidecar_archive(path)
    if archive_path is not None:
        archive_suffix = archive_path.suffix.lower().lstrip(".")
        return TargetArtifact(
            artifact_path=archive_path,
            decode_path=path,
            codec_name=archive_suffix,
            label=display_path(archive_path.with_suffix("")),
            size_bytes=archive_path.stat().st_size,
            is_archive=True,
        )

    codec_name = probe.codec_name if probe is not None else suffix.lstrip(".")
    return TargetArtifact(
        artifact_path=path,
        decode_path=path,
        codec_name=codec_name,
        label=display_path(path.with_suffix("")),
        size_bytes=path.stat().st_size,
        is_archive=False,
    )


def decode_to_mono_float32(path: Path, sample_rate: int, ffmpeg_exe: str) -> array:
    command = [
        ffmpeg_exe,
        "-nostdin",
        "-v",
        "error",
        "-i",
        str(path),
        "-ac",
        "1",
        "-ar",
        str(sample_rate),
        "-f",
        "f32le",
        "pipe:1",
    ]
    completed = subprocess.run(command, capture_output=True)
    if completed.returncode != 0:
        stderr = completed.stderr.decode("utf-8", errors="replace").strip()
        stdout = completed.stdout.decode("utf-8", errors="replace").strip()
        message = stderr or stdout or "unknown ffmpeg error"
        raise RuntimeError(f"ffmpeg decode failed for {path}: {message}")

    samples = array("f")
    samples.frombytes(completed.stdout)
    if sys.byteorder != "little":
        samples.byteswap()
    return samples


def format_bytes(num_bytes: int) -> str:
    kib = num_bytes / 1024.0
    if kib < 1024.0:
        return f"{kib:0.1f} KiB"
    return f"{kib / 1024.0:0.2f} MiB"


def format_float(value: float | None, digits: int = 2) -> str:
    if value is None:
        return "-"
    if math.isinf(value):
        return "inf"
    return f"{value:0.{digits}f}"


def format_seconds(value: float | None) -> str:
    return format_float(value, 3)


def is_reference_match(reference_norm: str, target_norm: str) -> bool:
    return reference_norm == target_norm or reference_norm in target_norm or target_norm in reference_norm


def choose_reference(reference_files: list[Path], target_path: Path) -> Path | None:
    target_norm = normalize_name(target_path.stem)
    best: Path | None = None
    best_score = -1
    for reference_path in reference_files:
        reference_norm = normalize_name(reference_path.stem)
        if not is_reference_match(reference_norm, target_norm):
            continue
        score = len(reference_norm)
        if score > best_score:
            best = reference_path
            best_score = score
    return best


def compute_row(
    reference_path: Path,
    reference_probe: AudioProbe,
    reference_samples: array,
    target: TargetArtifact,
    ffmpeg_exe: str,
) -> ComparisonRow:
    if reference_probe.sample_rate is None:
        raise RuntimeError(f"Could not read sample rate for reference file: {reference_path}")

    target_samples = decode_to_mono_float32(target.decode_path, reference_probe.sample_rate, ffmpeg_exe)

    sample_count = min(len(reference_samples), len(target_samples))
    if sample_count == 0:
        raise RuntimeError(f"No comparable samples found for {target.decode_path}")

    sum_sq_reference = 0.0
    sum_sq_residual = 0.0
    peak_residual = 0.0
    for index in range(sample_count):
        reference_sample = reference_samples[index]
        target_sample = target_samples[index]
        residual = reference_sample - target_sample
        sum_sq_reference += reference_sample * reference_sample
        sum_sq_residual += residual * residual
        peak_residual = max(peak_residual, abs(residual))

    rms_reference = math.sqrt(sum_sq_reference / sample_count)
    rms_residual = math.sqrt(sum_sq_residual / sample_count)

    snr_db = None
    if rms_residual > 0.0 and rms_reference > 0.0:
        snr_db = 20.0 * math.log10(rms_reference / rms_residual)

    psnr_db = None
    if rms_residual > 0.0:
        psnr_db = 20.0 * math.log10(1.0 / rms_residual)

    reference_duration_seconds = len(reference_samples) / float(reference_probe.sample_rate)
    target_duration_seconds = len(target_samples) / float(reference_probe.sample_rate)
    duration_delta_seconds = target_duration_seconds - reference_duration_seconds

    target_bitrate_kbps = 0.0
    if target_duration_seconds > 0.0:
        target_bitrate_kbps = (target.size_bytes * 8.0 / 1000.0) / target_duration_seconds

    return ComparisonRow(
        reference_path=reference_path,
        artifact_path=target.artifact_path,
        decode_path=target.decode_path,
        target_label=target.label,
        target_codec=target.codec_name,
        target_size_bytes=target.size_bytes,
        target_duration_seconds=target_duration_seconds,
        duration_delta_seconds=duration_delta_seconds,
        target_bitrate_kbps=target_bitrate_kbps,
        sample_count=sample_count,
        rms_reference=rms_reference,
        rms_residual=rms_residual,
        peak_residual=peak_residual,
        snr_db=snr_db,
        psnr_db=psnr_db,
    )


def target_dedup_key(path: Path, codec_name: str) -> tuple[str, str]:
    return normalize_name(strip_decoded_suffix(path.stem)), codec_name.lower()


def target_priority(target: TargetArtifact) -> tuple[int, float]:
    return (1 if target.is_archive else 0, target.artifact_path.stat().st_mtime)


def write_table(
    reference_path: Path,
    reference_probe: AudioProbe,
    reference_samples: array,
    rows: list[ComparisonRow],
) -> None:
    reference_duration = len(reference_samples) / float(reference_probe.sample_rate or 1)

    header = reference_path.stem
    print(f"\n== {header} ==")
    print(
        f"Reference: {reference_path.name} | codec={reference_probe.codec_name} | "
        f"size={format_bytes(reference_path.stat().st_size)} | "
        f"duration={format_seconds(reference_duration)} s"
    )
    print(
        "variant".ljust(32)
        + "  "
        + "codec".ljust(12)
        + "  "
        + "size".rjust(12)
        + "  "
        + "kbps".rjust(10)
        + "  "
        + "dur s".rjust(8)
        + "  "
        + "Δ s".rjust(8)
        + "  "
        + "SNR dB".rjust(10)
        + "  "
        + "PSNR dB".rjust(10)
        + "  "
        + "RMS resid".rjust(11)
        + "  "
        + "Peak resid".rjust(11)
    )
    print("-" * 120)

    reference_size = reference_path.stat().st_size
    reference_bitrate = 0.0
    if reference_duration > 0.0:
        reference_bitrate = (reference_size * 8.0 / 1000.0) / reference_duration

    print(
        reference_path.stem.ljust(32)
        + "  "
        + reference_probe.codec_name.ljust(12)
        + "  "
        + format_bytes(reference_size).rjust(12)
        + "  "
        + f"{reference_bitrate:0.1f}".rjust(10)
        + "  "
        + format_seconds(reference_duration).rjust(8)
        + "  "
        + "-".rjust(8)
        + "  "
        + "-".rjust(10)
        + "  "
        + "-".rjust(10)
        + "  "
        + "-".rjust(11)
        + "  "
        + "-".rjust(11)
    )

    for row in rows:
        print(
            row.target_label.ljust(32)
            + "  "
            + row.target_codec.ljust(12)
            + "  "
            + format_bytes(row.target_size_bytes).rjust(12)
            + "  "
            + f"{row.target_bitrate_kbps:0.1f}".rjust(10)
            + "  "
            + format_seconds(row.target_duration_seconds).rjust(8)
            + "  "
            + format_float(row.duration_delta_seconds, 3).rjust(8)
            + "  "
            + format_float(row.snr_db, 2).rjust(10)
            + "  "
            + format_float(row.psnr_db, 2).rjust(10)
            + "  "
            + format_float(row.rms_residual, 6).rjust(11)
            + "  "
            + format_float(row.peak_residual, 6).rjust(11)
        )


def write_csv(path: Path, grouped_rows: dict[Path, list[ComparisonRow]], reference_probes: dict[Path, AudioProbe]) -> None:
    fieldnames = [
        "reference",
        "reference_path",
        "target",
        "target_path",
        "artifact_path",
        "decode_path",
        "codec",
        "reference_codec",
        "reference_size_bytes",
        "target_size_bytes",
        "target_duration_seconds",
        "duration_delta_seconds",
        "target_bitrate_kbps",
        "sample_count",
        "rms_reference",
        "rms_residual",
        "peak_residual",
        "snr_db",
        "psnr_db",
    ]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for reference_path, rows in grouped_rows.items():
            reference_probe = reference_probes[reference_path]
            reference_size = reference_path.stat().st_size
            for row in rows:
                writer.writerow(
                    {
                        "reference": reference_path.name,
                        "reference_path": display_path(reference_path),
                        "target": row.artifact_path.name,
                        "target_path": display_path(row.artifact_path),
                        "artifact_path": display_path(row.artifact_path),
                        "decode_path": display_path(row.decode_path),
                        "codec": row.target_codec,
                        "reference_codec": reference_probe.codec_name,
                        "reference_size_bytes": reference_size,
                        "target_size_bytes": row.target_size_bytes,
                        "target_duration_seconds": f"{row.target_duration_seconds:0.6f}",
                        "duration_delta_seconds": f"{row.duration_delta_seconds:0.6f}",
                        "target_bitrate_kbps": f"{row.target_bitrate_kbps:0.3f}",
                        "sample_count": row.sample_count,
                        "rms_reference": f"{row.rms_reference:0.8f}",
                        "rms_residual": f"{row.rms_residual:0.8f}",
                        "peak_residual": f"{row.peak_residual:0.8f}",
                        "snr_db": "" if row.snr_db is None else f"{row.snr_db:0.6f}",
                        "psnr_db": "" if row.psnr_db is None else f"{row.psnr_db:0.6f}",
                    }
                )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Fast SNR/PSNR comparison for PULSAR reference WAVs and lossy codec outputs."
    )
    parser.add_argument(
        "--reference-root",
        action="append",
        type=Path,
        help="Reference root directory or file. Can be supplied multiple times.",
    )
    parser.add_argument(
        "--target-root",
        action="append",
        type=Path,
        help="Target root directory or file. Can be supplied multiple times.",
    )
    parser.add_argument(
        "--track",
        action="append",
        help="Only include reference tracks whose stem matches this text fuzzily. Can be repeated.",
    )
    parser.add_argument(
        "--csv",
        type=Path,
        help="Optional CSV output file.",
    )
    parser.add_argument(
        "--ffmpeg",
        default="ffmpeg",
        help="ffmpeg executable name or path.",
    )
    parser.add_argument(
        "--ffprobe",
        default="ffprobe",
        help="ffprobe executable name or path.",
    )
    parser.add_argument(
        "--jobs",
        type=int,
        default=max(1, min((os.cpu_count() or 4), 6)),
        help="Parallel workers used for decoding/comparison.",
    )
    return parser.parse_args()


def build_reference_list(reference_roots: list[Path]) -> list[Path]:
    files = collect_audio_files(reference_roots)
    return [candidate for candidate in files if candidate.suffix.lower() == ".wav"]


def build_target_list(target_roots: list[Path]) -> list[Path]:
    return collect_audio_files(target_roots, TARGET_EXTENSIONS)


def matches_track_filter(reference_path: Path, filters: list[str] | None) -> bool:
    if not filters:
        return True
    reference_norm = normalize_name(reference_path.stem)
    return any(normalize_name(filter_text) in reference_norm for filter_text in filters)


def main() -> int:
    args = parse_args()
    default_reference_roots, default_target_roots = default_roots()

    reference_roots = args.reference_root or default_reference_roots
    target_roots = args.target_root or default_target_roots

    reference_files = build_reference_list(reference_roots)
    target_files = build_target_list(target_roots)

    if not reference_files:
        print("No reference WAV files found.", file=sys.stderr)
        return 1
    if not target_files:
        print("No target audio files found.", file=sys.stderr)
        return 1

    filtered_references = [reference for reference in reference_files if matches_track_filter(reference, args.track)]
    if not filtered_references:
        print("No references matched the requested track filter.", file=sys.stderr)
        return 1

    grouped_targets: dict[Path, list[TargetArtifact]] = {reference: [] for reference in filtered_references}
    unmatched_targets: list[Path] = []
    target_probes: dict[Path, AudioProbe] = {}
    for target_path in target_files:
        if target_path.suffix.lower() in SUPPORTED_AUDIO_EXTENSIONS:
            try:
                target_probes[target_path] = probe_audio(target_path, args.ffprobe)
            except Exception as exc:
                print(f"Failed to probe target {target_path}: {exc}", file=sys.stderr)
                return 1

    deduped_targets: dict[tuple[str, str], TargetArtifact] = {}
    for target_path in target_files:
        target_probe = target_probes.get(target_path)
        target = build_target_artifact(target_path, target_probe)
        if target is None:
            continue

        key = target_dedup_key(target.artifact_path, target.codec_name)
        current = deduped_targets.get(key)
        if current is None or target_priority(target) > target_priority(current):
            deduped_targets[key] = target

    for target in deduped_targets.values():
        matched_reference = choose_reference(filtered_references, target.artifact_path)
        if matched_reference is None:
            unmatched_targets.append(target.artifact_path)
            continue
        grouped_targets[matched_reference].append(target)

    reference_probes: dict[Path, AudioProbe] = {}
    reference_samples: dict[Path, array] = {}
    for reference_path in filtered_references:
        try:
            probe = probe_audio(reference_path, args.ffprobe)
            if probe.sample_rate is None:
                raise RuntimeError(f"Could not read sample rate for reference file: {reference_path}")
            reference_probes[reference_path] = probe
            reference_samples[reference_path] = decode_to_mono_float32(reference_path, probe.sample_rate, args.ffmpeg)
        except Exception as exc:
            print(f"Failed to probe reference {reference_path}: {exc}", file=sys.stderr)
            return 1

    grouped_rows: dict[Path, list[ComparisonRow]] = {reference: [] for reference in filtered_references}
    tasks: list[tuple[Path, TargetArtifact]] = []
    for reference_path, targets in grouped_targets.items():
        tasks.extend((reference_path, target) for target in targets)

    if not tasks:
        print("No target files matched the selected references.", file=sys.stderr)
        return 1

    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as executor:
        future_map = {
            executor.submit(
                compute_row,
                reference_path,
                reference_probes[reference_path],
                reference_samples[reference_path],
                target,
                args.ffmpeg,
            ): (reference_path, target)
            for reference_path, target in tasks
        }

        for future in as_completed(future_map):
            reference_path, target = future_map[future]
            try:
                row = future.result()
            except Exception as exc:
                print(f"Failed to compare {target.artifact_path} against {reference_path}: {exc}", file=sys.stderr)
                return 1
            grouped_rows[reference_path].append(row)

    for rows in grouped_rows.values():
        rows.sort(key=lambda row: (row.target_bitrate_kbps, row.target_size_bytes, row.target_label.lower()))

    for reference_path in filtered_references:
        write_table(reference_path, reference_probes[reference_path], reference_samples[reference_path], grouped_rows[reference_path])

    if args.csv:
        args.csv.parent.mkdir(parents=True, exist_ok=True)
        write_csv(args.csv, grouped_rows, reference_probes)
        print(f"\nCSV written: {args.csv}")

    if unmatched_targets:
        print(f"\nUnmatched targets: {len(unmatched_targets)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())