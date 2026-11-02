#!/usr/bin/env bash
# Keyboard and clipboard checks with results read back from the editor (Ctrl+A, Ctrl+C), so they are assertions:
# typing while the pointer is outside the web view, Tab inside the editor, and pasting text copied by another app.
#
#   DISPLAY=:14 tools/bakeoff/keyboard-linux.sh [output file]
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
out=$(realpath -m "${1:-$root/measurements/integration/keyboard.txt}")
mkdir -p "$(dirname "$out")"
cd "$root"
b_aot=samples/Darp.CodeEditor.WebDemo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.WebDemo.Aot
: > "$out"

readback() {
  xdotool mousemove --window "$win" 600 300 click 1; sleep 0.3
  xdotool key ctrl+a ctrl+c; sleep 0.5
  timeout 2 xclip -o -selection clipboard 2>/dev/null | head -1 || true
}

for editor in ${EDITORS:-monaco codemirror}; do
  # shellcheck disable=SC2086
  "$b_aot" --editor "$editor" ${EXTRA_ARGS:-} > /dev/null 2>&1 &
  app=$!
  win=$(xdotool search --sync --name "Code editor demo" | head -1)
  xdotool windowmove "$win" 0 0 windowsize "$win" 1200 800
  sleep 4
  # Line 1 of the Luau document, behind its end.
  xdotool mousemove --window "$win" 1000 65 click 1; sleep 0.3
  xdotool type --delay 40 "A1"
  xdotool mousemove --window "$win" 140 26; sleep 0.3
  xdotool type --delay 40 "OUT"
  xdotool mousemove --window "$win" 1000 65; sleep 0.3
  xdotool type --delay 40 "B2"
  xdotool key Tab; xdotool type --delay 40 "C3"
  printf 'HOSTCLIP' | xclip -selection clipboard; sleep 0.3
  xdotool mousemove --window "$win" 1000 65 click 1; sleep 0.3
  xdotool key End ctrl+v; sleep 0.5
  line=$(readback)
  # Back to the host: click the app's text box, type, and copy what it holds.
  xdotool mousemove --window "$win" 140 26 click 1; sleep 0.3
  xdotool key ctrl+a BackSpace; xdotool type --delay 40 "HOSTKEYS"; xdotool key ctrl+a ctrl+c; sleep 0.5
  host=$(timeout 2 xclip -o -selection clipboard 2>/dev/null | head -1 || true)
  {
    [[ $host == HOSTKEYS ]] && echo "$editor: typing into the app's text box after the editor had focus: yes" || echo "$editor: typing into the app's text box after the editor had focus: NO (got '$host')"
    echo "$editor: line 1 after the sequence: $line"
    [[ $line == *A1* ]] && echo "  typing into the editor: yes" || echo "  typing into the editor: NO"
    [[ $line == *OUT* ]] && echo "  typing with the pointer over the app's text box reached the editor: yes" || echo "  typing with the pointer over the app's text box reached the editor: NO (keys lost)"
    [[ $line == *B2* ]] && echo "  typing after the pointer came back: yes" || echo "  typing after the pointer came back: NO"
    [[ $line == *$'\t'C3* || $line == *"  C3"* ]] && echo "  Tab inserted indentation (focus stays in the editor)" || { [[ $line == *C3* ]] && echo "  Tab: C3 typed without indentation" || echo "  Tab moved focus out of the editor (C3 not typed into it)"; }
    [[ $line == *HOSTCLIP* ]] && echo "  paste of text copied by another app: yes" || echo "  paste of text copied by another app: NO"
  } | tee -a "$out"
  kill "$app"; wait "$app" 2>/dev/null || true; sleep 1
done
