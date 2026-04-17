import wave
import struct
import math
import os
base = r'C:\Users\eller\Desktop\PULSAR'
a = wave.open(os.path.join(base, 'TestWAVs\\\\Output\\\\Old\\\\spira-vbr9.wav'), 'rb')
b = wave.open(os.path.join(base, 'TestWAVs\\\\Output\\ut\\\\strike-vbr9decoded.wav'), 'rb')
assert a.getnchannels() == b.getnchannels() == 2
assert a.getsampwidth() == b.getsampwidth() == 4
assert a.getframerate() == b.getframerate() == 44100
assert a.getnframes() == b.getnframes()
n = a.getnframes()
rawa = a.readframes(n)
rawb = b.readframes(n)
fmt = '<' + str(n*2) + 'f'
sa = struct.unpack(fmt, rawa)
sb = struct.unpack(fmt, rawb)
ssq = sum((x-y)**2 for x, y in zip(sa, sb))
ss = sum(x*x for x in sa)
rms = math.sqrt(ssq / n)
rmsA = math.sqrt(ss / n)
print('RMSdiff', rms)
print('RMSorig', rmsA)
print('SNR', 20 * math.log10(rmsA / (rms + 1e-18)))
