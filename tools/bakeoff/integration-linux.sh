#!/usr/bin/env bash
# Integration checks of the WebView editors on X11, driven with xdotool: focus and Tab, shortcuts, clipboard, theme,
# resizing, popups, DPI and closing while loading. Writes screenshots and a log to $1 (default
# measurements/integration). Read the screenshots: most checks are observations, not assertions.
#
#   DISPLAY=:14 tools/bakeoff/integration-linux.sh [output dir]
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
out=$(realpath -m "${1:-$root/measurements/integration}")
mkdir -p "$out"
cd "$root"
b_aot=samples/Darp.CodeEditor.WebDemo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.WebDemo.Aot
b_jit=samples/Darp.CodeEditor.WebDemo/bin/Release/net10.0/Darp.CodeEditor.WebDemo
log="$out/integration.log"
: > "$log"

note() { echo "$*" | tee -a "$log"; }
shot() { ffmpeg -loglevel error -y -f x11grab -video_size "$(xdpyinfo | awk '/dimensions/ {print $2}')" -i "$DISPLAY" -frames:v 1 "$out/$1.png"; }
clipboard() { timeout 2 xclip -o -selection clipboard 2>/dev/null || echo "(empty)"; }

start() {
  "$@" > "$out/app.stdout" 2>&1 &
  app=$!
  win=$(xdotool search --sync --name "Code editor demo" | head -1)
  xdotool windowmove "$win" 0 0 windowsize "$win" 1200 800
  sleep 4
}
stop() { kill "$app" 2>/dev/null || true; wait "$app" 2>/dev/null || true; sleep 1; }
# Client coordinates of the demo window: app text box, theme button, tabs, and editor lines (both editors: line 3 is
# empty, line 1 ends before x 800).
at() { xdotool mousemove --window "$win" "$1" "$2"; }
click() { xdotool mousemove --window "$win" "$1" "$2" click 1; sleep 0.4; }
textbox=(140 26)
theme=(337 26)
tab_json=(630 26)
line1_end=(1000 65)
line3=(300 102)

for editor in monaco codemirror; do
  note "== $editor (NativeAOT host)"
  start "$b_aot" --editor "$editor"

  # Focus and Tab traversal.
  click "${textbox[@]}"; xdotool type --delay 30 "abc"; xdotool key Tab; sleep 0.3; xdotool type --delay 30 "T"; sleep 0.5
  shot "$editor-tab-from-textbox"
  click "${line3[@]}"; xdotool type --delay 30 "e"; xdotool key Tab; sleep 0.3; xdotool type --delay 30 "t"; sleep 0.5
  shot "$editor-tab-in-editor"
  xdotool key shift+Tab; sleep 0.3; xdotool type --delay 30 "s"; sleep 0.5
  shot "$editor-shift-tab-in-editor"
  # Keyboard focus vs pointer: focus the editor, then leave the pointer over the app's text box and type.
  click "${line3[@]}"; at "${textbox[@]}"; sleep 0.3; xdotool type --delay 30 "P"; sleep 0.5
  shot "$editor-type-with-pointer-outside"
  # Focus out: click the app text box after the editor and type.
  click "${line3[@]}"; click "${textbox[@]}"; xdotool type --delay 30 "O"; sleep 0.5
  shot "$editor-focus-out-to-textbox"

  # Shortcuts.
  click "${line3[@]}"; xdotool key ctrl+f; sleep 0.8
  shot "$editor-ctrl-f"
  xdotool key Escape; click "${line3[@]}"; xdotool key ctrl+s; sleep 0.5
  shot "$editor-ctrl-s-in-editor"
  click "${textbox[@]}"; xdotool key ctrl+s; sleep 0.5
  shot "$editor-ctrl-s-in-host"

  # Clipboard, both ways.
  click "${line1_end[@]}"; xdotool key Home shift+End ctrl+c; sleep 0.5
  note "  clipboard after Ctrl+C in the editor: $(clipboard)"
  click "${textbox[@]}"; xdotool key ctrl+a ctrl+v; sleep 0.5
  shot "$editor-clipboard-editor-to-host"
  printf 'FROM-HOST-CLIPBOARD' | xclip -selection clipboard; sleep 0.3
  click "${line3[@]}"; xdotool key ctrl+v; sleep 0.5
  shot "$editor-clipboard-host-to-editor"

  # Completion through LSP on the JSON tab (Ctrl+Space), with the window small so the list meets the edge.
  click "${tab_json[@]}"; sleep 2
  xdotool windowsize "$win" 700 330; sleep 1.5
  click 300 182; xdotool key End; xdotool type --delay 40 ' "f'; xdotool key ctrl+space; sleep 1.5
  shot "$editor-completion-at-bottom-edge"
  xdotool key Escape; xdotool windowsize "$win" 1200 800; sleep 1

  # Theme switch, then resizing.
  click "${theme[@]}"; sleep 1.5
  shot "$editor-theme-light"
  click "${theme[@]}"; sleep 1
  xdotool windowsize "$win" 760 420; sleep 1.5
  shot "$editor-resized-small"
  stop

  # Hover and signature help from Roslyn near the window's right and bottom edges (JIT host).
  start "$b_jit" --editor "$editor"
  sleep 4
  xdotool windowsize "$win" 900 520; sleep 1.5
  at 215 120; sleep 2.5
  shot "$editor-hover"
  click 700 445; xdotool key ctrl+End; xdotool type --delay 60 "Device.ReadAsync("; sleep 2
  shot "$editor-signature-help-at-bottom"
  stop

  # DPI: Avalonia scaled; the GTK web view only follows GDK_SCALE (integers).
  AVALONIA_GLOBAL_SCALE_FACTOR=1.5 start "$b_aot" --editor "$editor"
  shot "$editor-scale-1.5"
  stop
  AVALONIA_GLOBAL_SCALE_FACTOR=1.5 GDK_SCALE=2 GDK_DPI_SCALE=0.75 start "$b_aot" --editor "$editor"
  shot "$editor-scale-1.5-gdk"
  stop

  # Closing the window while the page is still loading.
  for ms in 50 150 300 600; do
    started=$(date +%s%N)
    set +e
    timeout 20 "$b_aot" --editor "$editor" --close-after "$ms" > "$out/close-$editor-$ms.stdout" 2>&1
    code=$?
    set -e
    sleep 2
    left=$(pgrep -c -x WebKitWebProces || true)
    note "  close after $ms ms: exit code $code, WebKit web processes 2 s after exit: $left"
  done
done
note "screenshots in $out"
