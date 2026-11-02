// The measurement driver for Monaco (see measure.ts); not part of the editor page itself.

import * as monaco from "monaco-editor/editor"
import {EditorDriver} from "./measure"

export function monacoDriver(editor: monaco.editor.IStandaloneCodeEditor, model: monaco.editor.ITextModel, container: HTMLElement): EditorDriver {
  return {
    root: container,
    setText: text => model.setValue(text),
    text: () => model.getValue(),
    setCaret(offset) {
      const position = model.getPositionAt(offset)
      editor.setPosition(position)
      editor.revealPosition(position)
    },
    lineEnd: line => model.getOffsetAt({lineNumber: line, column: model.getLineMaxColumn(line)}),
    lineCount: () => model.getLineCount(),
    type: character => editor.trigger("keyboard", "type", {text: character}),
    scrollToLine: line => editor.setScrollTop(editor.getTopForLineNumber(line)),
    linesWithoutTokens() {
      // Internal API: Monaco paints lines it has not tokenized yet with tokens guessed from an earlier state.
      const tokenization = (model as any).tokenization
      let count = 0
      for (const range of editor.getVisibleRanges())
        for (let line = range.startLineNumber; line <= range.endLineNumber; line++)
          if (!tokenization.hasAccurateTokensForLine(line)) count++
      return count
    },
    hasRenderedLines: () => !!container.querySelector(".view-lines .view-line")?.textContent?.trim(),
    isHighlighted() {
      const classes = new Set<string>()
      for (const span of container.querySelectorAll(".view-lines .view-line span span")) classes.add(span.className)
      return classes.size > 1
    },
    // Monaco displays the widget at once and adds its "visible" class 100 ms later, which changes nothing on screen.
    completionOpen() {
      const widget = document.querySelector<HTMLElement>(".suggest-widget")
      return !!widget && getComputedStyle(widget).visibility != "hidden" && !!widget.querySelector(".monaco-list-row")
    },
    closeCompletion: () => editor.trigger("measure", "hideSuggestWidget", null),
    search(pattern, replacement) {
      const original = model.getValue()
      const find = editor.getContribution("editor.contrib.findController") as any
      editor.getAction("actions.find")!.run()
      find.getState().change({searchString: pattern, replaceString: replacement, isRegex: false, matchCase: false, wholeWord: false}, false)
      editor.setPosition({lineNumber: 1, column: 1})
      find.moveToNextMatch()
      const found = model.getValueInRange(editor.getSelection()!).toLowerCase() == pattern.toLowerCase()
      const matches = model.findMatches(pattern, false, false, false, null, false).length
      find.replaceAll()
      const text = model.getValue()
      const replaced = !text.toLowerCase().includes(pattern.toLowerCase()) && text.split(replacement).length - 1 >= matches
      editor.trigger("measure", "undo", null)
      const undone = model.getValue() == original
      find.getState().change({searchString: pattern, isReplaceRevealed: false}, false)
      return {found, matches, replaced, undone}
    },
    focus: () => editor.focus(),
    caretRect() {
      const position = editor.getScrolledVisiblePosition(editor.getPosition()!)!
      const box = container.getBoundingClientRect()
      const font = editor.getOption(monaco.editor.EditorOption.fontInfo)
      return {x: box.left + position.left, y: box.top + position.top, height: position.height, charWidth: font.typicalHalfwidthCharacterWidth}
    },
  }
}
