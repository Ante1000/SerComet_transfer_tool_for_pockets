#!/usr/bin/env python3
"""Makes the tape recording of the installer for the CE-1600P (PC-1600 MODE 0) from the output of bin2wav:
8 bit, 48 kHz, full-scale rectangle  ->  16 bit, 48 kHz, 65 % amplitude, the edges lightly rounded (the zero crossings stay where they are),
1 s of silence before and 1 s after the signal.
Usage: python3 tools/make_wav_mode0.py in.wav out.wav        (in.wav: bin2wav -t img -p 1600 -s 6 -nSL1600 SL1600.img)"""
import sys, wave, array
src, dst = sys.argv[1:3]
w = wave.open(src); sr = w.getframerate(); assert w.getsampwidth() == 1 and w.getnchannels() == 1
a = [x - 128 for x in w.readframes(w.getnframes())]
n = len(a)
pk = max(abs(x) for x in a) or 1
amp = 0.65 * 32767 / pk
out = array.array('h')
out.extend([0] * sr)
for i in range(n):
    p = a[i - 1] if i else a[i]; q = a[i + 1] if i + 1 < n else a[i]
    out.append(int(round((0.25 * p + 0.5 * a[i] + 0.25 * q) * amp)))
out.extend([0] * sr)
o = wave.open(dst, 'wb'); o.setnchannels(1); o.setsampwidth(2); o.setframerate(sr); o.writeframes(out.tobytes()); o.close()
print(dst, len(out) / sr, 's')
