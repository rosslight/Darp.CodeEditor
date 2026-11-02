#!/usr/bin/env bash
# Lines of code per component: non-blank lines that are not only a comment. Measurement code is listed separately.
set -euo pipefail
cd "$(dirname "$0")/../.."
count() { cat "$@" | grep -v -E '^\s*$|^\s*(//|/\*|\*)' | wc -l; }
row() { printf '%-60s %6s\n' "$1" "$2"; }
row "A editor: src/Darp.CodeEditor (without Instrumentation)" "$(count $(find src/Darp.CodeEditor -name '*.cs' -not -path '*/Instrumentation/*' -not -path '*/obj/*'))"
row "B host: src/Darp.CodeEditor.Web (C#)" "$(count $(find src/Darp.CodeEditor.Web -name '*.cs' -not -path '*/obj/*'))"
row "B pages: web/src, Monaco only (monaco.ts, host.ts, luau.ts)" "$(count src/Darp.CodeEditor.Web/web/src/{monaco,host,luau}.ts)"
row "B pages: web/src, CodeMirror only (codemirror{,-lsp}.ts, host.ts, luau.ts)" "$(count src/Darp.CodeEditor.Web/web/src/{codemirror,codemirror-lsp,host,luau}.ts)"
row "B pages: build and markup (build.mjs, *.html, page.css, *.d.ts)" "$(count src/Darp.CodeEditor.Web/web/build.mjs src/Darp.CodeEditor.Web/web/src/*.html src/Darp.CodeEditor.Web/web/src/page.css src/Darp.CodeEditor.Web/web/src/*.d.ts)"
row "LSP server adapter: src/Darp.CodeEditor.Lsp" "$(count $(find src/Darp.CodeEditor.Lsp -name '*.cs' -not -path '*/obj/*'))"
row "Contract: src/Darp.CodeEditor.LanguageServices" "$(count $(find src/Darp.CodeEditor.LanguageServices -name '*.cs' -not -path '*/obj/*'))"
row "C# service: src/Darp.CodeEditor.CSharp" "$(count $(find src/Darp.CodeEditor.CSharp -name '*.cs' -not -path '*/obj/*'))"
row "measurement: A UiStallProbe" "$(count src/Darp.CodeEditor/Instrumentation/*.cs)"
row "measurement: B measure.ts and the two drivers" "$(count src/Darp.CodeEditor.Web/web/src/{measure,monaco-driver,codemirror-driver}.ts)"
