#!/usr/bin/env bash
# Runs the editor bake-off on Linux: A (AvaloniaEdit) and B (WebView with Monaco and CodeMirror), one after the other
# on the same X display, with the same scenarios. Results go to $1 (default measurements/bakeoff).
#
#   DISPLAY=:14 tools/bakeoff/run-linux.sh [output dir]
#
# Environment: RUNS (startup runs per app, default 5), E2E_COUNT (keystrokes per e2e run, default 60),
# SKIP_BUILD=1 to reuse existing builds, ONLY="startup bench memory e2e sizes" to run a subset, APPS="b-" to run only
# the apps whose name contains that text.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
out=$(realpath -m "${1:-$root/measurements/bakeoff}")
runs=${RUNS:-5}
e2e_count=${E2E_COUNT:-60}
only=${ONLY:-"startup bench memory e2e sizes"}
mkdir -p "$out"
cd "$root"

if [[ -z "${SKIP_BUILD:-}" ]]; then
  dotnet build CodeEditor.slnx -c Release -warnaserror > "$out/build.log"
  dotnet publish samples/Darp.CodeEditor.Demo.Aot -c Release -r linux-x64 > "$out/publish-a-aot.log"
  dotnet publish samples/Darp.CodeEditor.WebDemo.Aot -c Release -r linux-x64 > "$out/publish-b-aot.log"
fi

a_jit=samples/Darp.CodeEditor.Demo/bin/Release/net10.0/Darp.CodeEditor.Demo
a_aot=samples/Darp.CodeEditor.Demo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Demo.Aot
b_jit=samples/Darp.CodeEditor.WebDemo/bin/Release/net10.0/Darp.CodeEditor.WebDemo
b_aot=samples/Darp.CodeEditor.WebDemo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.WebDemo.Aot
probe=(dotnet tools/Darp.CodeEditor.Probe/bin/Release/net10.0/Darp.CodeEditor.Probe.dll)

# name, executable, extra arguments
apps=(
  "a-jit|$a_jit|"
  "a-aot|$a_aot|"
  "b-jit-monaco|$b_jit|--editor monaco"
  "b-jit-codemirror|$b_jit|--editor codemirror"
  "b-aot-monaco|$b_aot|--editor monaco"
  "b-aot-codemirror|$b_aot|--editor codemirror"
)
selected=()
for app in "${apps[@]}"; do [[ ${app%%|*} == *${APPS:-}* ]] && selected+=("$app"); done
apps=("${selected[@]}")

# Starts an app in the background, waits for a file it writes, then runs a command with its pid and stops it.
with_app() {
  local wait_for=$1 timeout=$2; shift 2
  local exe=$1; shift
  local -a app_args=()
  while [[ $# -gt 0 && $1 != -- ]]; do app_args+=("$1"); shift; done
  shift
  rm -f "$wait_for"
  "$exe" "${app_args[@]}" > "$wait_for.stdout" 2>&1 &
  local pid=$!
  for ((i = 0; i < timeout * 10; i++)); do
    [[ -s $wait_for ]] && break
    sleep 0.1
  done
  sleep 3
  "$@" "$pid" || true
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  sleep 1
}

memory() { "${probe[@]}" memory --pid "$2" --out "$1"; }

if [[ $only == *startup* ]]; then
  for app in "${apps[@]}"; do
    IFS='|' read -r name exe extra <<< "$app"
    for ((run = 1; run <= runs; run++)); do
      # shellcheck disable=SC2086
      "$exe" --smoke $extra --out "$out/startup-$name-$run.txt" > /dev/null 2>&1 || echo "startup $name run $run failed"
      # The same start, seen from outside: when highlighted text is on screen.
      # shellcheck disable=SC2086
      "${probe[@]}" startup -- "$exe" $extra >> "$out/startup-onscreen-$name.txt" 2>&1 || echo "on-screen startup $name run $run failed"
    done
  done
fi

if [[ $only == *bench* ]]; then
  for app in "${apps[@]}"; do
    IFS='|' read -r name exe extra <<< "$app"
    # shellcheck disable=SC2086
    "$exe" --bench $extra --out "$out/bench-$name.txt" > "$out/bench-$name.stdout" 2>&1 || echo "bench $name failed"
  done
fi

if [[ $only == *memory* ]]; then
  for app in "${apps[@]}"; do
    IFS='|' read -r name exe extra <<< "$app"
    # shellcheck disable=SC2086
    with_app "$out/memory-$name-idle.report" 60 "$exe" --smoke --hold $extra --out "$out/memory-$name-idle.report" -- memory "$out/memory-$name-idle.txt"
    # shellcheck disable=SC2086
    with_app "$out/memory-$name-bench.report" 600 "$exe" --bench --hold $extra --out "$out/memory-$name-bench.report" -- memory "$out/memory-$name-bench.txt"
  done
fi

if [[ $only == *e2e* ]]; then
  # A C# document without language features in each AOT demo; the probe clicks behind line 1 and types there.
  latency() { "${probe[@]}" latency --target "$1" --count "$e2e_count" --out "${1%.json}.txt"; }
  for app in "a-aot|$a_aot|" "b-aot-monaco|$b_aot|--editor monaco" "b-aot-codemirror|$b_aot|--editor codemirror"; do
    IFS='|' read -r name exe extra <<< "$app"
    # shellcheck disable=SC2086
    with_app "$out/e2e-$name.json" 60 "$exe" $extra --e2e "$out/e2e-$name.json" -- bash -c "$(declare -f latency); probe=(${probe[*]}); e2e_count=$e2e_count; latency $out/e2e-$name.json" _
  done
  # The WebView with WebKit's compositing switched off (CPU rendering instead of OpenGL on llvmpipe).
  for editor in monaco codemirror; do
    WEBKIT_DISABLE_COMPOSITING_MODE=1 with_app "$out/e2e-b-aot-$editor-nocompositing.json" 60 "$b_aot" --editor "$editor" \
      --e2e "$out/e2e-b-aot-$editor-nocompositing.json" -- bash -c "$(declare -f latency); probe=(${probe[*]}); e2e_count=$e2e_count; latency $out/e2e-b-aot-$editor-nocompositing.json" _
  done
fi

if [[ $only == *sizes* ]]; then
  {
    for dir in "$(dirname "$a_aot")" "$(dirname "$b_aot")"; do
      echo "$dir (without .dbg and .pdb):"
      find "$dir" -type f ! -name '*.dbg' ! -name '*.pdb' -printf '%s %P\n' | sort -rn
      find "$dir" -type f ! -name '*.dbg' ! -name '*.pdb' -printf '%s\n' | awk '{s += $1} END {printf "total %.1f MB\n\n", s / 1e6}'
    done
    echo "System packages the WebView needs on Linux (installed size):"
    dpkg-query -W -f='${Package} ${Version} ${Installed-Size} KiB\n' 'libwebkit2gtk-4.1-0' 'libjavascriptcoregtk-4.1-0' 'libgtk-3-0t64' 'libsoup-3.0-0' 2>/dev/null || true
  } > "$out/sizes.txt"
  grep -h -c -i ' warning ' "$out"/publish-*.log > "$out/aot-warnings.txt" 2>/dev/null || true
fi

echo "results in $out"
