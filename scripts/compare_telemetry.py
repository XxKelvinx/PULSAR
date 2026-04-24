#!/usr/bin/python3

"""Compare baseline and planned telemetry files."""

from __future__ import annotations

import argparse
import statistics

from score_telemetry import frame_difficulty, read_frames, summarize_segments


def correlation(xs: list[float], ys: list[float]) -> float:
    mean_x = statistics.fmean(xs)
    mean_y = statistics.fmean(ys)
    numerator = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    denominator = (
        sum((x - mean_x) ** 2 for x in xs) * sum((y - mean_y) ** 2 for y in ys)
    ) ** 0.5
    return 0.0 if denominator == 0 else numerator / denominator


def average_kbps(packet_bytes: list[int], sample_rate: int, frame_samples: int) -> float:
    return sum(packet_bytes) * 8 * sample_rate / len(packet_bytes) / frame_samples / 1000.0


def main() -> None:
    parser = argparse.ArgumentParser(description="Compare baseline and planned telemetry files.")
    parser.add_argument("baseline_csv")
    parser.add_argument("planned_csv")
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--segment-ms", type=int, default=2000)
    parser.add_argument("--extremes", type=int, default=10, help="Number of hardest/easiest segments to compare")
    args = parser.parse_args()

    baseline = read_frames(args.baseline_csv)
    planned = read_frames(args.planned_csv)
    if len(baseline) != len(planned):
        raise ValueError("Baseline and planned telemetry must have the same frame count")

    frame_samples = baseline[0].input_samples
    baseline_bytes = [frame.packet_bytes for frame in baseline]
    planned_bytes = [frame.packet_bytes for frame in planned]
    difficulties = [frame_difficulty(frame) for frame in baseline]

    baseline_segments = summarize_segments(baseline, args.sample_rate, args.segment_ms)
    planned_segments = summarize_segments(planned, args.sample_rate, args.segment_ms)

    hardest = sorted(
        range(len(baseline_segments)),
        key=lambda index: baseline_segments[index].smoothed_difficulty,
        reverse=True,
    )[: args.extremes]
    easiest = sorted(
        range(len(baseline_segments)),
        key=lambda index: baseline_segments[index].smoothed_difficulty,
    )[: args.extremes]

    print(f"baseline_avg_kbps={average_kbps(baseline_bytes, args.sample_rate, frame_samples):.3f}")
    print(f"planned_avg_kbps={average_kbps(planned_bytes, args.sample_rate, frame_samples):.3f}")
    print(f"baseline_diff_byte_corr={correlation(difficulties, baseline_bytes):.4f}")
    print(f"planned_diff_byte_corr={correlation(difficulties, planned_bytes):.4f}")
    print(
        "hardest_avg_bytes "
        f"baseline={statistics.fmean(baseline_segments[i].avg_packet_bytes for i in hardest):.3f} "
        f"planned={statistics.fmean(planned_segments[i].avg_packet_bytes for i in hardest):.3f}"
    )
    print(
        "easiest_avg_bytes "
        f"baseline={statistics.fmean(baseline_segments[i].avg_packet_bytes for i in easiest):.3f} "
        f"planned={statistics.fmean(planned_segments[i].avg_packet_bytes for i in easiest):.3f}"
    )
    print(f"baseline_packet_std={statistics.pstdev(baseline_bytes):.3f}")
    print(f"planned_packet_std={statistics.pstdev(planned_bytes):.3f}")


if __name__ == "__main__":
    main()