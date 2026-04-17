import numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly, stft
from math import gcd

sr1, d1 = wavfile.read(r'Artifacts\Test Tracks\RAW Tracks\Spira 32s.wav')
sr2, d2 = wavfile.read(r'Artifacts\Output\Packer\spira-tf-v1-v2-20260417-052116\spira-tf-v1-v2-decoded.wav')
sr3, d3 = wavfile.read(r'Artifacts\Output\Packer\spira-trim-v2-20260417-050548\spira-trim-v2-decoded.wav')

if d1.dtype == np.int16: d1 = d1.astype(np.float32) / 32768.0

ch1 = d1[:, 0] if d1.ndim > 1 else d1
ch2 = d2[:, 0] if d2.ndim > 1 else d2
ch3 = d3[:, 0] if d3.ndim > 1 else d3

g = gcd(44100, sr1)
ch1_r = resample_poly(ch1, 44100 // g, sr1 // g)

n = min(len(ch1_r), len(ch2), len(ch3))

diff_new = ch1_r[:n] - ch2[:n]
diff_old = ch1_r[:n] - ch3[:n]

f, t, Zd_new = stft(diff_new, fs=44100, nperseg=2048, noverlap=1536)
f, t, Zd_old = stft(diff_old, fs=44100, nperseg=2048, noverlap=1536)
f, t, Zo = stft(ch1_r[:n], fs=44100, nperseg=2048, noverlap=1536)

rd_new = np.sqrt(np.mean(np.abs(Zd_new)**2, axis=1))
rd_old = np.sqrt(np.mean(np.abs(Zd_old)**2, axis=1))
ro = np.sqrt(np.mean(np.abs(Zo)**2, axis=1))

print(f"{'Range':>14s} {'NoTF SNR':>10s} {'TF SNR':>10s} {'Delta':>8s}")
print("-" * 50)
for lo, hi in [(0,2000),(2000,4000),(4000,8000),(8000,12000),(12000,16000),(16000,19000)]:
    mask = (f >= lo) & (f < hi)
    if mask.any():
        sig = np.sqrt(np.mean(ro[mask]**2))
        n_old = np.sqrt(np.mean(rd_old[mask]**2))
        n_new = np.sqrt(np.mean(rd_new[mask]**2))
        snr_old = 20*np.log10(sig/n_old) if n_old > 0 else float('inf')
        snr_new = 20*np.log10(sig/n_new) if n_new > 0 else float('inf')
        delta = snr_new - snr_old
        print(f"{lo:5d}-{hi:5d} Hz {snr_old:8.1f} dB {snr_new:8.1f} dB {delta:+6.1f} dB")
