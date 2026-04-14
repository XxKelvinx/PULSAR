import math
import os
import struct
import wave
from pathlib import Path

SOURCE_FOLDER = Path('Artifacts/Test Tracks/RAW Tracks')
TARGET_FOLDER = Path('Artifacts/Test Tracks/RAW Tracks 44k1')
TARGET_RATE = 44100
WINDOW_HALF_WIDTH = 32


def sinc(x: float) -> float:
    if x == 0.0:
        return 1.0
    return math.sin(math.pi * x) / (math.pi * x)


def window(x: float, half_width: int) -> float:
    # Blackman window over [-half_width, half_width]
    if abs(x) > half_width:
        return 0.0
    a0 = 0.42
    a1 = 0.5
    a2 = 0.08
    ratio = x / half_width
    return a0 - a1 * math.cos(math.pi * (ratio + 1.0) / 2.0) + a2 * math.cos(2.0 * math.pi * (ratio + 1.0) / 2.0)


def resample_channel(samples: list[float], source_rate: int, target_rate: int) -> list[float]:
    if source_rate == target_rate:
        return samples.copy()

    ratio = target_rate / source_rate
    output_length = max(1, int(math.floor(len(samples) * ratio)))
    cutoff = 0.5 * min(1.0, ratio)
    half_width = WINDOW_HALF_WIDTH
    output = [0.0] * output_length
    source_length = len(samples)

    for out_index in range(output_length):
        position = out_index / ratio
        left = int(math.floor(position))
        accum = 0.0
        total_weight = 0.0

        start = left - half_width + 1
        end = left + half_width

        for i in range(start, end + 1):
            if i < 0 or i >= source_length:
                sample = samples[0] if i < 0 else samples[-1]
            else:
                sample = samples[i]
            weight = sinc((position - i) * cutoff) * window(position - i, half_width)
            accum += sample * weight
            total_weight += weight

        output[out_index] = accum / total_weight if total_weight != 0.0 else 0.0

    return output


def read_pcm16_stereo(path: Path) -> tuple[list[float], list[float], int]:
    with wave.open(str(path), 'rb') as wav:
        channels = wav.getnchannels()
        rate = wav.getframerate()
        sampwidth = wav.getsampwidth()
        if sampwidth != 2:
            raise RuntimeError(f'Unsupported sample width: {sampwidth} bytes')
        if channels != 2:
            raise RuntimeError(f'Unsupported channel count: {channels}')
        frames = wav.readframes(wav.getnframes())

    values = struct.unpack('<' + 'h' * (len(frames) // 2), frames)
    left = [sample / 32768.0 for sample in values[0::2]]
    right = [sample / 32768.0 for sample in values[1::2]]
    return left, right, rate


def write_pcm16_stereo(path: Path, left: list[float], right: list[float], rate: int) -> None:
    num_frames = min(len(left), len(right))
    with wave.open(str(path), 'wb') as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.setnframes(num_frames)
        buffer = bytearray()
        for i in range(num_frames):
            l = max(-1.0, min(1.0, left[i]))
            r = max(-1.0, min(1.0, right[i]))
            buffer += struct.pack('<hh', int(round(l * 32767.0)), int(round(r * 32767.0)))
        wav.writeframes(buffer)


def downsample_file(source_path: Path, target_path: Path) -> None:
    left, right, source_rate = read_pcm16_stereo(source_path)
    if source_rate == TARGET_RATE:
        write_pcm16_stereo(target_path, left, right, TARGET_RATE)
        return

    left_out = resample_channel(left, source_rate, TARGET_RATE)
    right_out = resample_channel(right, source_rate, TARGET_RATE)
    write_pcm16_stereo(target_path, left_out, right_out, TARGET_RATE)


def main() -> None:
    TARGET_FOLDER.mkdir(parents=True, exist_ok=True)
    wav_files = sorted([p for p in SOURCE_FOLDER.iterdir() if p.suffix.lower() == '.wav'])
    if not wav_files:
        raise RuntimeError('No WAV files found in source folder')

    for source_path in wav_files:
        target_path = TARGET_FOLDER / source_path.name
        print(f'Downsampling {source_path.name} -> {target_path}')
        downsample_file(source_path, target_path)
    print('Downsampling complete.')


if __name__ == '__main__':
    main()
