// Monaco 0.57 (ESM): editor features, C#, Luau and JSON tokenizers, and Monaco's own LSP client talking to the host.

import * as monaco from "monaco-editor/editor"
import "monaco-editor/features/register.all"
import "monaco-editor/languages/definitions/csharp/register"
import {conf as luaConf, language as luaLanguage} from "monaco-editor/languages/definitions/lua/lua"
// Monaco ships its LSP client (the `lsp` namespace of the full build) outside the modules its package exports.
import {MonacoLspClient} from "../node_modules/monaco-editor/esm/external/monaco-lsp-client/out/index.js"
import {config, forwardHostShortcuts, lsp, onCommand, postEvent, setPageTheme, Theme} from "./host"
import {installMeasurements, reportFirst, watchStartup} from "./measure"
import {monacoDriver} from "./monaco-driver"
import {luauMonarch} from "./luau"

;(self as any).MonacoEnvironment = {
  getWorker(_id: string, label: string) {
    const worker = new Worker("editor.worker.js", {name: label})
    // Proof that workers run from the app origin: the first message from the worker.
    worker.addEventListener("message", () => postEvent("worker", {label}), {once: true})
    worker.addEventListener("error", e => postEvent("error", {message: `worker ${label}: ${e.message}`}))
    return worker
  },
}

monaco.languages.register({id: "luau", extensions: [".luau"], aliases: ["Luau"]})
monaco.languages.setLanguageConfiguration("luau", luaConf)
monaco.languages.setMonarchTokensProvider("luau", luauMonarch(luaLanguage))
monaco.languages.register({id: "json", extensions: [".json"]})
monaco.languages.setMonarchTokensProvider("json", {
  tokenizer: {
    root: [
      [/"(?:[^"\\]|\\.)*"(?=\s*:)/, "type"],
      [/"(?:[^"\\]|\\.)*"/, "string"],
      [/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/, "number"],
      [/\b(?:true|false|null)\b/, "keyword"],
      [/[{}[\],:]/, "delimiter"],
    ],
  },
})

// Semantic token colors from the language server, the same colors the AvaloniaEdit editor uses.
const semanticColors: Record<Theme, Record<string, string>> = {
  dark: {class: "4EC9B0", delegate: "4EC9B0", struct: "86C691", interface: "B8D7A3", enum: "B8D7A3", typeParameter: "B8D7A3",
    method: "DCDCAA", extensionMethod: "DCDCAA", property: "9CDCFE", field: "9CDCFE", event: "9CDCFE", local: "9CDCFE",
    parameter: "9CDCFE", constant: "4FC1FF", enumMember: "4FC1FF"},
  light: {class: "267F99", delegate: "267F99", struct: "267F99", interface: "267F99", enum: "267F99", typeParameter: "267F99",
    method: "795E26", extensionMethod: "795E26", property: "001080", field: "001080", event: "001080", local: "001080",
    parameter: "001080", constant: "0070C1", enumMember: "0070C1"},
}
for (const theme of ["dark", "light"] as Theme[]) {
  monaco.editor.defineTheme(`darp-${theme}`, {
    base: theme == "dark" ? "vs-dark" : "vs",
    inherit: true,
    rules: Object.entries(semanticColors[theme]).map(([token, foreground]) => ({token, foreground})),
    colors: {},
  })
}

setPageTheme(config.theme)
forwardHostShortcuts(document.documentElement)

const container = document.getElementById("editor")!
const model = monaco.editor.createModel("", config.language, monaco.Uri.parse(config.uri))
// Created before the document arrives, so building the editor overlaps the host round trip.
const editor = monaco.editor.create(container, {
  model,
  theme: `darp-${config.theme}`,
  automaticLayout: true,
  fontFamily: "Cascadia Code, JetBrains Mono, Consolas, Menlo, DejaVu Sans Mono, monospace",
  fontSize: 14,
  minimap: {enabled: false},
  "semanticHighlighting.enabled": true,
  // Language features come from the server only; no delay before asking it.
  wordBasedSuggestions: "off",
  quickSuggestionsDelay: 0,
  // With inline suggestions on, Monaco holds typed-word completion requests back by about 75 ms; nothing here
  // provides inline completions.
  inlineSuggest: {enabled: false},
  quickSuggestions: {other: "on", comments: "off", strings: "off"},
  // Hovers and the suggest list may extend past the editor, up to the edge of the web view.
  fixedOverflowWidgets: true,
  // Paste-as reads the clipboard through the asynchronous Clipboard API, which WebKitGTK denies without a permission
  // handler: pasting text copied in another application did nothing. The browser's own paste works.
  pasteAs: {enabled: false},
})

monaco.editor.onDidChangeMarkers(uris => {
  if (uris.some(uri => uri.toString() == model.uri.toString()) && monaco.editor.getModelMarkers({resource: model.uri}).length)
    reportFirst("diagnosticsShown")
})

onCommand("open", command => {
  model.setValue(command.text)
  if (config.lsp) connectLanguageServer()
})

onCommand("theme", command => {
  setPageTheme(command.theme)
  monaco.editor.setTheme(`darp-${command.theme}`)
})

function connectLanguageServer() {
  let listener: ((message: any) => void) | undefined
  lsp.subscribe(text => listener?.(JSON.parse(text)))
  // Monaco's client takes a json-rpc message transport; the host connection is always open.
  new MonacoLspClient({
    state: {value: {state: "open"}, onChange: () => ({dispose() {}})},
    async send(message: unknown) { lsp.send(JSON.stringify(message)) },
    setListener(l: ((message: any) => void) | undefined) { listener = l },
    toString: () => "avalonia-host",
  } as any)
}

if (config.measure) {
  const driver = monacoDriver(editor, model, container)
  installMeasurements(driver)
  watchStartup(driver)
}
postEvent("ready", {at: performance.timeOrigin + performance.now()})
