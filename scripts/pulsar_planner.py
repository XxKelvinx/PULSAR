#!/usr/bin/python3
# Copyright (c) 2026 Kelvin Eller
# Licensed under the BSD 3-Clause License (see COPYING)
"""Create an asymmetric, psychoacoustically optimized offline VBR plan."""
from __future__ import annotations
import argparse
import csv
import statistics
import numpy as np
from scipy.signal import butter, filtfilt

from score_telemetry import clamp, read_frames

def renormalize(scales: list[float]) -> list[float]:
    mean_scale = statistics.fmean(scales) if scales else 1.0
    if mean_scale <= 0.0: return scales
    return [scale / mean_scale for scale in scales]

def compute_flux(pcm_path: str, frame_samples: int, highpass_hz: float) -> np.ndarray:
    pcm = np.fromfile(pcm_path, dtype=np.int16).astype(np.float64)
    pcm = pcm.reshape(-1, 2) / 32768.0
    mono = pcm.mean(axis=1)
    
    # 1. Frequency Splitting: High-Pass Filter the detector!
    # This prevents sub-bass from falsely triggering transients, ensuring bits
    # are boosted only for hi-hats, snares, and rhythm clicks.
    if highpass_hz > 0:
        nyq = 48000.0 / 2.0
        cutoff = highpass_hz / nyq
        b, a = butter(4, cutoff, btype='high')
        mono = filtfilt(b, a, mono)

    fft_size = 2048
    hop = frame_samples
    window = np.hanning(fft_size)
    spectra = []
    for start in range(0, len(mono) - fft_size, hop):
        frame = mono[start : start + fft_size]
        spectra.append(np.abs(np.fft.rfft(frame * window)))
    
    flux = [0.0]
    for prev, curr in zip(spectra, spectra[1:]):
        flux.append(float(np.sum(np.maximum(curr - prev, 0.0))))
        
    return np.array(flux, dtype=np.float64)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input_pcm")
    parser.add_argument("telemetry_csv")
    parser.add_argument("--output")
    
    # Core Scale Args
    parser.add_argument("--strong-peak", type=float, default=1.45) # We can boost harder now since bass is ignored
    parser.add_argument("--medium-peak", type=float, default=1.25)
    parser.add_argument("--base-scale", type=float, default=0.88)
    parser.add_argument("--min-scale", type=float, default=0.80)
    parser.add_argument("--max-scale", type=float, default=1.50)
    
    # Psychoacoustic Args
    parser.add_argument("--highpass-hz", type=float, default=150.0, help="Cutoff for transient detection")
    parser.add_argument("--pre-echo-decay", type=float, default=0.85, help="Multiplier going backward (Shield)")
    parser.add_argument("--post-mask-decay", type=float, default=0.30, help="Multiplier going forward (Steal)")
    
    args = parser.parse_args()

    frames = read_frames(args.telemetry_csv)
    flux = compute_flux(args.input_pcm, frames[0].input_samples, args.highpass_hz)
    
    total_frames = min(len(frames), len(flux))
    frames = frames[:total_frames]
    flux = flux[:total_frames]

    strong_threshold = float(np.quantile(flux, 0.94))
    medium_threshold = float(np.quantile(flux, 0.88))

    raw_scales = np.zeros(total_frames, dtype=np.float64)

    for index, flux_value in enumerate(flux):
        frame = frames[index]
        
        # We give back bits to tonal bass / noise dynamically
        # calm_and_tonal represents continuous pads, drones, background ambiance
        is_calm_and_tonal = clamp(0.7 * frame.tonality + 0.3 * (1.0 - frame.noisiness), 0.0, 1.0)
        frame_base = args.base_scale + (1.0 - args.base_scale) * is_calm_and_tonal
        
        raw_scales[index] = frame_base

        transient_weight = clamp(0.55 * frame.activity + 0.45 * frame.noisiness, 0.0, 1.0)
        
        if flux_value >= strong_threshold:
            raw_scales[index] = max(raw_scales[index], 1.0 + (args.strong_peak - 1.0) * transient_weight)
        elif flux_value >= medium_threshold:
            raw_scales[index] = max(raw_scales[index], 1.0 + (args.medium_peak - 1.0) * transient_weight)

    # ====================================================================
    # Asymmetric Psychoacoustic Smoothing (Forward/Backward Pass)
    # ====================================================================
    
    envelope = raw_scales.copy()
    stolen = set()
    
    # 1. Post-masking Steal (Forward Pass):
    # Rip the bits away immediately AFTER the peak. The ear is "deaf" temporarily.
    # If peak is at frame 10, frame 11 drops to 30%, saving massive amounts of bits!
    for i in range(1, total_frames):
        # Steal ONLY if the current frame is not a peak itself!
        if envelope[i-1] > 1.0 and envelope[i] <= 1.0:
            envelope[i] = min(envelope[i], envelope[i-1] * args.post_mask_decay)
            stolen.add(i)
        elif i-1 in stolen and envelope[i] <= 1.0:
            candidate = envelope[i-1] * args.post_mask_decay
            if candidate < envelope[i]:
                envelope[i] = candidate
                stolen.add(i)

    # 2. Pre-echo Shield (Backward Pass):
    # Ramp the bit allocation UP before the peak actually hits.
    # If a peak is at frame 10, frame 9 gets 85% of it, frame 8 gets 72%, etc.
    for i in range(total_frames - 2, -1, -1):
        if i not in stolen:
            envelope[i] = max(envelope[i], envelope[i+1] * args.pre_echo_decay)
        
    # Clip top to max_scale, but let Steal valleys go down to 0.1 (absolute floor)
    # args.min_scale is no longer blocking our deep Psycho-Valleys!
    bounded = np.clip(envelope, 0.10, args.max_scale)
    normalized = renormalize(bounded.tolist())

    with open(args.output, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["frame_index", "target_bitrate_bps", "scale", "flux"])
        for frame_index, (frame, scale_val, flux_value) in enumerate(zip(frames, normalized, flux)):
            bitrate = max(6000, int(round(frame.requested_bitrate_bps * scale_val)))
            writer.writerow([frame_index, bitrate, f"{scale_val:.6f}", f"{flux_value:.6f}"])

if __name__ == "__main__":
    main()
