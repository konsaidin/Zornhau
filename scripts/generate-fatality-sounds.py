#!/usr/bin/env python3
"""Create original mono gore Foley: thud, crunch, damp tear and wet tail.

Only generated waveforms are encoded; no game or third-party recordings are
copied. The runtime optionally layers the base game's referenced squish sound.
Requires ffmpeg with libvorbis. Committed Ogg assets need no build-time encoder.
"""
import math
from pathlib import Path
import random
import struct
import subprocess
import tempfile
import wave

ROOT = Path(__file__).resolve().parents[1]
RATE = 44100


def synthesize(family, variant):
    rng = random.Random(70900 + variant + ('sever', 'crush', 'land').index(family) * 101)
    length = {'sever': .49, 'crush': .44, 'land': .27}[family]
    samples = [0.] * int(length * RATE)
    low, mid, phase = 0., 0., 0.
    crunches = sorted((rng.uniform(.009, .16), rng.uniform(.9, 1.6), rng.uniform(450, 1600))
                      for _ in range(13 if family == 'crush' else 7 if family == 'sever' else 3))
    bubbles = [(rng.uniform(.06, length - .06), rng.uniform(350, 900), rng.uniform(.12, .3)) for _ in range(8)]
    for i in range(len(samples)):
        t = i / RATE
        noise = rng.uniform(-1, 1)
        low += .09 * (noise - low)
        mid += .32 * (noise - mid)
        phase += 2 * math.pi * (68 + 75 * math.exp(-t * 38)) / RATE
        attack = min(1, t / .0018)
        thud = math.sin(phase) * math.exp(-t * (21 if family != 'land' else 28)) * .58
        tear = (mid - low) * math.exp(-t * (12 if family == 'sever' else 22)) * 1.5
        wet = low * math.exp(-t * 9) * (1 + .7 * math.sin(2 * math.pi * 43 * t))
        value = attack * (thud + tear + wet)
        for start, amplitude, frequency in crunches:
            age = t - start
            if 0 <= age < .035:
                value += (noise * .65 + math.sin(2 * math.pi * frequency * age) * .22) * amplitude * math.exp(-age * 230)
        for start, frequency, amplitude in bubbles:
            age = t - start
            if 0 <= age < .06:
                value += amplitude * math.sin(2 * math.pi * (frequency * age - 1300 * age * age)) * math.exp(-age * 80)
        # Soft saturation keeps the transient chunky without clipped PCM.
        samples[i] = math.tanh(value * 1.4) * min(1, (length - t) / .025)
    peak = max(abs(value) for value in samples)
    # Vorbis transients can overshoot the source peak; leave ample headroom.
    return struct.pack('<' + 'h' * len(samples), *(round(value / peak * 18000) for value in samples))


def main():
    destination = ROOT / 'assets/zornhau/sounds/fatalities'
    destination.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='zornhau-gore-audio-') as scratch:
        for family in ('sever', 'crush', 'land'):
            for variant in (1, 2):
                source = Path(scratch) / f'{family}{variant}.wav'
                with wave.open(str(source), 'wb') as output:
                    output.setparams((1, 2, RATE, 0, 'NONE', 'not compressed'))
                    output.writeframes(synthesize(family, variant))
                target = destination / f'{family}{variant}.ogg'
                subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-i', str(source),
                    '-map_metadata', '-1', '-c:a', 'libvorbis', '-q:a', '5', str(target)], check=True)
                print('Generated:', target.relative_to(ROOT))


if __name__ == '__main__':
    main()
