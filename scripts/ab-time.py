#!/usr/bin/env python3
"""A/B timing for comparing builds on a noisy host: runs each labelled command N times, alternating the order between
rounds, and prints the median, minimum and maximum elapsed seconds per label. Output of the commands is discarded.

    scripts/ab-time.py 10 'before=dotnet old/mutex.dll' 'after=dotnet new/mutex.dll' 'native=./mutex-native'
"""
import statistics
import subprocess
import sys
import time

if len(sys.argv) < 3 or not sys.argv[1].isdigit():
    sys.exit(__doc__)
rounds = int(sys.argv[1])
pairs = [argument.split("=", 1) for argument in sys.argv[2:]]
times = {label: [] for label, _ in pairs}
for round_index in range(rounds):
    for label, command in pairs if round_index % 2 == 0 else reversed(pairs):
        start = time.perf_counter()
        subprocess.run(command, shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        times[label].append(time.perf_counter() - start)
for label, values in times.items():
    print(f"{label:24s} median {statistics.median(values):.3f}  min {min(values):.3f}  max {max(values):.3f}  (n={len(values)})")
