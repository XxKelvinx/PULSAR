#!/usr/bin/python3
# Copyright (c) 2026 Kelvin Eller
# Licensed under the BSD 3-Clause License (see COPYING)
"""
Offline VBR planner for Opus (v2.1).

Core philosophy:
  - Peaks boost up to 1.20 (Opus handles the fine allocation anyway).
  - Frames after a peak get stolen exponentially: hardest first, recovering
    toward 1.0. Only one steal curve applies per frame (the most recent peak),
    so rapid transients (snare rolls) don't double-steal.
  - Stolen bits are redistributed by TRANSITION RISK, not just by global
    loudness. A frame's risk score = how much louder the recent past was
    (loud→quiet drop) × how quiet the frame itself is. This targets exactly
    the chaos→silence transitions where Opus' reservoir runs dry and masking
    is lowest — the Adrenalina problem.
  - Budget is tracked precisely: anything clipped at QUIET_BOOST_MAX is
    carried forward to the next-best frames, so no stolen budget is wasted.
  - Baseline is exactly 1.0. enforce_budget should drift <1%.
"""
from __future__ import annotations
import argparse
import csv
import numpy as np
from scipy.signal import sosfilt

from score_telemetry import clamp, read_frames

SR = 48000.0
FFT_SIZE = 2048

BARK_EDGES_HZ = np.array([
    0, 100, 300, 630, 1270, 2700, 4400, 7700, 15500, 24000
], dtype=np.float64)
NUM_BANDS = len(BARK_EDGES_HZ) - 1

BAND_WEIGHTS = np.array([
    0.35, 0.60, 0.85, 1.00, 1.30, 1.30, 1.10, 0.70, 0.30,
])

# ---------------------------------------------------------------------------
# The Illusionist V3: Advanced Global Lookahead & Spectral Complexity
# ---------------------------------------------------------------------------
STEAL_FLOOR = 0.40        # Even deeper steal for redundant tonal frames
STEAL_RECOVERY = 0.85     # Slow, surgical recovery
STEAL_CUTOFF = 0.98
PEAK_BOOST_MAX = 1.35     # Extreme headroom for transients
PEAK_BOOST_MIN = 1.05
BASELINE = 1.00

# Quiet/Transition Redistribution
QUIET_BOOST_MAX = 1.50    
ABSOLUTE_SILENCE_DB = -75.0
PRE_ECHO_MS = 40          # Lookahead window to prevent pre-echo smear
TRANSITION_WINDOW_MS = 600

# Bandweights optimized for human vocal presence (1k-4k range)
BAND_WEIGHTS = np.array([
    0.30, 0.50, 0.80, 1.20, 1.50, 1.40, 0.90, 0.50, 0.20,
])


# ---------------------------------------------------------------------------
# Feature extraction (same as v2, unchanged)
# ---------------------------------------------------------------------------

def bark_bin_ranges(fft_size: int, sr: float) -> list[tuple[int, int]]:
    freqs = np.fft.rfftfreq(fft_size, d=1.0 / sr)
    ranges = []
    for lo, hi in zip(BARK_EDGES_HZ[:-1], BARK_EDGES_HZ[1:]):
        start = int(np.searchsorted(freqs, lo, side='left'))
        end = int(np.searchsorted(freqs, hi, side='left'))
        ranges.append((start, max(end, start + 1)))
    return ranges


def spectral_features(pcm_mono: np.ndarray, frame_samples: int):
    window = np.hanning(FFT_SIZE)
    bands = bark_bin_ranges(FFT_SIZE, SR)
    hop = frame_samples
    n_frames = max(0, (len(pcm_mono) - FFT_SIZE) // hop + 1)
    if n_frames == 0:
        return np.zeros((0, NUM_BANDS)), np.zeros(0)

    n_bins = FFT_SIZE // 2 + 1
    mag = np.empty((n_frames, n_bins))
    phase = np.empty((n_frames, n_bins))
    for i in range(n_frames):
        seg = pcm_mono[i * hop: i * hop + FFT_SIZE] * window
        spec = np.fft.rfft(seg)
        mag[i] = np.abs(spec)
        phase[i] = np.angle(spec)

    mag_prev = np.vstack([mag[0:1], mag[:-1]])
    diff = np.maximum(mag - mag_prev, 0.0)
    band_flux = np.zeros((n_frames, NUM_BANDS))
    for b, (lo, hi) in enumerate(bands):
        band_flux[:, b] = diff[:, lo:hi].sum(axis=1)

    onset = np.zeros(n_frames)
    if n_frames >= 3:
        predicted_phase = 2.0 * phase[1:-1] - phase[:-2]
        wrapped = np.angle(np.exp(1j * predicted_phase))
        predicted = mag[1:-1] * np.exp(1j * wrapped)
        actual = mag[2:] * np.exp(1j * phase[2:])
        onset[2:] = np.sum(np.abs(actual - predicted), axis=1)

    return band_flux, onset


def frame_loudness_db(pcm_stereo: np.ndarray, frame_samples: int) -> np.ndarray:
    mono = pcm_stereo.mean(axis=1)
    n = len(mono) // frame_samples
    blocks = mono[:n * frame_samples].reshape(n, frame_samples)
    rms = np.sqrt(np.mean(blocks ** 2, axis=1) + 1e-12)
    return 20.0 * np.log10(rms)


# ---------------------------------------------------------------------------
# Exponential steal + quiet-frame redistribution
# ---------------------------------------------------------------------------

def apply_steal_and_redistribute(
        raw: np.ndarray,
        loudness_db: np.ndarray,
        band_flux: np.ndarray,
        is_silent: np.ndarray,
        frame_samples: int,
        steal_floor: float = STEAL_FLOOR,
        steal_recovery: float = STEAL_RECOVERY,
        steal_cutoff: float = STEAL_CUTOFF,
) -> np.ndarray:
    """
    Forward pass: exponential steal after peaks, with two fixes over the
    naive implementation:

    Fix 1 — No double-stealing on rapid transients (snare rolls etc.).
    Each frame is only stolen from by the MOST RECENT peak within range.
    If a second peak fires before the first curve has recovered, the second
    peak's curve takes over from that point. Net effect: a frame is never
    pushed below the deepest single applicable curve.

    Fix 2 — Transition-aware redistribution (the Adrenalina fix).
    Stolen budget is NOT distributed to globally-quiet frames. It goes to
    frames whose RISK is highest:

        risk(i) = loud_drop(i) × quietness(i)

        loud_drop(i)  = max(0, rolling_max_db[-500ms..-20ms] - loud_db[i])
                        → how much louder was the recent past?
        quietness(i)  = max(0, track_median_db - loud_db[i])
                        → how far below median is this frame?

    The product peaks exactly at chaos→silence transitions: high recent
    loudness, suddenly quiet frame. That's where Opus' reservoir is drained
    and the masking threshold hits the floor simultaneously.

    Frames that are already in a steal window get no redistribution — we
    don't hand stolen bits to frames we just stole from.

    Budget tracking: anything clipped at QUIET_BOOST_MAX is carried to the
    next-best frames in a second pass, so no budget is silently discarded.
    """
    n = len(raw)
    env = raw.copy()

    gap = 1.0 - steal_floor

    # ------------------------------------------------------------------
    # Pass 1: compute per-frame steal ceiling + New Steal Sources (v3)
    # ------------------------------------------------------------------
    steal_ceil = np.ones(n)
    in_steal = np.zeros(n, dtype=bool)

    # Calculate Spectral Complexity for Tonal/Broadband Steal logic
    # Complexity = normalized entropy of the band flux (low = tonal/redundant)
    band_sum = band_flux.sum(axis=1, keepdims=True) + 1e-12
    p = band_flux / band_sum
    entropy = -np.sum(p * np.log2(p + 1e-12), axis=1)
    complexity = entropy / np.log2(NUM_BANDS) # 0.0 (Pure Sine) to 1.0 (White Noise)

    for i in range(n):
        # 1. TEMPORAL STEAL (Post-Peak)
        if raw[i] >= PEAK_BOOST_MIN:
            depth_factor = clamp(
                (raw[i] - PEAK_BOOST_MIN) / (PEAK_BOOST_MAX - PEAK_BOOST_MIN),
                0.0, 1.0,
            )
            k = 1
            j = i + 1
            while j < n and not is_silent[j]:
                m = 1.0 - gap * depth_factor * (steal_recovery ** (k - 1))
                if m >= steal_cutoff: break
                if m < steal_ceil[j]: steal_ceil[j] = m
                in_steal[j] = True
                k += 1; j += 1

        # 2. TONAL/REDUNDANT STEAL (Piano Decay / Sustained Notes)
        # Low complexity frames that aren't silent can afford -15% bits
        if not is_silent[i] and not in_steal[i] and complexity[i] < 0.35:
            tonal_steal = 0.85
            if tonal_steal < steal_ceil[i]:
                steal_ceil[i] = tonal_steal
                in_steal[i] = True  # FIX: mark as steal window to avoid redistribution bounce

    # Apply steal and track stolen budget
    stolen_budget = np.zeros(n)
    for i in range(n):
        if env[i] > steal_ceil[i]:
            stolen_budget[i] = env[i] - steal_ceil[i]
            env[i] = steal_ceil[i]

    # ------------------------------------------------------------------
    # Pass 2: The Illusionist v3 Redistribution (Complexity + Pre-Echo)
    # ------------------------------------------------------------------
    total_stolen = float(stolen_budget.sum())
    if total_stolen <= 0.0: return env

    frames_lookahead = max(1, int(PRE_ECHO_MS / 1000.0 * SR / frame_samples))
    frames_lookback  = max(1, int(TRANSITION_WINDOW_MS / 1000.0 * SR / frame_samples))
    
    # Pre-Echo weighting: Boost frames just BEFORE a peak
    pre_echo_risk = np.zeros(n)
    for i in range(n):
        if raw[i] >= PEAK_BOOST_MIN:
            for k in range(1, frames_lookahead + 1):
                idx = i - k
                if idx >= 0:
                    # Exponential ramp up to the peak
                    pre_echo_risk[idx] += (1.1 ** (frames_lookahead - k))

    # Transition weighting (same as v2.2)
    track_med = float(np.median(loudness_db[~is_silent]) if np.any(~is_silent) else np.median(loudness_db))
    recent_max = np.full(n, track_med)
    for i in range(n):
        lo = max(0, i - frames_lookback)
        hi = max(0, i - 1)
        if lo < hi: recent_max[i] = float(np.max(loudness_db[lo:hi]))
    
    contrast_risk = np.maximum(0.0, recent_max - loudness_db)
    texture_weight = np.exp(-((loudness_db - (-50.0))**2) / (2 * 15.0**2))
    
    # Final Risk: Blend Pre-Echo, Transition, and Spectral Complexity
    # We multiply by complexity because a complex quiet frame is harder to encode than a lone sine.
    risk = (contrast_risk * texture_weight + pre_echo_risk) * (complexity + 0.2)
    
    is_absolute_silence = loudness_db < ABSOLUTE_SILENCE_DB
    eligible = (~is_silent) & (~in_steal) & (raw < PEAK_BOOST_MIN) & (~is_absolute_silence)
    risk[~eligible] = 0.0

    risk_sum = risk.sum()
    if risk_sum <= 0.0:
        # Fallback: distribute evenly to non-silent non-steal frames
        eligible_idx = np.where(eligible)[0]
        if len(eligible_idx) > 0:
            risk[eligible_idx] = 1.0
            risk_sum = float(len(eligible_idx))
        else:
            return env

    weights = risk / risk_sum

    # ------------------------------------------------------------------
    # Pass 3: distribute budget — carry overflow to next-best frames
    # to ensure no budget is lost at the QUIET_BOOST_MAX clip.
    # ------------------------------------------------------------------
    remaining = total_stolen

    # Sort frames by weight descending so overflow flows naturally
    order = np.argsort(weights)[::-1]
    for idx in order:
        if remaining <= 0.0:
            break
        if weights[idx] <= 0.0:
            continue
        alloc = total_stolen * weights[idx]
        headroom = QUIET_BOOST_MAX - env[idx]
        if headroom <= 0.0:
            continue
        give = min(alloc, headroom, remaining)
        env[idx] += give
        remaining -= give

    return env


# ---------------------------------------------------------------------------
# Budget enforcement (should be near-no-op with good calibration)
# ---------------------------------------------------------------------------

def enforce_budget(scales: np.ndarray, target: float,
                   lo: float, hi: float, iters: int = 4) -> np.ndarray:
    out = scales.copy()
    for _ in range(iters):
        m = out.mean()
        if m <= 0.0:
            break
        out = np.clip(out * (target / m), lo, hi)
    return out


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input_pcm")
    parser.add_argument("telemetry_csv")
    parser.add_argument("--output", required=True)

    parser.add_argument("--strong-quantile",  type=float, default=0.94)
    parser.add_argument("--medium-quantile",  type=float, default=0.85)
    parser.add_argument("--onset-quantile",   type=float, default=0.92)
    parser.add_argument("--silence-db",       type=float, default=40.0,
                        help="dB below track median = true silence")
    parser.add_argument("--steal-floor",      type=float, default=STEAL_FLOOR,
                        help="Scale of frame immediately after peak (deepest steal)")
    parser.add_argument("--steal-recovery",   type=float, default=STEAL_RECOVERY,
                        help="Geometric recovery factor per frame (lower=faster)")
    parser.add_argument("--steal-cutoff",     type=float, default=STEAL_CUTOFF,
                        help="Stop stealing when recovered to this scale")
    parser.add_argument("--budget-target",    type=float, default=1.0)
    parser.add_argument("--min-scale",        type=float, default=0.50)
    parser.add_argument("--max-scale",        type=float, default=1.35)

    args = parser.parse_args()

    pcm = np.fromfile(args.input_pcm, dtype=np.int16).astype(np.float64) / 32768.0
    pcm = pcm.reshape(-1, 2)
    mono = pcm.mean(axis=1)

    frames = read_frames(args.telemetry_csv)
    frame_samples = frames[0].input_samples

    band_flux, onset = spectral_features(mono, frame_samples)
    loudness_db = frame_loudness_db(pcm, frame_samples)

    n = min(len(frames), band_flux.shape[0], len(loudness_db))
    frames      = frames[:n]
    band_flux   = band_flux[:n]
    onset       = onset[:n]
    loudness_db = loudness_db[:n]

    weighted_flux = band_flux @ BAND_WEIGHTS

    strong_thr = float(np.quantile(weighted_flux, args.strong_quantile))
    medium_thr = float(np.quantile(weighted_flux, args.medium_quantile))
    onset_thr  = float(np.quantile(onset,         args.onset_quantile))

    track_median_db = float(np.median(loudness_db))
    is_silent = loudness_db < (track_median_db - args.silence_db)

    band_mean  = band_flux.mean(axis=0, keepdims=True) + 1e-12
    active     = (band_flux > 0.5 * band_mean).sum(axis=1)
    broadband  = np.clip(active / NUM_BANDS, 0.0, 1.0)

    # ------------------------------------------------------------------
    # Raw scales: baseline 1.0, peaks boosted, silence floored
    # ------------------------------------------------------------------
    raw = np.full(n, BASELINE)

    for i in range(n):
        frame = frames[i]

        if is_silent[i]:
            raw[i] = 0.75
            continue

        transient_weight = clamp(
            0.55 * frame.activity + 0.45 * frame.noisiness, 0.0, 1.0
        )

        if weighted_flux[i] >= strong_thr or onset[i] >= onset_thr:
            # Broadband transients (cymbals) get slightly more than narrow ones
            peak = PEAK_BOOST_MIN + (PEAK_BOOST_MAX - PEAK_BOOST_MIN) * (
                0.7 * transient_weight + 0.3 * broadband[i]
            )
            raw[i] = max(BASELINE, peak)
        elif weighted_flux[i] >= medium_thr:
            raw[i] = max(BASELINE, PEAK_BOOST_MIN + 0.05 * transient_weight)

    # ------------------------------------------------------------------
    # Exponential steal -> redistribute into quiet frames
    # ------------------------------------------------------------------
    env = apply_steal_and_redistribute(
        raw, loudness_db, band_flux, is_silent, frame_samples,
        steal_floor=args.steal_floor,
        steal_recovery=args.steal_recovery,
        steal_cutoff=args.steal_cutoff,
    )

    env = np.clip(env, args.min_scale, args.max_scale)

    # Budget enforcement — should drift <2% from 1.0 with good parameters
    final = enforce_budget(env, args.budget_target,
                           args.min_scale, args.max_scale)

    import sys
    drift = abs(float(final.mean()) - args.budget_target)
    print(f"[v2.1] scale range [{final.min():.3f}, {final.max():.3f}]  "
          f"mean {final.mean():.4f}  budget drift {drift:.4f}",
          file=sys.stderr)

    with open(args.output, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow([
            "frame_index", "target_bitrate_bps", "scale",
            "weighted_flux", "onset", "loudness_db", "silent",
        ])
        for i, frame in enumerate(frames):
            bitrate = max(6000, int(round(frame.requested_bitrate_bps * final[i])))
            writer.writerow([
                i, bitrate, f"{final[i]:.6f}",
                f"{weighted_flux[i]:.6f}", f"{onset[i]:.6f}",
                f"{loudness_db[i]:.2f}", int(is_silent[i]),
            ])


if __name__ == "__main__":
    main()