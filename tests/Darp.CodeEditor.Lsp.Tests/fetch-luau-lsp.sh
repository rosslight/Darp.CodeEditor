#!/usr/bin/env bash
# Downloads the luau-lsp release that the interop tests run against to .tools/luau-lsp, which git ignores.
# The tests skip the interop checks when the binary is missing.
set -euo pipefail

version=1.70.1
sha256=1a2ea1ae4f98f8946cefd970a4b54853e11ab4775e6932faa7f60cf920346567
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
target="$root/.tools/luau-lsp"

download="$(mktemp -d)"
trap 'rm -rf "$download"' EXIT
curl -fsSL -o "$download/luau-lsp.zip" \
  "https://github.com/JohnnyMorganz/luau-lsp/releases/download/$version/luau-lsp-linux-x86_64.zip"
echo "$sha256  $download/luau-lsp.zip" | sha256sum --check --quiet

rm -rf "$target"
mkdir -p "$target"
unzip -q "$download/luau-lsp.zip" -d "$target"
chmod +x "$target/luau-lsp"
echo "$target/luau-lsp"
