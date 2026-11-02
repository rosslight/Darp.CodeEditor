// The measurement driver for CodeMirror (see measure.ts); not part of the editor page itself.

import {EditorView} from "@codemirror/view"
import {insertNewlineAndIndent, undo} from "@codemirror/commands"
import {syntaxTreeAvailable} from "@codemirror/language"
import {SearchQuery, findNext, openSearchPanel, replaceAll, setSearchQuery} from "@codemirror/search"
import {closeCompletion, completionStatus} from "@codemirror/autocomplete"
import {EditorDriver} from "./measure"

export function codeMirrorDriver(view: EditorView): EditorDriver {
  return {
    root: view.dom,
    setText: text => view.dispatch({changes: {from: 0, to: view.state.doc.length, insert: text}}),
    text: () => view.state.doc.toString(),
    setCaret: offset => view.dispatch({selection: {anchor: offset}, scrollIntoView: true}),
    lineEnd: line => view.state.doc.line(line).to,
    lineCount: () => view.state.doc.lines,
    type(character) {
      // What typing does: Enter runs its key binding, other characters go through the input handlers (auto-closing
      // brackets) and otherwise insert as "input.type", which starts completion.
      if (character == "\n") {
        insertNewlineAndIndent(view)
        return
      }
      const {from, to} = view.state.selection.main
      const insert = () => view.state.update({
        changes: {from, to, insert: character}, selection: {anchor: from + character.length}, userEvent: "input.type", scrollIntoView: true,
      })
      if (!view.state.facet(EditorView.inputHandler).some(handler => handler(view, from, to, character, insert)))
        view.dispatch(insert())
    },
    scrollToLine: line => view.dispatch({effects: EditorView.scrollIntoView(view.state.doc.line(line).from, {y: "start"})}),
    linesWithoutTokens() {
      let count = 0
      for (const {from, to} of view.visibleRanges)
        for (let pos = from; pos <= to;) {
          const line = view.state.doc.lineAt(pos)
          if (!syntaxTreeAvailable(view.state, line.to)) count++
          pos = line.to + 1
        }
      return count
    },
    hasRenderedLines: () => !!view.contentDOM.querySelector(".cm-line")?.textContent?.trim(),
    // Highlight style classes are generated names starting with "ͼ".
    isHighlighted: () => !!view.contentDOM.querySelector(".cm-line span[class*='ͼ']"),
    completionOpen: () => completionStatus(view.state) == "active" && !!view.dom.querySelector(".cm-tooltip-autocomplete li"),
    closeCompletion: () => closeCompletion(view),
    search(pattern, replacement) {
      const original = view.state.doc.toString()
      openSearchPanel(view)
      view.dispatch({effects: setSearchQuery.of(new SearchQuery({search: pattern, replace: replacement, caseSensitive: false}))})
      view.dispatch({selection: {anchor: 0}})
      findNext(view)
      const found = view.state.sliceDoc(view.state.selection.main.from, view.state.selection.main.to).toLowerCase() == pattern.toLowerCase()
      const matches = original.toLowerCase().split(pattern.toLowerCase()).length - 1
      replaceAll(view)
      const text = view.state.doc.toString()
      const replaced = !text.toLowerCase().includes(pattern.toLowerCase()) && text.split(replacement).length - 1 >= matches
      undo(view)
      const undone = view.state.doc.toString() == original
      view.dispatch({effects: setSearchQuery.of(new SearchQuery({search: pattern, caseSensitive: false}))})
      return {found, matches, replaced, undone}
    },
    focus: () => view.focus(),
    caretRect() {
      const coords = view.coordsAtPos(view.state.selection.main.head)!
      return {x: coords.left, y: coords.top, height: coords.bottom - coords.top, charWidth: view.defaultCharacterWidth}
    },
  }
}
