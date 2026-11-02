// Two LSP features @codemirror/lsp-client 6.3 does not have: completion with lazily resolved descriptions
// (completionItem/resolve), and semantic tokens for the visible range.

import type * as lsp from "vscode-languageserver-protocol"
import {RangeSetBuilder, Extension} from "@codemirror/state"
import {Decoration, DecorationSet, EditorView, ViewPlugin, ViewUpdate} from "@codemirror/view"
import {Completion, CompletionContext, CompletionResult, CompletionSource} from "@codemirror/autocomplete"
import {LSPPlugin} from "@codemirror/lsp-client"
import {reportFirst} from "./measure"

const kindToType: Record<number, string> = {
  1: "text", 2: "method", 3: "function", 4: "class", 5: "property", 6: "variable", 7: "class", 8: "interface",
  9: "namespace", 10: "property", 13: "enum", 14: "keyword", 15: "text", 20: "constant", 21: "constant", 22: "class",
  23: "property", 25: "type",
}

/**
 * Asks the server once per word, like the AvaloniaEdit editor: the list is valid while the user keeps typing word
 * characters, and descriptions load when an item is selected.
 */
export const lspCompletionSource: CompletionSource = async (context: CompletionContext): Promise<CompletionResult | null> => {
  const plugin = context.view && LSPPlugin.get(context.view)
  if (!plugin || !plugin.client.serverCapabilities?.completionProvider) return null
  const before = context.state.sliceDoc(context.pos - 1, context.pos)
  const triggers = plugin.client.serverCapabilities.completionProvider.triggerCharacters ?? []
  const word = context.matchBefore(/[\w]+$/)
  const isTrigger = triggers.includes(before)
  if (!context.explicit && !isTrigger && !(word && word.to - word.from == 1)) return null
  plugin.client.sync()
  const params: lsp.CompletionParams = {
    textDocument: {uri: plugin.uri},
    position: plugin.toPosition(context.pos),
    context: isTrigger ? {triggerKind: 2, triggerCharacter: before} : {triggerKind: 1},
  }
  context.addEventListener("abort", () => plugin.client.cancelRequest(params))
  let result: lsp.CompletionList | lsp.CompletionItem[] | null
  try {
    result = await plugin.client.request<lsp.CompletionParams, lsp.CompletionList | lsp.CompletionItem[] | null>("textDocument/completion", params)
  } catch (e: any) {
    if (e?.code == -32800) return null
    throw e
  }
  if (!result) return null
  const list: lsp.CompletionList = Array.isArray(result) ? {isIncomplete: false, items: result} : result
  const line = context.state.doc.lineAt(context.pos)
  const range = list.itemDefaults?.editRange
  const editRange = range && "start" in range ? range : undefined
  const from = editRange ? line.from + editRange.start.character : word ? word.from : context.pos
  return {
    from,
    to: context.pos,
    validFor: /^\w*$/,
    commitCharacters: list.itemDefaults?.commitCharacters,
    options: list.items.map((item): Completion => {
      const text = item.textEditText ?? (item.textEdit && "newText" in item.textEdit ? item.textEdit.newText : undefined) ?? item.label
      return {
        label: item.filterText ?? item.label,
        displayLabel: item.label,
        apply: text,
        type: item.kind ? kindToType[item.kind] : undefined,
        detail: item.detail,
        sortText: item.sortText,
        info: () => resolveDescription(plugin, item),
      }
    }),
  }
}

async function resolveDescription(plugin: LSPPlugin, item: lsp.CompletionItem): Promise<Node | null> {
  const resolved = await plugin.client.request<lsp.CompletionItem, lsp.CompletionItem>("completionItem/resolve", item)
  if (!resolved.documentation) return null
  const element = document.createElement("div")
  element.className = "cm-lsp-documentation cm-lsp-completion-documentation"
  element.innerHTML = plugin.docToHTML(resolved.documentation)
  return element
}

/** Semantic tokens for the viewport, requested when typing pauses; decorations move with the text meanwhile. */
export function semanticTokens(): Extension {
  return ViewPlugin.fromClass(class {
    decorations: DecorationSet = Decoration.none
    private timer = -1
    private request = 0

    constructor(private view: EditorView) {
      this.schedule(0)
    }

    update(update: ViewUpdate) {
      if (update.docChanged) this.decorations = this.decorations.map(update.changes)
      if (update.docChanged || update.viewportChanged) this.schedule(250)
    }

    destroy() {
      clearTimeout(this.timer)
    }

    private schedule(delay: number) {
      clearTimeout(this.timer)
      this.timer = setTimeout(() => this.fetch(), delay)
    }

    private async fetch() {
      const plugin = LSPPlugin.get(this.view)
      const legend = plugin?.client.serverCapabilities?.semanticTokensProvider?.legend
      if (!plugin || !legend) {
        // Not initialized yet: try again shortly.
        if (plugin) this.schedule(100)
        return
      }
      const request = ++this.request
      plugin.client.sync()
      const doc = plugin.syncedDoc
      const {from, to} = this.view.viewport
      const tokens = await plugin.client.request<lsp.SemanticTokensRangeParams, lsp.SemanticTokens | null>(
        "textDocument/semanticTokens/range",
        {textDocument: {uri: plugin.uri}, range: {start: plugin.toPosition(from, doc), end: plugin.toPosition(Math.min(to, doc.length), doc)}},
      ).catch(() => null)
      if (!tokens || request != this.request) return
      const builder = new RangeSetBuilder<Decoration>()
      let line = 0, character = 0
      for (let i = 0; i < tokens.data.length; i += 5) {
        line += tokens.data[i]
        character = tokens.data[i] ? tokens.data[i + 1] : character + tokens.data[i + 1]
        const start = doc.line(line + 1).from + character
        builder.add(start, start + tokens.data[i + 2], Decoration.mark({class: `cm-sem-${legend.tokenTypes[tokens.data[i + 3]]}`}))
      }
      // Map from the synced document to the current one.
      this.decorations = builder.finish().map(plugin.unsyncedChanges)
      this.view.dispatch({})
      reportFirst("semanticShown")
    }
  }, {decorations: plugin => plugin.decorations})
}
