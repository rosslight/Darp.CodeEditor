#!/usr/bin/env bash
# Measures both demos on Linux/X11: the in-process benchmarks (--bench), highlighted text on screen after launch, and
# keystroke to changed pixels (Probe latency). Results go to $1 (default measurements/linux).
#
#   DISPLAY=:14 tools/measure/run-linux.sh [output dir]
#
# Environment: RUNS (startup runs per demo, default 5), E2E_COUNT (keystrokes per latency run, default 60),
# SKIP_BUILD=1 to reuse existing builds, ONLY="bench startup e2e" to run a subset.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
out=$(realpath -m "${1:-$root/measurements/linux}")
runs=${RUNS:-5}
e2e_count=${E2E_COUNT:-60}
only=${ONLY:-"bench startup e2e"}
mkdir -p "$out"
cd "$root"

if [[ -z "${SKIP_BUILD:-}" ]]; then
  dotnet build CodeEditor.slnx -c Release -warnaserror > "$out/build.log"
  dotnet publish samples/Darp.CodeEditor.Demo.Aot -c Release -r linux-x64 > "$out/publish-aot.log"
fi

jit=samples/Darp.CodeEditor.Demo/bin/Release/net10.0/Darp.CodeEditor.Demo
aot=samples/Darp.CodeEditor.Demo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Demo.Aot
probe=(dotnet tools/Darp.CodeEditor.Probe/bin/Release/net10.0/Darp.CodeEditor.Probe.dll)
demos=("jit|$jit" "aot|$aot")

uptime > "$out/load.txt"

if [[ $only == *bench* ]]; then
  for demo in "${demos[@]}"; do
    IFS='|' read -r name exe <<< "$demo"
    "$exe" --bench --out "$out/bench-$name.txt" > "$out/bench-$name.stdout" 2>&1 || echo "bench $name failed"
  done
fi

if [[ $only == *startup* ]]; then
  for demo in "${demos[@]}"; do
    IFS='|' read -r name exe <<< "$demo"
    for ((run = 1; run <= runs; run++)); do
      "${probe[@]}" startup -- "$exe" >> "$out/startup-onscreen-$name.txt" 2>&1 || echo "startup $name run $run failed"
    done
  done
fi

if [[ $only == *e2e* ]]; then
  # A C# document without language features; the probe clicks behind line 1 and types there.
  for demo in "${demos[@]}"; do
    IFS='|' read -r name exe <<< "$demo"
    target="$out/e2e-$name.json"
    rm -f "$target"
    "$exe" --e2e "$target" > "$target.stdout" 2>&1 &
    pid=$!
    for ((i = 0; i < 600; i++)); do
      [[ -s $target ]] && break
      sleep 0.1
    done
    sleep 3
    "${probe[@]}" latency --target "$target" --count "$e2e_count" --out "$out/e2e-$name.txt" || echo "e2e $name failed"
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    sleep 1
  done
fi

uptime >> "$out/load.txt"
echo "results in $out"
