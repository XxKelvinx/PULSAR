#!/usr/bin/python3

"""Summarize opus_demo telemetry CSV into segment difficulty scores.

This script is intentionally simple and deterministic. It does not change the
codec. It turns per-frame telemetry into segment-level scores that can later
drive an offline planner.
"""

from __future__ import annotations

import argparse
import csv
import math
import os
import statistics
from dataclasses import dataclass


@dataclass
class FrameTelemetry:
    frame_index: int
    input_samples: int
    packet_bytes: int
    requested_bitrate_bps: int
    analysis_valid: int
    tonality: float
    activity: float
    activity_probability: float
    music_prob: float
    bandwidth: int
    tonality_slope: float
    noisiness: float
    max_pitch_ratio: float


@dataclass
class SegmentSummary:
    segment_index: int
    start_s: float
    end_s: float
    frames: int
    avg_packet_bytes: float
    packet_stddev: float
    mean_activity: float
    mean_tonality: float
    mean_music_prob: float
    mean_bandwidth: float
    raw_difficulty: float
    smoothed_difficulty: float = 0.0
    budget_scale: float = 1.0
    segment_class: str = "steady"


def clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(value, maximum))


def safe_int(row: dict[str, str], key: str) -> int:
    value = row.get(key, "0").strip()
    return int(value) if value else 0


def safe_float(row: dict[str, str], key: str) -> float:
    value = row.get(key, "0").strip()
    return float(value) if value else 0.0


def read_frames(csv_path: str) -> list[FrameTelemetry]:
    frames: list[FrameTelemetry] = []
    with open(csv_path, "r", newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            frames.append(
                FrameTelemetry(
                    frame_index=safe_int(row, "frame_index"),
                    input_samples=safe_int(row, "input_samples"),
                    packet_bytes=safe_int(row, "packet_bytes"),
                    requested_bitrate_bps=safe_int(row, "requested_bitrate_bps"),
                    analysis_valid=safe_int(row, "analysis_valid"),
                    tonality=safe_float(row, "analysis_tonality"),
                    activity=safe_float(row, "analysis_activity"),
                    activity_probability=safe_float(row, "analysis_activity_probability"),
                    music_prob=safe_float(row, "analysis_music_prob"),
                    bandwidth=safe_int(row, "analysis_bandwidth"),
                    tonality_slope=safe_float(row, "analysis_tonality_slope"),
                    noisiness=safe_float(row, "analysis_noisiness"),
                    max_pitch_ratio=safe_float(row, "analysis_max_pitch_ratio"),
                )
            )
    if not frames:
        raise ValueError(f"No telemetry rows found in {csv_path}")
    return frames


def frame_difficulty(frame: FrameTelemetry) -> float:
    bandwidth_norm = clamp(frame.bandwidth / 20.0, 0.0, 1.0)
    slope_norm = clamp(abs(frame.tonality_slope) * 4.0, 0.0, 1.0)
    noisiness_norm = clamp(frame.noisiness, 0.0, 1.0)
    transient_hint = clamp(frame.activity * (1.1 - 0.5 * frame.tonality), 0.0, 1.0)

    if not frame.analysis_valid:
        return 0.0

    score = (
        0.28 * clamp(frame.activity, 0.0, 1.0)
        + 0.20 * noisiness_norm
        + 0.16 * clamp(frame.tonality, 0.0, 1.0)
        + 0.14 * bandwidth_norm
        + 0.12 * clamp(frame.activity_probability, 0.0, 1.0)
        + 0.10 * slope_norm
        + 0.10 * transient_hint
    )
    return clamp(score, 0.0, 1.0)


def packet_stddev(values: list[int]) -> float:
    if len(values) < 2:
        return 0.0
    return statistics.pstdev(values)


def classify_segment(score: float) -> str:
    if score >= 0.82:
        return "killer"
    if score >= 0.67:
        return "hard"
    if score >= 0.50:
        return "active"
    if score >= 0.32:
        return "steady"
    return "easy"


def summarize_segments(frames: list[FrameTelemetry], sample_rate: int, segment_ms: int) -> list[SegmentSummary]:
    frames_per_segment = max(1, round((segment_ms / 1000.0) * sample_rate / frames[0].input_samples))
    summaries: list[SegmentSummary] = []

    for segment_index, start in enumerate(range(0, len(frames), frames_per_segment)):
        chunk = frames[start:start + frames_per_segment]
        difficulties = [frame_difficulty(frame) for frame in chunk]
        packets = [frame.packet_bytes for frame in chunk]
        activities = [frame.activity for frame in chunk]
        tonalities = [frame.tonality for frame in chunk]
        music_probs = [frame.music_prob for frame in chunk]
        bandwidths = [frame.bandwidth for frame in chunk]

        mean_difficulty = statistics.fmean(difficulties)
        peak_difficulty = max(difficulties)
        volatility = clamp(packet_stddev(packets) / 250.0, 0.0, 1.0)
        raw_difficulty = clamp(0.62 * mean_difficulty + 0.28 * peak_difficulty + 0.10 * volatility, 0.0, 1.0)

        start_s = sum(frame.input_samples for frame in frames[:start]) / sample_rate
        end_s = start_s + sum(frame.input_samples for frame in chunk) / sample_rate

        summaries.append(
            SegmentSummary(
                segment_index=segment_index,
                start_s=start_s,
                end_s=end_s,
                frames=len(chunk),
                avg_packet_bytes=statistics.fmean(packets),
                packet_stddev=packet_stddev(packets),
                mean_activity=statistics.fmean(activities),
                mean_tonality=statistics.fmean(tonalities),
                mean_music_prob=statistics.fmean(music_probs),
                mean_bandwidth=statistics.fmean(bandwidths),
                raw_difficulty=raw_difficulty,
            )
        )

    apply_smoothing_and_budget_scale(summaries)
    return summaries


def apply_smoothing_and_budget_scale(summaries: list[SegmentSummary]) -> None:
    if not summaries:
        return

    raw_scores = [summary.raw_difficulty for summary in summaries]
    global_mean = statistics.fmean(raw_scores)
    global_std = statistics.pstdev(raw_scores) if len(raw_scores) > 1 else 0.0

    for index, summary in enumerate(summaries):
        weighted_sum = 0.0
        total_weight = 0.0
        for neighbor_index in range(max(0, index - 1), min(len(summaries), index + 2)):
            distance = abs(index - neighbor_index)
            weight = 2.0 if distance == 0 else 1.0
            weighted_sum += summaries[neighbor_index].raw_difficulty * weight
            total_weight += weight

        summary.smoothed_difficulty = weighted_sum / total_weight

        if global_std > 1e-9:
            normalized = (summary.smoothed_difficulty - global_mean) / global_std
        else:
            normalized = 0.0

        summary.budget_scale = clamp(1.0 + 0.10 * normalized, 0.85, 1.15)
        summary.segment_class = classify_segment(summary.smoothed_difficulty)


def write_segments_csv(summaries: list[SegmentSummary], output_path: str) -> None:
    fieldnames = [
        "segment_index",
        "start_s",
        "end_s",
        "frames",
        "avg_packet_bytes",
        "packet_stddev",
        "mean_activity",
        "mean_tonality",
        "mean_music_prob",
        "mean_bandwidth",
        "raw_difficulty",
        "smoothed_difficulty",
        "budget_scale",
        "segment_class",
    ]
    with open(output_path, "w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for summary in summaries:
            writer.writerow(
                {
                    "segment_index": summary.segment_index,
                    "start_s": f"{summary.start_s:.3f}",
                    "end_s": f"{summary.end_s:.3f}",
                    "frames": summary.frames,
                    "avg_packet_bytes": f"{summary.avg_packet_bytes:.3f}",
                    "packet_stddev": f"{summary.packet_stddev:.3f}",
                    "mean_activity": f"{summary.mean_activity:.6f}",
                    "mean_tonality": f"{summary.mean_tonality:.6f}",
                    "mean_music_prob": f"{summary.mean_music_prob:.6f}",
                    "mean_bandwidth": f"{summary.mean_bandwidth:.3f}",
                    "raw_difficulty": f"{summary.raw_difficulty:.6f}",
                    "smoothed_difficulty": f"{summary.smoothed_difficulty:.6f}",
                    "budget_scale": f"{summary.budget_scale:.6f}",
                    "segment_class": summary.segment_class,
                }
            )


def default_output_path(input_path: str) -> str:
    base, _ = os.path.splitext(input_path)
    return f"{base}.segments.csv"


def main() -> None:
    parser = argparse.ArgumentParser(description="Summarize opus_demo telemetry into segment scores.")
    parser.add_argument("input_csv", help="Path to telemetry CSV from opus_demo -dump_csv")
    parser.add_argument("--output", help="Path to output segment CSV")
    parser.add_argument("--sample-rate", type=int, default=48000, help="Sample rate used during encode")
    parser.add_argument("--segment-ms", type=int, default=2000, help="Segment size in milliseconds")
    args = parser.parse_args()

    frames = read_frames(args.input_csv)
    output_path = args.output or default_output_path(args.input_csv)
    summaries = summarize_segments(frames, args.sample_rate, args.segment_ms)
    write_segments_csv(summaries, output_path)
    print(f"Wrote {len(summaries)} segment summaries to {output_path}")


if __name__ == "__main__":
    main()