// CodeMirror 6: C# (legacy clike mode), Luau and JSON highlighting, search, completion, lint, and
// @codemirror/lsp-client talking to the host.

import {Compartment, EditorState, Extension} from "@codemirror/state"
import {
  EditorView, crosshairCursor, drawSelection, dropCursor, highlightActiveLine, highlightActiveLineGutter,
  highlightSpecialChars, keymap, lineNumbers, rectangularSelection,
} from "@codemirror/view"
import {defaultKeymap, history, historyKeymap} from "@codemirror/commands"
import {
  HighlightStyle, StreamLanguage, bracketMatching, foldGutter, foldKeymap, indentOnInput, syntaxHighlighting,
} from "@codemirror/language"
import {csharp} from "@codemirror/legacy-modes/mode/clike"
import {json} from "@codemirror/legacy-modes/mode/javascript"
import {highlightSelectionMatches, searchKeymap} from "@codemirror/search"
import {autocompletion, closeBrackets, closeBracketsKeymap, completionKeymap} from "@codemirror/autocomplete"
import {diagnosticCount, lintGutter, lintKeymap} from "@codemirror/lint"
import {LSPClient, hoverTooltips, serverDiagnostics, signatureHelp} from "@codemirror/lsp-client"
import {tags as t} from "@lezer/highlight"
import {config, forwardHostShortcuts, lsp, onCommand, postEvent, setPageTheme, Theme} from "./host"
import {installMeasurements, reportFirst, watchStartup} from "./measure"
import {codeMirrorDriver} from "./codemirror-driver"
import {lspCompletionSource, semanticTokens} from "./codemirror-lsp"
import {luauStreamParser} from "./luau"

// Dark+ and Light+ colors, the themes of the AvaloniaEdit editor and Monaco.
const palette: Record<Theme, Record<string, string>> = {
  dark: {background: "#1E1E1E", foreground: "#D4D4D4", gutter: "#858585", line: "#282828", selection: "#264F78",
    keyword: "#569CD6", control: "#C586C0", string: "#CE9178", comment: "#6A9955", number: "#B5CEA8", type: "#4EC9B0",
    function: "#DCDCAA", variable: "#9CDCFE", constant: "#4FC1FF", struct: "#86C691", interface: "#B8D7A3"},
  light: {background: "#FFFFFF", foreground: "#000000", gutter: "#237893", line: "#F3F3F3", selection: "#ADD6FF",
    keyword: "#0000FF", control: "#AF00DB", string: "#A31515", comment: "#008000", number: "#098658", type: "#267F99",
    function: "#795E26", variable: "#001080", constant: "#0070C1", struct: "#267F99", interface: "#267F99"},
}

function themeExtension(theme: Theme): Extension {
  const c = palette[theme]
  const semantic = (types: string[], color: string) => Object.fromEntries(types.map(type => [`.cm-sem-${type}`, {color}]))
  return [
    EditorView.theme({
      "&": {color: c.foreground, backgroundColor: c.background, height: "100%"},
      ".cm-scroller": {fontFamily: "Cascadia Code, JetBrains Mono, Consolas, Menlo, DejaVu Sans Mono, monospace", fontSize: "14px"},
      ".cm-gutters": {backgroundColor: c.background, color: c.gutter, border: "none"},
      ".cm-activeLine": {backgroundColor: c.line},
      ".cm-activeLineGutter": {backgroundColor: c.line},
      "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": {backgroundColor: c.selection},
      ".cm-cursor": {borderLeftColor: c.foreground},
      ...semantic(["class", "delegate"], c.type),
      ...semantic(["struct"], c.struct),
      ...semantic(["interface", "enum", "typeParameter"], c.interface),
      ...semantic(["method", "extensionMethod"], c.function),
      ...semantic(["property", "field", "event", "local", "parameter"], c.variable),
      ...semantic(["constant", "enumMember"], c.constant),
    }, {dark: theme == "dark"}),
    syntaxHighlighting(HighlightStyle.define([
      {tag: [t.keyword, t.bool, t.null, t.atom], color: c.keyword},
      {tag: [t.controlKeyword, t.moduleKeyword], color: c.control},
      {tag: [t.string, t.special(t.string)], color: c.string},
      {tag: [t.comment, t.lineComment, t.blockComment], color: c.comment},
      {tag: t.number, color: c.number},
      {tag: [t.typeName, t.className, t.standard(t.variableName)], color: c.type},
      {tag: [t.function(t.variableName), t.definition(t.variableName)], color: c.function},
      {tag: t.propertyName, color: c.variable},
      {tag: t.meta, color: c.gutter},
    ])),
  ]
}

function languageExtension(language: string): Extension {
  switch (language) {
    case "csharp": return StreamLanguage.define(csharp)
    case "luau": return StreamLanguage.define(luauStreamParser)
    case "json": return StreamLanguage.define(json)
    default: return []
  }
}

setPageTheme(config.theme)
forwardHostShortcuts(document.documentElement)

const themes = new Compartment()
const languageServer = new Compartment()

const view = new EditorView({
  parent: document.getElementById("editor")!,
  state: EditorState.create({
    doc: "",
    extensions: [
      lineNumbers(), highlightActiveLineGutter(), highlightSpecialChars(), history(), foldGutter(), drawSelection(),
      dropCursor(), EditorState.allowMultipleSelections.of(true), indentOnInput(), bracketMatching(), closeBrackets(),
      rectangularSelection(), crosshairCursor(), highlightActiveLine(), highlightSelectionMatches(), lintGutter(),
      // Ask the server right away; the default waits 100 ms after typing.
      autocompletion({override: [lspCompletionSource], activateOnTypingDelay: 0}),
      keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...foldKeymap,
        ...completionKeymap, ...lintKeymap]),
      languageExtension(config.language),
      themes.of(themeExtension(config.theme)),
      languageServer.of([]),
      EditorView.updateListener.of(update => {
        if (update.transactions.length && diagnosticCount(update.state) > 0) reportFirst("diagnosticsShown")
      }),
    ],
  }),
})

onCommand("open", command => {
  view.dispatch({changes: {from: 0, to: view.state.doc.length, insert: command.text}})
  if (config.lsp) {
    const client = new LSPClient({extensions: [hoverTooltips(), signatureHelp(), serverDiagnostics()], timeout: 10000}).connect(lsp)
    view.dispatch({effects: languageServer.reconfigure([client.plugin(config.uri, config.language), semanticTokens()])})
  }
})

onCommand("theme", command => {
  setPageTheme(command.theme)
  view.dispatch({effects: themes.reconfigure(themeExtension(command.theme))})
})

if (config.measure) {
  const driver = codeMirrorDriver(view)
  installMeasurements(driver)
  watchStartup(driver)
}
postEvent("ready", {at: performance.timeOrigin + performance.now()})
