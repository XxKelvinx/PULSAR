from __future__ import annotations

import csv
import ctypes
import hashlib
import math
import struct
import tempfile
import threading
import time
import tkinter as tk
from dataclasses import dataclass
from pathlib import Path
from tkinter import filedialog, messagebox, ttk


MIN_BLOCK_SIZE = 256
MAX_BLOCK_SIZE = 16384
PLAYHEAD_UPDATE_MS = 16
PLAYHEAD_DEVICE_SYNC_MS = 180
GRAPH_BACKGROUND = "#020617"
MIN_PLAYBACK_SPEED = 0.25
MAX_PLAYBACK_SPEED = 1.5


def blend_hex(color: str, background: str, alpha: float) -> str:
    alpha = max(0.0, min(1.0, alpha))

    def parse(hex_color: str) -> tuple[int, int, int]:
        hex_color = hex_color.lstrip("#")
        return int(hex_color[0:2], 16), int(hex_color[2:4], 16), int(hex_color[4:6], 16)

    fg_r, fg_g, fg_b = parse(color)
    bg_r, bg_g, bg_b = parse(background)
    out_r = int((fg_r * alpha) + (bg_r * (1.0 - alpha)))
    out_g = int((fg_g * alpha) + (bg_g * (1.0 - alpha)))
    out_b = int((fg_b * alpha) + (bg_b * (1.0 - alpha)))
    return f"#{out_r:02x}{out_g:02x}{out_b:02x}"


def mci_send(command: str) -> str:
    buffer = ctypes.create_unicode_buffer(260)
    error_code = ctypes.windll.winmm.mciSendStringW(command, buffer, len(buffer), 0)
    if error_code != 0:
        error_text = ctypes.create_unicode_buffer(260)
        ctypes.windll.winmm.mciGetErrorStringW(error_code, error_text, len(error_text))
        raise RuntimeError(error_text.value or f"MCI error {error_code}: {command}")
    return buffer.value


@dataclass
class PlotSeries:
    points: list[tuple[float, float]]
    title: str
    color: str
    y_label: str
    y_min: float | None = None
    y_max: float | None = None
    scale_mode: str = "linear"
    band_floor: float = 0.0
    band_ceiling: float = 1.0
    draw_style: str = "line"


@dataclass
class ComparisonMetrics:
    duration_seconds: float = 0.0
    snr_db: float | None = None
    rms_original: float | None = None
    rms_run: float | None = None
    rms_residual: float | None = None
    peak_residual: float | None = None
    avg_audio_kbps: float | None = None


def read_chunks(raw: bytes) -> dict[bytes, bytes]:
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError("Unsupported WAV file.")

    chunks: dict[bytes, bytes] = {}
    offset = 12
    while offset + 8 <= len(raw):
        chunk_id = raw[offset:offset + 4]
        chunk_size = struct.unpack_from("<I", raw, offset + 4)[0]
        chunk_data_start = offset + 8
        chunk_data_end = chunk_data_start + chunk_size
        chunks[chunk_id] = raw[chunk_data_start:chunk_data_end]
        offset = chunk_data_end + (chunk_size & 1)
    return chunks


def build_rate_adjusted_wav(source_path: Path, speed: float) -> Path:
    speed = clamp(speed, MIN_PLAYBACK_SPEED, MAX_PLAYBACK_SPEED)
    if abs(speed - 1.0) < 1e-6:
        return source_path

    raw = bytearray(source_path.read_bytes())
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError("Playback speed control currently requires a WAV file.")

    offset = 12
    patched = False
    while offset + 8 <= len(raw):
        chunk_id = raw[offset:offset + 4]
        chunk_size = struct.unpack_from("<I", raw, offset + 4)[0]
        chunk_data_start = offset + 8
        if chunk_id == b"fmt " and chunk_size >= 12:
            source_rate = struct.unpack_from("<I", raw, chunk_data_start + 4)[0]
            block_align = struct.unpack_from("<H", raw, chunk_data_start + 12)[0] if chunk_size >= 14 else 0
            target_rate = max(1, int(round(source_rate * speed)))
            target_byte_rate = target_rate * max(1, block_align)
            struct.pack_into("<I", raw, chunk_data_start + 4, target_rate)
            struct.pack_into("<I", raw, chunk_data_start + 8, target_byte_rate)
            patched = True
            break
        offset = chunk_data_start + chunk_size + (chunk_size & 1)

    if not patched:
        raise ValueError("Playback speed control could not find a valid WAV fmt chunk.")

    source_stat = source_path.stat()
    cache_key = hashlib.sha1(
        f"{source_path.resolve()}|{source_stat.st_mtime_ns}|{source_stat.st_size}|{speed:0.4f}".encode("utf-8")
    ).hexdigest()[:16]
    cache_dir = Path(tempfile.gettempdir()) / "pulsar-planner-visualizer"
    cache_dir.mkdir(parents=True, exist_ok=True)
    adjusted_path = cache_dir / f"{source_path.stem}-speed-{cache_key}.wav"
    if not adjusted_path.exists():
        adjusted_path.write_bytes(raw)
    return adjusted_path


def decode_wave_file(path: Path) -> tuple[int, list[float]]:
    raw = path.read_bytes()
    chunks = read_chunks(raw)
    fmt = chunks.get(b"fmt ")
    data = chunks.get(b"data")
    if fmt is None or data is None:
        raise ValueError("WAV file is missing fmt or data chunk.")

    format_tag, channels, sample_rate = struct.unpack_from("<HHI", fmt, 0)
    bits_per_sample = struct.unpack_from("<H", fmt, 14)[0]
    if channels <= 0:
        raise ValueError("Invalid channel count.")

    sample_values = decode_pcm_samples(format_tag, bits_per_sample, channels, data, fmt)
    mono = mix_to_mono(sample_values, channels)
    return sample_rate, mono


def decode_pcm_samples(format_tag: int, bits_per_sample: int, channels: int, data: bytes, fmt_chunk: bytes) -> list[float]:
    actual_tag = format_tag
    if format_tag == 0xFFFE and len(fmt_chunk) >= 40:
        subformat = fmt_chunk[24:40]
        if subformat.startswith(b"\x01\x00\x00\x00"):
            actual_tag = 1
        elif subformat.startswith(b"\x03\x00\x00\x00"):
            actual_tag = 3

    bytes_per_sample = max(1, bits_per_sample // 8)
    frame_width = bytes_per_sample * channels
    frame_count = len(data) // frame_width
    values: list[float] = []

    if actual_tag == 1 and bits_per_sample == 16:
        unpacked = struct.unpack("<{}h".format(frame_count * channels), data[:frame_count * frame_width])
        values.extend(sample / 32768.0 for sample in unpacked)
        return values

    if actual_tag == 1 and bits_per_sample == 24:
        for offset in range(0, frame_count * frame_width, 3):
            sample_bytes = data[offset:offset + 3]
            value = int.from_bytes(sample_bytes, "little", signed=False)
            if value & 0x800000:
                value -= 0x1000000
            values.append(value / 8388608.0)
        return values

    if actual_tag == 1 and bits_per_sample == 32:
        unpacked = struct.unpack("<{}i".format(frame_count * channels), data[:frame_count * frame_width])
        values.extend(sample / 2147483648.0 for sample in unpacked)
        return values

    if actual_tag == 3 and bits_per_sample == 32:
        unpacked = struct.unpack("<{}f".format(frame_count * channels), data[:frame_count * frame_width])
        values.extend(float(sample) for sample in unpacked)
        return values

    raise ValueError(f"Unsupported WAV encoding: format={format_tag}, bits={bits_per_sample}")


def mix_to_mono(samples: list[float], channels: int) -> list[float]:
    if channels == 1:
        return samples
    mono: list[float] = []
    for index in range(0, len(samples), channels):
        mono.append(sum(samples[index:index + channels]) / channels)
    return mono


def resample_mono(samples: list[float], source_sample_rate: int, target_sample_rate: int) -> list[float]:
    if not samples or source_sample_rate <= 0 or target_sample_rate <= 0:
        return samples
    if source_sample_rate == target_sample_rate:
        return samples
    if len(samples) == 1:
        return samples.copy()

    duration_seconds = len(samples) / float(source_sample_rate)
    target_length = max(1, int(round(duration_seconds * target_sample_rate)))
    if target_length == 1:
        return [samples[0]]

    resampled = [0.0] * target_length
    position_scale = source_sample_rate / float(target_sample_rate)
    last_index = len(samples) - 1

    for target_index in range(target_length):
        source_position = target_index * position_scale
        left_index = min(last_index, int(source_position))
        right_index = min(last_index, left_index + 1)
        fraction = source_position - left_index
        left_value = samples[left_index]
        right_value = samples[right_index]
        resampled[target_index] = left_value + ((right_value - left_value) * fraction)

    return resampled


def design_biquad_highpass(sample_rate: int, cutoff_hz: float, q: float = 0.70710678) -> tuple[float, float, float, float, float] | None:
    nyquist = sample_rate * 0.5
    if sample_rate <= 0 or cutoff_hz <= 0.0 or cutoff_hz >= nyquist:
        return None

    omega = 2.0 * math.pi * cutoff_hz / sample_rate
    sin_omega = math.sin(omega)
    cos_omega = math.cos(omega)
    alpha = sin_omega / (2.0 * q)

    b0 = (1.0 + cos_omega) * 0.5
    b1 = -(1.0 + cos_omega)
    b2 = (1.0 + cos_omega) * 0.5
    a0 = 1.0 + alpha
    a1 = -2.0 * cos_omega
    a2 = 1.0 - alpha
    return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0)


def design_biquad_lowpass(sample_rate: int, cutoff_hz: float, q: float = 0.70710678) -> tuple[float, float, float, float, float] | None:
    nyquist = sample_rate * 0.5
    if sample_rate <= 0 or cutoff_hz <= 0.0 or cutoff_hz >= nyquist:
        return None

    omega = 2.0 * math.pi * cutoff_hz / sample_rate
    sin_omega = math.sin(omega)
    cos_omega = math.cos(omega)
    alpha = sin_omega / (2.0 * q)

    b0 = (1.0 - cos_omega) * 0.5
    b1 = 1.0 - cos_omega
    b2 = (1.0 - cos_omega) * 0.5
    a0 = 1.0 + alpha
    a1 = -2.0 * cos_omega
    a2 = 1.0 - alpha
    return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0)


def apply_biquad(samples: list[float], coefficients: tuple[float, float, float, float, float] | None) -> list[float]:
    if not samples or coefficients is None:
        return samples.copy()

    b0, b1, b2, a1, a2 = coefficients
    x1 = 0.0
    x2 = 0.0
    y1 = 0.0
    y2 = 0.0
    output: list[float] = []
    append_output = output.append

    for sample in samples:
        y0 = (b0 * sample) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2)
        append_output(y0)
        x2 = x1
        x1 = sample
        y2 = y1
        y1 = y0

    return output


def apply_zero_phase_biquad(samples: list[float], coefficients: tuple[float, float, float, float, float] | None) -> list[float]:
    if not samples or coefficients is None:
        return samples.copy()
    forward = apply_biquad(samples, coefficients)
    reverse = apply_biquad(list(reversed(forward)), coefficients)
    reverse.reverse()
    return reverse


def bandlimit_difference_waveform(samples: list[float], sample_rate: int, low_cut_hz: float = 500.0, high_cut_hz: float = 18000.0) -> list[float]:
    if not samples or sample_rate <= 0:
        return samples.copy()

    nyquist = sample_rate * 0.5
    safe_low_cut = clamp(low_cut_hz, 0.0, max(0.0, nyquist * 0.9))
    safe_high_cut = clamp(high_cut_hz, 0.0, max(0.0, nyquist * 0.95))

    if safe_high_cut > 0.0 and safe_low_cut >= safe_high_cut:
        safe_low_cut = safe_high_cut * 0.5

    filtered = samples.copy()
    if safe_low_cut >= 1.0:
        filtered = apply_zero_phase_biquad(filtered, design_biquad_highpass(sample_rate, safe_low_cut))
    if safe_high_cut >= 1.0 and safe_high_cut < nyquist:
        filtered = apply_zero_phase_biquad(filtered, design_biquad_lowpass(sample_rate, safe_high_cut))
    return filtered


def compute_entropy_series(
    samples: list[float],
    sample_rate: int,
    window_size: int = 2048,
    hop_size: int = 256,
    bins: int = 32,
    max_points: int = 2400,
) -> list[tuple[float, float]]:
    if not samples:
        return []

    entropy_points: list[tuple[float, float]] = []
    normalization = math.log2(bins)
    adaptive_hop = max(hop_size, max(1, (len(samples) - window_size) // max(1, max_points)))
    adaptive_hop = max(64, (adaptive_hop + 63) // 64 * 64)

    for start in range(0, max(1, len(samples) - window_size), adaptive_hop):
        window = samples[start:start + window_size]
        if not window:
            break

        peak = max(abs(sample) for sample in window)
        if peak <= 1e-12:
            entropy_points.append((start / sample_rate, 0.0))
            continue

        histogram = [0] * bins
        for sample in window:
            magnitude = min(0.999999, abs(sample) / peak)
            bucket = min(bins - 1, int(magnitude * bins))
            histogram[bucket] += 1

        total = len(window)
        entropy = 0.0
        for count in histogram:
            if count == 0:
                continue
            probability = count / total
            entropy -= probability * math.log2(probability)

        entropy_points.append((start / sample_rate, entropy / normalization if normalization > 0 else 0.0))

    return entropy_points


def load_csv_rows(path: Path) -> list[dict[str, str]]:
    with path.open("r", newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def parse_csv_float(value: str | None, default: float = 0.0) -> float:
    if value is None:
        return default
    text = value.strip()
    if not text or text.lower() == "none":
        return default
    try:
        return float(text)
    except ValueError:
        return default


def parse_csv_int(value: str | None, default: int = 0) -> int:
    if value is None:
        return default
    text = value.strip()
    if not text or text.lower() == "none":
        return default
    try:
        return int(text)
    except ValueError:
        return default


def choose_block_series(run_folder: Path) -> list[tuple[float, float]]:
    segment_logs = sorted(run_folder.glob("planner-channel-*-segments.csv"))
    render_logs = sorted(run_folder.glob("planner-channel-*-render.csv"))
    source = segment_logs[0] if segment_logs else (render_logs[0] if render_logs else None)
    if source is None:
        raise FileNotFoundError("No planner render or segment CSV found in the selected folder.")

    rows = load_csv_rows(source)
    if not rows:
        return []

    points: list[tuple[float, float]] = []

    for index, row in enumerate(rows):
        time_s = parse_csv_float(row.get("start_time_s") or row.get("time_s") or "0.0")
        block_size = parse_csv_int(row.get("block_size") or row.get("target_block_size"), MIN_BLOCK_SIZE)
        block_value = math.log2(max(MIN_BLOCK_SIZE, block_size))
        points.append((time_s, block_value))

        if index + 1 < len(rows):
            next_time_s = parse_csv_float(rows[index + 1].get("start_time_s") or rows[index + 1].get("time_s") or str(time_s))
            if next_time_s > time_s:
                points.append((next_time_s, block_value))

    return points


def try_choose_block_series(candidate_folders: list[Path]) -> tuple[list[tuple[float, float]], Path | None]:
    seen: set[Path] = set()
    for folder in candidate_folders:
        resolved = folder.resolve()
        if resolved in seen or not folder.exists() or not folder.is_dir():
            continue
        seen.add(resolved)
        try:
            return choose_block_series(folder), folder
        except FileNotFoundError:
            continue

    return [], None


def parse_summary_metrics(run_folder: Path) -> ComparisonMetrics:
    summary_path = run_folder / "summary.txt"
    metrics = ComparisonMetrics()
    if not summary_path.exists():
        return metrics

    for raw_line in summary_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or ":" not in line:
            continue
        key, value = line.split(":", 1)
        key = key.strip().lower()
        value = value.strip().replace(",", ".")
        try:
            numeric = float(value)
        except ValueError:
            continue

        if key == "duration seconds":
            metrics.duration_seconds = numeric
        elif key == "snr db":
            metrics.snr_db = numeric
        elif key == "rms original":
            metrics.rms_original = numeric
        elif key == "rms processed":
            metrics.rms_run = numeric
        elif key == "rms residual":
            metrics.rms_residual = numeric
        elif key == "peak residual":
            metrics.peak_residual = numeric
        elif key == "audio payload bitrate kbps":
            metrics.avg_audio_kbps = numeric
        elif key == "estimated pure audio bitrate kbps" and metrics.avg_audio_kbps is None:
            metrics.avg_audio_kbps = numeric

    return metrics


def compute_comparison_metrics(original_samples: list[float], run_samples: list[float], sample_rate: int) -> ComparisonMetrics:
    length = min(len(original_samples), len(run_samples))
    if length <= 0:
        return ComparisonMetrics()

    sum_sq_original = 0.0
    sum_sq_run = 0.0
    sum_sq_residual = 0.0
    peak_residual = 0.0
    for index in range(length):
        original = original_samples[index]
        run = run_samples[index]
        residual = original - run
        sum_sq_original += original * original
        sum_sq_run += run * run
        sum_sq_residual += residual * residual
        peak_residual = max(peak_residual, abs(residual))

    rms_original = math.sqrt(sum_sq_original / length)
    rms_run = math.sqrt(sum_sq_run / length)
    rms_residual = math.sqrt(sum_sq_residual / length)
    snr_db = None
    if rms_residual > 0 and rms_original > 0:
        snr_db = 20.0 * math.log10(rms_original / rms_residual)

    return ComparisonMetrics(
        duration_seconds=length / max(sample_rate, 1),
        snr_db=snr_db,
        rms_original=rms_original,
        rms_run=rms_run,
        rms_residual=rms_residual,
        peak_residual=peak_residual,
    )


def reduce_points(points: list[tuple[float, float]], max_points: int) -> list[tuple[float, float]]:
    if len(points) <= max_points:
        return points

    reduced: list[tuple[float, float]] = []
    bucket_size = len(points) / max_points
    cursor = 0.0
    while int(cursor) < len(points):
        start = int(cursor)
        end = min(len(points), max(start + 1, int(cursor + bucket_size)))
        bucket = points[start:end]
        if not bucket:
            break
        min_point = min(bucket, key=lambda point: point[1])
        max_point = max(bucket, key=lambda point: point[1])
        if min_point[0] <= max_point[0]:
            reduced.extend((min_point, max_point))
        else:
            reduced.extend((max_point, min_point))
        cursor += bucket_size

    if reduced and reduced[-1] != points[-1]:
        reduced.append(points[-1])

    return reduced[: max_points * 2]


def resolve_y_range(series: PlotSeries) -> tuple[float, float]:
    if not series.points:
        return (0.0, 1.0)

    if series.y_min is not None and series.y_max is not None:
        return (series.y_min, series.y_max)

    values = [point[1] for point in series.points]
    y_min = min(values)
    y_max = max(values)
    if abs(y_max - y_min) < 1e-9:
        pad = max(0.05, abs(y_max) * 0.15, 0.05)
        return (y_min - pad, y_max + pad)

    pad = (y_max - y_min) * 0.08
    return (y_min - pad, y_max + pad)


def normalize_value(value: float, y_min: float, y_max: float) -> float:
    y_span = max(1e-9, y_max - y_min)
    return max(0.0, min(1.0, (value - y_min) / y_span))


def apply_scale_mode(normalized: float, scale_mode: str) -> float:
    normalized = max(0.0, min(1.0, normalized))
    if scale_mode == "log":
        return math.log1p(normalized * 15.0) / math.log1p(15.0)
    if scale_mode == "exp":
        return math.pow(normalized, 1.8)
    return normalized


def apply_vertical_band(normalized: float, band_floor: float, band_ceiling: float) -> float:
    band_floor = max(0.0, min(1.0, band_floor))
    band_ceiling = max(band_floor, min(1.0, band_ceiling))
    return band_floor + (normalized * (band_ceiling - band_floor))


def map_series_value_to_canvas_y(series: PlotSeries, value: float, top: float, bottom: float) -> float:
    y_min, y_max = resolve_y_range(series)
    y_fraction = normalize_value(value, y_min, y_max)
    y_fraction = apply_scale_mode(y_fraction, series.scale_mode)
    y_fraction = apply_vertical_band(y_fraction, series.band_floor, series.band_ceiling)
    return bottom - (y_fraction * (bottom - top))


def clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, value))


def collect_visible_points(points: list[tuple[float, float]], view_start: float, view_end: float) -> list[tuple[float, float]]:
    if not points:
        return []

    visible: list[tuple[float, float]] = []
    first_after: tuple[float, float] | None = None
    previous: tuple[float, float] | None = None
    for point in points:
        time_s = point[0]
        if time_s < view_start:
            previous = point
            continue
        if time_s > view_end:
            first_after = point
            break
        if not visible and previous is not None:
            visible.append(previous)
        visible.append(point)

    if visible and first_after is not None:
        visible.append(first_after)
    elif not visible and previous is not None and previous[0] <= view_end:
        visible.append(previous)
        if first_after is not None:
            visible.append(first_after)

    return visible


def draw_combined_series(
    canvas: tk.Canvas,
    series_list: list[PlotSeries],
    duration_seconds: float,
    playhead_seconds: float | None,
    view_start_seconds: float,
    view_window_seconds: float,
) -> None:
    canvas.delete("all")

    width = max(200, canvas.winfo_width())
    height = max(160, canvas.winfo_height())
    left = 56
    top = 18
    right = width - 16
    bottom = height - 32

    canvas.create_rectangle(left, top, right, bottom, outline="#4b5563")
    canvas.create_text(left, 6, text="Planner Overlay", anchor="nw", fill="#e5e7eb", font=("Segoe UI", 11, "bold"))
    canvas.create_text(8, (top + bottom) / 2, text="normalized", anchor="w", fill="#cbd5e1", angle=90, font=("Segoe UI", 9))

    if not any(series.points for series in series_list):
        canvas.create_text((left + right) / 2, (top + bottom) / 2, text="No data", fill="#94a3b8", font=("Segoe UI", 10))
        return

    block_size_series = next((series for series in series_list if series.y_label == "block-size" and series.points), None)
    if block_size_series is not None:
        for block_size in (MIN_BLOCK_SIZE, 512, 1024, 2048, 4096, 8192, MAX_BLOCK_SIZE):
            y = map_series_value_to_canvas_y(block_size_series, math.log2(block_size), top, bottom)
            canvas.create_line(left, y, right, y, fill="#1f2937")
            canvas.create_text(left - 8, y, text=str(block_size), anchor="e", fill=block_size_series.color, font=("Consolas", 8))
    else:
        for fraction in (0.0, 0.5, 1.0):
            y = bottom - ((bottom - top) * fraction)
            canvas.create_line(left, y, right, y, fill="#1f2937")
            canvas.create_text(left - 8, y, text=f"{fraction:0.1f}", anchor="e", fill="#94a3b8", font=("Consolas", 8))

    latest_point_time = max((series.points[-1][0] for series in series_list if series.points), default=0.0)
    total_duration = max(duration_seconds, latest_point_time, 1e-6)
    window_seconds = clamp(view_window_seconds, 0.25, total_duration)
    max_view_start = max(0.0, total_duration - window_seconds)
    view_start = clamp(view_start_seconds, 0.0, max_view_start)
    view_end = min(total_duration, view_start + window_seconds)
    for fraction in (0.0, 0.25, 0.5, 0.75, 1.0):
        x = left + ((right - left) * fraction)
        canvas.create_line(x, top, x, bottom, fill="#111827")
        canvas.create_text(x, bottom + 14, text=f"{view_start + (window_seconds * fraction):0.2f}s", anchor="n", fill="#94a3b8", font=("Consolas", 8))

    canvas.create_text(right - 8, bottom + 14, text=f"view {window_seconds:0.2f}s", anchor="ne", fill="#64748b", font=("Consolas", 8))

    legend_x = right - 190
    legend_y = top + 10
    for legend_index, series in enumerate(series_list):
        if not series.points:
            continue
        entry_y = legend_y + (legend_index * 18)
        canvas.create_line(legend_x, entry_y, legend_x + 20, entry_y, fill=series.color, width=3)
        canvas.create_text(legend_x + 28, entry_y, text=series.title, anchor="w", fill="#cbd5e1", font=("Segoe UI", 9))

        y_min, y_max = resolve_y_range(series)
        visible_points = collect_visible_points(series.points, view_start, view_end)
        drawable_points = reduce_points(visible_points, max(300, (right - left) // 2))
        flattened: list[float] = []
        previous_x: float | None = None
        previous_y: float | None = None
        for time_s, value in drawable_points:
            x = left + (((time_s - view_start) / max(window_seconds, 1e-9)) * (right - left))
            y_fraction = normalize_value(value, y_min, y_max)
            y_fraction = apply_scale_mode(y_fraction, series.scale_mode)
            y_fraction = apply_vertical_band(y_fraction, series.band_floor, series.band_ceiling)
            y = bottom - (y_fraction * (bottom - top))
            if series.draw_style == "step" and previous_x is not None and previous_y is not None:
                flattened.extend((x, previous_y, x, y))
            else:
                flattened.extend((x, y))
            previous_x = x
            previous_y = y

        if len(flattened) >= 4:
            canvas.create_line(*flattened, fill=series.color, width=2, smooth=False)

    if playhead_seconds is not None:
        clamped = max(0.0, min(total_duration, playhead_seconds))
        if view_start <= clamped <= view_end:
            playhead_x = left + (((clamped - view_start) / max(window_seconds, 1e-9)) * (right - left))
            canvas.create_line(playhead_x, top, playhead_x, bottom, fill="#f8fafc", width=2)
            canvas.create_text(playhead_x + 6, top + 10, text=f"{clamped:0.3f}s", anchor="nw", fill="#f8fafc", font=("Consolas", 8))


def draw_waveform_panel(
    canvas: tk.Canvas,
    title: str,
    samples: list[float],
    sample_rate: int,
    color: str,
    duration_seconds: float,
    playhead_seconds: float | None,
    view_start_seconds: float,
    view_window_seconds: float,
    vertical_gain: float = 1.0,
) -> None:
    canvas.delete("all")

    width = max(200, canvas.winfo_width())
    height = max(100, canvas.winfo_height())
    left = 56
    top = 10
    right = width - 16
    bottom = height - 18
    center_y = (top + bottom) / 2

    canvas.create_rectangle(left, top, right, bottom, outline="#334155")
    canvas.create_text(left, 0, text=title, anchor="nw", fill="#cbd5e1", font=("Segoe UI", 9, "bold"))
    canvas.create_line(left, center_y, right, center_y, fill="#1f2937")

    if not samples or sample_rate <= 0:
        canvas.create_text((left + right) / 2, center_y, text="No waveform data", fill="#64748b", font=("Segoe UI", 9))
        return

    total_duration = max(duration_seconds, len(samples) / max(sample_rate, 1), 1e-6)
    window_seconds = clamp(view_window_seconds, 0.25, total_duration)
    max_view_start = max(0.0, total_duration - window_seconds)
    view_start = clamp(view_start_seconds, 0.0, max_view_start)
    view_end = min(total_duration, view_start + window_seconds)

    visible_sample_start = clamp(int(view_start * sample_rate), 0, len(samples) - 1)
    visible_sample_end = clamp(int(math.ceil(view_end * sample_rate)), visible_sample_start + 1, len(samples))
    panel_width = max(1, right - left)
    visible_sample_count = max(1, visible_sample_end - visible_sample_start)
    samples_per_pixel = max(1, int(math.ceil(visible_sample_count / panel_width)))

    for pixel_index in range(panel_width):
        bucket_start = visible_sample_start + (pixel_index * samples_per_pixel)
        if bucket_start >= visible_sample_end:
            break
        bucket_end = min(visible_sample_end, bucket_start + samples_per_pixel)
        bucket = samples[bucket_start:bucket_end]
        if not bucket:
            continue

        min_value = min(bucket)
        max_value = max(bucket)
        scaled_max = clamp(max_value * vertical_gain, -1.0, 1.0)
        scaled_min = clamp(min_value * vertical_gain, -1.0, 1.0)
        y1 = center_y - (scaled_max * ((bottom - top) * 0.46))
        y2 = center_y - (scaled_min * ((bottom - top) * 0.46))
        x = left + pixel_index
        canvas.create_line(x, y1, x, y2, fill=color)

    if playhead_seconds is not None and view_start <= playhead_seconds <= view_end:
        playhead_x = left + (((playhead_seconds - view_start) / max(window_seconds, 1e-9)) * (right - left))
        canvas.create_line(playhead_x, top, playhead_x, bottom, fill="#f8fafc", width=2)


class PlannerVisualizerApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("PULSAR Planner Visualizer")
        self.root.geometry("1180x760")
        self.root.configure(bg="#0f172a")

        self.original_wav_path = tk.StringVar()
        self.run_wav_path = tk.StringVar()
        self.run_folder = tk.StringVar()
        self.status_text = tk.StringVar(value="Load a WAV and a planner run folder.")
        self.show_original_entropy = tk.BooleanVar(value=True)
        self.show_run_entropy = tk.BooleanVar(value=True)
        self._loading = False
        self._mci_alias = "pulsarviz"
        self._opened_playback_path: Path | None = None
        self._playhead_seconds = 0.0
        self._playback_poll_job: str | None = None
        self._is_playing = False
        self._playback_anchor_seconds = 0.0
        self._playback_anchor_clock = 0.0
        self._last_device_sync_clock = 0.0
        self._generated_playback_files: set[Path] = set()

        self.metrics = ComparisonMetrics()
        self.metric_duration = tk.StringVar(value="-")
        self.metric_snr = tk.StringVar(value="-")
        self.metric_rms_residual = tk.StringVar(value="-")
        self.metric_peak_residual = tk.StringVar(value="-")
        self.metric_audio_kbps = tk.StringVar(value="-")
        self.view_window_seconds = tk.DoubleVar(value=8.0)
        self.view_start_seconds = tk.DoubleVar(value=0.0)
        self.follow_playhead = tk.BooleanVar(value=True)
        self.playback_speed = tk.DoubleVar(value=1.0)
        self.playback_speed_text = tk.StringVar(value="1.00x")

        self.original_entropy_series = PlotSeries([], "Original Entropy", blend_hex("#22c55e", GRAPH_BACKGROUND, 0.5), "entropy", 0.0, 1.0, "exp", 0.18, 0.72)
        self.run_entropy_series = PlotSeries([], "Run Entropy", blend_hex("#38bdf8", GRAPH_BACKGROUND, 0.5), "entropy", 0.0, 1.0, "exp", 0.18, 0.72)
        self.block_series = PlotSeries([], "Block Size", blend_hex("#f97316", GRAPH_BACKGROUND, 0.5), "block-size", math.log2(MIN_BLOCK_SIZE), math.log2(MAX_BLOCK_SIZE), "linear", 0.06, 0.96, "step")
        self.duration_seconds = 1.0
        self.difference_waveform_samples: list[float] = []
        self.original_waveform_samples: list[float] = []
        self.run_waveform_samples: list[float] = []
        self.waveform_sample_rate = 0

        self.build_ui()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

    def build_ui(self) -> None:
        controls = ttk.Frame(self.root, padding=12)
        controls.pack(fill="x")

        ttk.Label(controls, text="Original WAV").grid(row=0, column=0, sticky="w")
        ttk.Entry(controls, textvariable=self.original_wav_path, width=110).grid(row=0, column=1, sticky="ew", padx=8)
        ttk.Button(controls, text="Browse", command=self.browse_original_wav).grid(row=0, column=2, padx=4)

        ttk.Label(controls, text="Run WAV").grid(row=1, column=0, sticky="w", pady=(8, 0))
        ttk.Entry(controls, textvariable=self.run_wav_path, width=110).grid(row=1, column=1, sticky="ew", padx=8, pady=(8, 0))
        ttk.Button(controls, text="Browse", command=self.browse_run_wav).grid(row=1, column=2, padx=4, pady=(8, 0))

        ttk.Label(controls, text="Run Folder").grid(row=2, column=0, sticky="w", pady=(8, 0))
        ttk.Entry(controls, textvariable=self.run_folder, width=110).grid(row=2, column=1, sticky="ew", padx=8, pady=(8, 0))
        ttk.Button(controls, text="Browse", command=self.browse_run_folder).grid(row=2, column=2, padx=4, pady=(8, 0))

        ttk.Checkbutton(controls, text="Original entropy", variable=self.show_original_entropy, command=self.redraw).grid(row=0, column=3, sticky="w", padx=(12, 0))
        ttk.Checkbutton(controls, text="Run entropy", variable=self.show_run_entropy, command=self.redraw).grid(row=1, column=3, sticky="w", padx=(12, 0))

        ttk.Button(controls, text="Load", command=self.load_data).grid(row=0, column=4, rowspan=3, padx=(12, 0), sticky="ns")
        ttk.Button(controls, text="Play", command=self.play_audio).grid(row=0, column=5, padx=(12, 0))
        ttk.Button(controls, text="Pause", command=self.pause_audio).grid(row=1, column=5, padx=(12, 0), pady=(8, 0))
        ttk.Button(controls, text="Stop", command=self.stop_audio).grid(row=2, column=5, padx=(12, 0), pady=(8, 0))

        controls.columnconfigure(1, weight=1)

        timeline_controls = ttk.Frame(self.root, padding=(12, 0, 12, 8))
        timeline_controls.pack(fill="x")
        ttk.Label(timeline_controls, text="View seconds").grid(row=0, column=0, sticky="w")
        self.window_scale = ttk.Scale(timeline_controls, from_=0.25, to=max(self.duration_seconds, 1.0), variable=self.view_window_seconds, command=self.on_window_scale)
        self.window_scale.grid(row=0, column=1, sticky="ew", padx=(8, 12))
        ttk.Label(timeline_controls, text="Start").grid(row=0, column=2, sticky="w")
        self.start_scale = ttk.Scale(timeline_controls, from_=0.0, to=max(0.0, self.duration_seconds - self.view_window_seconds.get()), variable=self.view_start_seconds, command=self.on_start_scale)
        self.start_scale.grid(row=0, column=3, sticky="ew", padx=(8, 12))
        ttk.Checkbutton(timeline_controls, text="Center playhead", variable=self.follow_playhead, command=self.redraw).grid(row=0, column=4, sticky="w")
        ttk.Button(timeline_controls, text="Fit full", command=self.fit_full_view).grid(row=0, column=5, padx=(12, 0))
        ttk.Label(timeline_controls, text="Playback speed").grid(row=1, column=0, sticky="w", pady=(8, 0))
        self.speed_scale = ttk.Scale(timeline_controls, from_=MIN_PLAYBACK_SPEED, to=MAX_PLAYBACK_SPEED, variable=self.playback_speed, command=self.on_playback_speed_scale)
        self.speed_scale.grid(row=1, column=1, columnspan=3, sticky="ew", padx=(8, 12), pady=(8, 0))
        ttk.Label(timeline_controls, textvariable=self.playback_speed_text).grid(row=1, column=4, sticky="w", pady=(8, 0))
        timeline_controls.columnconfigure(1, weight=1)
        timeline_controls.columnconfigure(3, weight=1)

        ttk.Label(self.root, textvariable=self.status_text, padding=(16, 0, 16, 8)).pack(anchor="w")

        self.graph_canvas = tk.Canvas(self.root, bg="#020617", highlightthickness=0, height=500)
        self.graph_canvas.pack(fill="both", expand=True, padx=12, pady=(0, 8))
        self.graph_canvas.bind("<Configure>", lambda _event: self.redraw())
        self.graph_canvas.bind("<Button-1>", self.on_graph_click)
        self.graph_canvas.bind("<MouseWheel>", self.on_graph_wheel)

        waveform_frame = ttk.Frame(self.root, padding=(12, 0, 12, 8))
        waveform_frame.pack(fill="x")
        self.difference_waveform_canvas = tk.Canvas(waveform_frame, bg="#020617", highlightthickness=0, height=95)
        self.difference_waveform_canvas.pack(fill="x", pady=(0, 6))
        self.difference_waveform_canvas.bind("<Configure>", lambda _event: self.redraw())
        self.original_waveform_canvas = tk.Canvas(waveform_frame, bg="#020617", highlightthickness=0, height=95)
        self.original_waveform_canvas.pack(fill="x", pady=(0, 6))
        self.original_waveform_canvas.bind("<Configure>", lambda _event: self.redraw())
        self.run_waveform_canvas = tk.Canvas(waveform_frame, bg="#020617", highlightthickness=0, height=95)
        self.run_waveform_canvas.pack(fill="x")
        self.run_waveform_canvas.bind("<Configure>", lambda _event: self.redraw())

        metrics_frame = ttk.Frame(self.root, padding=(12, 0, 12, 12))
        metrics_frame.pack(fill="x")
        ttk.Label(metrics_frame, text="Duration").grid(row=0, column=0, sticky="w")
        ttk.Label(metrics_frame, textvariable=self.metric_duration).grid(row=0, column=1, sticky="w", padx=(6, 18))
        ttk.Label(metrics_frame, text="SNR").grid(row=0, column=2, sticky="w")
        ttk.Label(metrics_frame, textvariable=self.metric_snr).grid(row=0, column=3, sticky="w", padx=(6, 18))
        ttk.Label(metrics_frame, text="RMS residual").grid(row=0, column=4, sticky="w")
        ttk.Label(metrics_frame, textvariable=self.metric_rms_residual).grid(row=0, column=5, sticky="w", padx=(6, 18))
        ttk.Label(metrics_frame, text="Peak residual").grid(row=0, column=6, sticky="w")
        ttk.Label(metrics_frame, textvariable=self.metric_peak_residual).grid(row=0, column=7, sticky="w", padx=(6, 18))
        ttk.Label(metrics_frame, text="Avg audio kbps").grid(row=0, column=8, sticky="w")
        ttk.Label(metrics_frame, textvariable=self.metric_audio_kbps).grid(row=0, column=9, sticky="w", padx=(6, 0))

    def browse_original_wav(self) -> None:
        selected = filedialog.askopenfilename(filetypes=[("WAV files", "*.wav")])
        if selected:
            self.original_wav_path.set(selected)

    def browse_run_wav(self) -> None:
        selected = filedialog.askopenfilename(filetypes=[("WAV files", "*.wav")])
        if selected:
            self.run_wav_path.set(selected)

    def browse_run_folder(self) -> None:
        selected = filedialog.askdirectory()
        if selected:
            self.run_folder.set(selected)

    def clamp_view_state(self) -> None:
        total_duration = max(self.duration_seconds, 0.25)
        clamped_window = clamp(self.view_window_seconds.get(), 0.25, total_duration)
        if abs(clamped_window - self.view_window_seconds.get()) > 1e-9:
            self.view_window_seconds.set(clamped_window)
        max_view_start = max(0.0, total_duration - clamped_window)
        clamped_start = clamp(self.view_start_seconds.get(), 0.0, max_view_start)
        if abs(clamped_start - self.view_start_seconds.get()) > 1e-9:
            self.view_start_seconds.set(clamped_start)
        self.window_scale.configure(to=total_duration)
        self.start_scale.configure(to=max_view_start)

    def fit_full_view(self) -> None:
        self.view_window_seconds.set(max(self.duration_seconds, 0.25))
        self.view_start_seconds.set(0.0)
        self.clamp_view_state()
        self.redraw()

    def on_window_scale(self, _value: str) -> None:
        center = self.view_start_seconds.get() + (self.view_window_seconds.get() * 0.5)
        self.clamp_view_state()
        new_window = self.view_window_seconds.get()
        self.view_start_seconds.set(clamp(center - (new_window * 0.5), 0.0, max(0.0, self.duration_seconds - new_window)))
        self.clamp_view_state()
        self.redraw()

    def on_start_scale(self, _value: str) -> None:
        self.clamp_view_state()
        self.redraw()

    def get_playback_speed(self) -> float:
        speed = clamp(self.playback_speed.get(), MIN_PLAYBACK_SPEED, MAX_PLAYBACK_SPEED)
        if abs(speed - self.playback_speed.get()) > 1e-9:
            self.playback_speed.set(speed)
        return speed

    def update_playback_speed_label(self) -> None:
        self.playback_speed_text.set(f"{self.get_playback_speed():0.2f}x")

    def logical_seconds_to_device_ms(self, logical_seconds: float) -> int:
        return int(max(0.0, logical_seconds / max(self.get_playback_speed(), 1e-9)) * 1000.0)

    def device_ms_to_logical_seconds(self, device_ms: int) -> float:
        return (device_ms / 1000.0) * self.get_playback_speed()

    def on_playback_speed_scale(self, _value: str) -> None:
        self.update_playback_speed_label()
        if self._opened_playback_path is None:
            return

        logical_playhead = self._playhead_seconds
        was_playing = self._is_playing
        try:
            self.close_playback()
            self._playhead_seconds = logical_playhead
            self.ensure_playback_open()
            device_ms = self.logical_seconds_to_device_ms(self._playhead_seconds)
            mci_send(f'seek {self._mci_alias} to {device_ms}')
            self._playback_anchor_seconds = self._playhead_seconds
            self._playback_anchor_clock = time.perf_counter()
            self._last_device_sync_clock = self._playback_anchor_clock
            if was_playing:
                mci_send(f'play {self._mci_alias} from {device_ms}')
                self._is_playing = True
                self.start_playback_poll()
            self.keep_playhead_visible()
            self.redraw()
        except Exception as exc:
            messagebox.showerror("Planner Visualizer", str(exc))

    def keep_playhead_visible(self) -> None:
        if not self.follow_playhead.get():
            return
        window = self.view_window_seconds.get()
        if window >= self.duration_seconds:
            self.view_start_seconds.set(0.0)
            return
        target_start = self._playhead_seconds - (window * 0.5)
        self.view_start_seconds.set(clamp(target_start, 0.0, max(0.0, self.duration_seconds - window)))
        self.clamp_view_state()

    def load_data(self) -> None:
        if self._loading:
            return

        try:
            original_wav = Path(self.original_wav_path.get())
            run_wav = Path(self.run_wav_path.get())
            run_folder = Path(self.run_folder.get())
            if not original_wav.exists():
                raise FileNotFoundError("Original WAV file not found.")
            if not run_wav.exists():
                raise FileNotFoundError("Run WAV file not found.")
            if not run_folder.exists():
                raise FileNotFoundError("Run folder not found.")
            self._loading = True
            self.status_text.set("Loading and analyzing... UI stays responsive.")
            self.root.configure(cursor="watch")

            def worker() -> None:
                try:
                    def report_progress(message: str) -> None:
                        self.root.after(0, lambda: self.status_text.set(message))

                    report_progress("Decoding original WAV...")
                    original_sample_rate, original_samples = decode_wave_file(original_wav)
                    report_progress("Decoding run WAV...")
                    run_sample_rate, run_samples = decode_wave_file(run_wav)

                    analysis_original_samples = original_samples
                    analysis_original_rate = original_sample_rate
                    if original_sample_rate != run_sample_rate:
                        report_progress(f"Resampling original WAV from {original_sample_rate} Hz to {run_sample_rate} Hz...")
                        analysis_original_samples = resample_mono(original_samples, original_sample_rate, run_sample_rate)
                        analysis_original_rate = run_sample_rate

                    report_progress("Computing original entropy...")
                    original_entropy = compute_entropy_series(analysis_original_samples, analysis_original_rate)
                    report_progress("Computing run entropy...")
                    run_entropy = compute_entropy_series(run_samples, run_sample_rate)
                    report_progress("Searching planner logs...")
                    block_activity, block_source = try_choose_block_series([
                        run_folder,
                        run_wav.parent,
                        original_wav.parent,
                    ])
                    report_progress("Computing comparison metrics...")
                    metrics = compute_comparison_metrics(analysis_original_samples, run_samples, run_sample_rate)
                    raw_difference_samples = [
                        analysis_original_samples[index] - run_samples[index]
                        for index in range(min(len(analysis_original_samples), len(run_samples)))
                    ]
                    report_progress("Band-limiting difference waveform (500 Hz - 18 kHz)...")
                    difference_samples = bandlimit_difference_waveform(raw_difference_samples, run_sample_rate)
                    summary_metrics = parse_summary_metrics(run_folder)
                    if summary_metrics.avg_audio_kbps is not None:
                        metrics.avg_audio_kbps = summary_metrics.avg_audio_kbps
                    if summary_metrics.snr_db is not None:
                        metrics.snr_db = summary_metrics.snr_db
                    if summary_metrics.rms_original is not None:
                        metrics.rms_original = summary_metrics.rms_original
                    if summary_metrics.rms_run is not None:
                        metrics.rms_run = summary_metrics.rms_run
                    if summary_metrics.rms_residual is not None:
                        metrics.rms_residual = summary_metrics.rms_residual
                    if summary_metrics.peak_residual is not None:
                        metrics.peak_residual = summary_metrics.peak_residual
                    duration_seconds = max(metrics.duration_seconds, summary_metrics.duration_seconds)

                    def apply_results() -> None:
                        self.duration_seconds = duration_seconds
                        self.original_entropy_series = PlotSeries(original_entropy, "Original Entropy", blend_hex("#22c55e", GRAPH_BACKGROUND, 0.5), "entropy", 0.0, 1.0, "exp", 0.18, 0.72)
                        self.run_entropy_series = PlotSeries(run_entropy, "Run Entropy", blend_hex("#38bdf8", GRAPH_BACKGROUND, 0.5), "entropy", 0.0, 1.0, "exp", 0.18, 0.72)
                        self.block_series = PlotSeries(block_activity, "Block Size", blend_hex("#f97316", GRAPH_BACKGROUND, 0.5), "block-size", math.log2(MIN_BLOCK_SIZE), math.log2(MAX_BLOCK_SIZE), "linear", 0.06, 0.96, "step")
                        self.difference_waveform_samples = difference_samples
                        self.original_waveform_samples = analysis_original_samples
                        self.run_waveform_samples = run_samples
                        self.waveform_sample_rate = run_sample_rate
                        self.metrics = metrics
                        self.metric_duration.set(f"{duration_seconds:0.2f}s")
                        self.metric_snr.set(f"{metrics.snr_db:0.2f} dB" if metrics.snr_db is not None else "-")
                        self.metric_rms_residual.set(f"{metrics.rms_residual:0.6f}" if metrics.rms_residual is not None else "-")
                        self.metric_peak_residual.set(f"{metrics.peak_residual:0.6f}" if metrics.peak_residual is not None else "-")
                        self.metric_audio_kbps.set(f"{metrics.avg_audio_kbps:0.2f}" if metrics.avg_audio_kbps is not None else "-")
                        self.close_playback()
                        self._playhead_seconds = 0.0
                        self.view_window_seconds.set(min(max(2.0, self.view_window_seconds.get()), self.duration_seconds))
                        self.view_start_seconds.set(0.0)
                        self.clamp_view_state()
                        if block_source is None:
                            block_status = "no planner CSV found for block sizes"
                        elif block_source.resolve() == run_folder.resolve():
                            block_status = f"block-size points: {len(block_activity)}"
                        else:
                            block_status = f"block-size points: {len(block_activity)} from {block_source.name}"

                        if original_sample_rate != run_sample_rate:
                            rate_status = f"original resampled {original_sample_rate} Hz -> {run_sample_rate} Hz"
                        else:
                            rate_status = f"sample rate: {run_sample_rate} Hz"

                        self.status_text.set(
                            f"Loaded {run_wav.name} | {rate_status} | original entropy: {len(original_entropy)} | run entropy: {len(run_entropy)} | {block_status} | duration: {self.duration_seconds:0.2f}s"
                        )
                        self._loading = False
                        self.root.configure(cursor="")
                        self.redraw()

                    self.root.after(0, apply_results)
                except Exception as exc:
                    error_message = str(exc)

                    def report_error() -> None:
                        self._loading = False
                        self.root.configure(cursor="")
                        self.status_text.set(f"Load failed: {error_message}")
                        messagebox.showerror("Planner Visualizer", error_message)

                    self.root.after(0, report_error)

            threading.Thread(target=worker, daemon=True).start()
        except Exception as exc:
            messagebox.showerror("Planner Visualizer", str(exc))

    def redraw(self) -> None:
        self.clamp_view_state()
        series_list: list[PlotSeries] = [self.block_series]
        if self.show_original_entropy.get():
            series_list.insert(0, self.original_entropy_series)
        if self.show_run_entropy.get():
            series_list.insert(0, self.run_entropy_series)
        draw_combined_series(
            self.graph_canvas,
            series_list,
            self.duration_seconds,
            self._playhead_seconds,
            self.view_start_seconds.get(),
            self.view_window_seconds.get(),
        )
        draw_waveform_panel(
            self.difference_waveform_canvas,
            "Difference Waveform",
            self.difference_waveform_samples,
            self.waveform_sample_rate,
            blend_hex("#f43f5e", GRAPH_BACKGROUND, 0.85),
            self.duration_seconds,
            self._playhead_seconds,
            self.view_start_seconds.get(),
            self.view_window_seconds.get(),
            6.0,
        )
        draw_waveform_panel(
            self.original_waveform_canvas,
            "Original Waveform",
            self.original_waveform_samples,
            self.waveform_sample_rate,
            blend_hex("#22c55e", GRAPH_BACKGROUND, 0.7),
            self.duration_seconds,
            self._playhead_seconds,
            self.view_start_seconds.get(),
            self.view_window_seconds.get(),
        )
        draw_waveform_panel(
            self.run_waveform_canvas,
            "Run Waveform",
            self.run_waveform_samples,
            self.waveform_sample_rate,
            blend_hex("#38bdf8", GRAPH_BACKGROUND, 0.7),
            self.duration_seconds,
            self._playhead_seconds,
            self.view_start_seconds.get(),
            self.view_window_seconds.get(),
        )

    def ensure_playback_open(self) -> None:
        wav_path = Path(self.run_wav_path.get())
        if not wav_path.exists():
            raise FileNotFoundError("Run WAV file not found.")

        playback_path = build_rate_adjusted_wav(wav_path, self.get_playback_speed())
        if playback_path != wav_path:
            self._generated_playback_files.add(playback_path)

        if self._opened_playback_path != playback_path:
            self.close_playback()
            escaped = str(playback_path).replace('"', '""')
            mci_send(f'open "{escaped}" type waveaudio alias {self._mci_alias}')
            mci_send(f'set {self._mci_alias} time format milliseconds')
            self._opened_playback_path = playback_path
            self._playhead_seconds = 0.0
            self._playback_anchor_seconds = 0.0
            self._playback_anchor_clock = time.perf_counter()
            self._last_device_sync_clock = 0.0

    def close_playback(self) -> None:
        self.cancel_playback_poll()
        try:
            mci_send(f'close {self._mci_alias}')
        except Exception:
            pass
        self._opened_playback_path = None
        self._is_playing = False
        self._playback_anchor_seconds = 0.0
        self._playback_anchor_clock = 0.0
        self._last_device_sync_clock = 0.0

    def cleanup_generated_playback_files(self) -> None:
        for path in list(self._generated_playback_files):
            try:
                if path.exists():
                    path.unlink()
            except Exception:
                pass
        self._generated_playback_files.clear()

    def play_audio(self) -> None:
        try:
            self.ensure_playback_open()
            start_ms = self.logical_seconds_to_device_ms(self._playhead_seconds)
            mci_send(f'play {self._mci_alias} from {start_ms}')
            self._is_playing = True
            self._playback_anchor_seconds = self._playhead_seconds
            self._playback_anchor_clock = time.perf_counter()
            self._last_device_sync_clock = 0.0
            self.start_playback_poll()
        except Exception as exc:
            messagebox.showerror("Planner Visualizer", str(exc))

    def pause_audio(self) -> None:
        try:
            self.ensure_playback_open()
            mci_send(f'pause {self._mci_alias}')
            self._is_playing = False
            self.update_playhead_from_device()
            self.cancel_playback_poll()
            self.redraw()
        except Exception as exc:
            messagebox.showerror("Planner Visualizer", str(exc))

    def stop_audio(self) -> None:
        try:
            self.ensure_playback_open()
            mci_send(f'stop {self._mci_alias}')
            mci_send(f'seek {self._mci_alias} to start')
            self._is_playing = False
            self._playhead_seconds = 0.0
            self._playback_anchor_seconds = 0.0
            self._playback_anchor_clock = time.perf_counter()
            self._last_device_sync_clock = 0.0
            self.cancel_playback_poll()
            self.redraw()
        except Exception as exc:
            messagebox.showerror("Planner Visualizer", str(exc))

    def start_playback_poll(self) -> None:
        self.cancel_playback_poll()
        self._playback_poll_job = self.root.after(PLAYHEAD_UPDATE_MS, self.poll_playback)

    def cancel_playback_poll(self) -> None:
        if self._playback_poll_job is not None:
            self.root.after_cancel(self._playback_poll_job)
            self._playback_poll_job = None

    def update_playhead_from_device(self) -> None:
        position_ms = int(mci_send(f'status {self._mci_alias} position') or '0')
        self._playhead_seconds = self.device_ms_to_logical_seconds(position_ms)
        self._playback_anchor_seconds = self._playhead_seconds
        self._playback_anchor_clock = time.perf_counter()
        self._last_device_sync_clock = self._playback_anchor_clock

    def poll_playback(self) -> None:
        self._playback_poll_job = None
        try:
            mode = mci_send(f'status {self._mci_alias} mode').strip().lower()
            now = time.perf_counter()
            if mode == 'playing':
                if self._playback_anchor_clock <= 0.0:
                    self._playback_anchor_clock = now
                estimated = self._playback_anchor_seconds + (max(0.0, now - self._playback_anchor_clock) * self.get_playback_speed())
                self._playhead_seconds = min(self.duration_seconds, estimated)
                if (now - self._last_device_sync_clock) * 1000.0 >= PLAYHEAD_DEVICE_SYNC_MS:
                    self.update_playhead_from_device()
            else:
                self.update_playhead_from_device()
            self.keep_playhead_visible()
            self.redraw()
            if mode == 'playing':
                self._is_playing = True
                self.start_playback_poll()
            else:
                self._is_playing = False
        except Exception:
            self._is_playing = False

    def on_graph_click(self, event: tk.Event[tk.Canvas]) -> None:
        if self.duration_seconds <= 0:
            return

        width = max(200, self.graph_canvas.winfo_width())
        left = 56
        right = width - 16
        if event.x < left or event.x > right:
            return

        view_start = self.view_start_seconds.get()
        view_window = self.view_window_seconds.get()
        fraction = (event.x - left) / max(1, right - left)
        self._playhead_seconds = max(0.0, min(self.duration_seconds, view_start + (view_window * fraction)))
        self._playback_anchor_seconds = self._playhead_seconds
        self._playback_anchor_clock = time.perf_counter()
        self._last_device_sync_clock = self._playback_anchor_clock
        try:
            self.ensure_playback_open()
            device_ms = self.logical_seconds_to_device_ms(self._playhead_seconds)
            mci_send(f'seek {self._mci_alias} to {device_ms}')
            if self._is_playing:
                mci_send(f'play {self._mci_alias} from {device_ms}')
                self.start_playback_poll()
        except Exception:
            pass
        self.keep_playhead_visible()
        self.redraw()

    def on_graph_wheel(self, event: tk.Event[tk.Canvas]) -> None:
        if self.duration_seconds <= 0:
            return

        width = max(200, self.graph_canvas.winfo_width())
        left = 56
        right = width - 16
        if event.x < left or event.x > right:
            return

        old_window = self.view_window_seconds.get()
        zoom_factor = 0.8 if event.delta > 0 else 1.25
        new_window = clamp(old_window * zoom_factor, 0.25, max(self.duration_seconds, 0.25))
        cursor_fraction = (event.x - left) / max(1, right - left)
        cursor_time = self.view_start_seconds.get() + (old_window * cursor_fraction)
        new_start = clamp(cursor_time - (new_window * cursor_fraction), 0.0, max(0.0, self.duration_seconds - new_window))
        self.view_window_seconds.set(new_window)
        self.view_start_seconds.set(new_start)
        self.clamp_view_state()
        self.redraw()

    def on_close(self) -> None:
        self.close_playback()
        self.cleanup_generated_playback_files()
        self.root.destroy()


def main() -> None:
    root = tk.Tk()
    style = ttk.Style(root)
    style.theme_use("clam")
    style.configure("TFrame", background="#0f172a")
    style.configure("TLabel", background="#0f172a", foreground="#e2e8f0", font=("Segoe UI", 10))
    style.configure("TButton", font=("Segoe UI", 10))
    style.configure("TEntry", fieldbackground="#e5e7eb")
    app = PlannerVisualizerApp(root)
    app.update_playback_speed_label()
    root.after(100, app.redraw)
    root.mainloop()


if __name__ == "__main__":
    main()