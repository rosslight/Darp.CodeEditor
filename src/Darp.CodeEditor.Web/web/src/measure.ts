// Measurements inside the page, for the comparison with the AvaloniaEdit editor. Only active with ?measure=1. The
// scenarios are written once against EditorDriver, so Monaco and CodeMirror are measured the same way. The host sends
// the documents (samples/Shared/SampleDocuments.cs) and the scenario parameters.

import {lsp, now, onCommand, postEvent, HostCommand} from "./host"

export interface CaretRect {x: number, y: number, height: number, charWidth: number}

export interface SearchCheck {found: boolean, matches: number, replaced: boolean, undone: boolean}

/** What the measurements need from an editor. Offsets are UTF-16 offsets into the text, lines are 1-based. */
export interface EditorDriver {
  readonly root: HTMLElement
  setText(text: string): void
  text(): string
  setCaret(offset: number): void
  lineEnd(line: number): number
  lineCount(): number
  /** Types one character the way the keyboard does: the editor's input path, auto-closing, completion triggers. */
  type(character: string): void
  scrollToLine(line: number): void
  /** Visible lines the tokenizer has not reached yet. */
  linesWithoutTokens(): number
  hasRenderedLines(): boolean
  /** True when the rendered lines use more than one text color. */
  isHighlighted(): boolean
  /** True when the completion list is shown with at least one item. */
  completionOpen(): boolean
  closeCompletion(): void
  /** Opens the search UI, finds, replaces all and undoes; leaves the search UI open on the pattern. */
  search(pattern: string, replacement: string): SearchCheck
  focus(): void
  /** The caret in CSS pixels, relative to the page's viewport. */
  caretRect(): CaretRect
}

const sleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))

/** Resolves after the next frame was painted (the first task after the next animation frame). */
function afterNextPaint(): Promise<number> {
  return new Promise(resolve => requestAnimationFrame(() => {
    const channel = new MessageChannel()
    channel.port1.onmessage = () => resolve(performance.now())
    channel.port2.postMessage(0)
  }))
}

// Main-thread latency probe, the page's counterpart of the host's UiStallProbe: a message posted every 2 ms measures
// how long the main thread was busy before it could run it. Browsers expose no GC pauses, so none are reported.
class StallProbe {
  private latencies: number[] = []
  private stalls: {at: number, ms: number}[] = []
  private phaseStart = 0
  private postedAt = 0
  private channel = new MessageChannel()

  constructor() {
    this.channel.port1.onmessage = () => {
      const ms = performance.now() - this.postedAt
      this.postedAt = 0
      this.add(ms)
      setTimeout(() => this.post(), 2)
    }
    this.post()
  }

  begin() {
    this.latencies = []
    this.stalls = []
    this.phaseStart = performance.now()
  }

  end(name: string): string {
    if (this.postedAt) this.add(performance.now() - Math.max(this.postedAt, this.phaseStart))
    const sorted = [...this.latencies].sort((a, b) => a - b)
    const pick = (p: number) => sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)] : 0
    const duration = performance.now() - this.phaseStart
    const over16 = this.stalls.length, over50 = this.stalls.filter(s => s.ms > 50).length
    let line = `${name.padEnd(44)} ${duration.toFixed(0).padStart(7)} ms | n ${String(sorted.length).padStart(5)} | ` +
      `p50 ${pick(0.5).toFixed(2).padStart(5)} | p99 ${pick(0.99).toFixed(2).padStart(6)} | ` +
      `max ${(sorted.at(-1) ?? 0).toFixed(2).padStart(7)} ms | >16ms ${String(over16).padStart(2)} | >50ms ${String(over50).padStart(2)} | page main thread`
    const worst = [...this.stalls].sort((a, b) => b.ms - a.ms).slice(0, 6)
    if (worst.length) line += `\n    worst stalls (at ms: length ms): ${worst.map(s => `${s.at.toFixed(0)}: ${s.ms.toFixed(0)}`).join(", ")}`
    return line
  }

  private post() {
    this.postedAt = performance.now()
    this.channel.port2.postMessage(0)
  }

  private add(ms: number) {
    this.latencies.push(ms)
    if (ms > 16) this.stalls.push({at: performance.now() - ms - this.phaseStart, ms})
  }
}

// Classifies LSP messages from the server without parsing them; the server writes "jsonrpc" and "id" first.
const traffic = {completions: 0, lastCompletionItems: 0, initialized: false, completionRequestAt: 0, completionResponseAt: 0}
const completionWaiters = new Set<() => void>()

function observeServerMessage(message: string) {
  const head = message.slice(0, 120)
  if (head.includes('"result":{"isIncomplete"')) {
    traffic.completionResponseAt = performance.now()
    traffic.completions++
    traffic.lastCompletionItems = (message.match(/"label":/g) ?? []).length
    for (const waiter of completionWaiters) waiter()
  } else if (head.includes('"result":{"data":[')) {
    // Semantic tokens: the editors apply them as soon as the response arrives, so the next paint shows them.
    reportFirst("semanticShown")
  } else if (head.includes('"result":{"capabilities"') && !traffic.initialized) {
    traffic.initialized = true
    afterNextPaint().then(() => postEvent("lspReady", {at: now()}))
  }
}

/** Facts about the page that qualify its numbers: timer resolution and the rendering backend. */
function pageInfo() {
  let resolution = Infinity
  let last = performance.now()
  for (let i = 0; i < 200000 && resolution > 0.002; i++) {
    const t = performance.now()
    if (t > last) {
      resolution = Math.min(resolution, t - last)
      last = t
    }
  }
  const gl = document.createElement("canvas").getContext("webgl")
  const debug = gl?.getExtension("WEBGL_debug_renderer_info")
  return {
    timerResolutionMs: resolution,
    crossOriginIsolated: self.crossOriginIsolated,
    webgl: gl ? String(gl.getParameter(debug ? debug.UNMASKED_RENDERER_WEBGL : gl.RENDERER)) : "unavailable",
    devicePixelRatio,
    userAgent: navigator.userAgent,
  }
}

/** Reports when the editor first shows text, and first shows it highlighted (both before the frame is painted). */
export function watchStartup(driver: EditorDriver) {
  let visible = false
  const check = () => {
    if (!visible && driver.hasRenderedLines()) {
      visible = true
      postEvent("visible", {at: now()})
    }
    if (visible && driver.isHighlighted()) {
      observer.disconnect()
      postEvent("highlighted", {at: now()})
    }
  }
  const observer = new MutationObserver(check)
  observer.observe(driver.root, {childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["class"]})
  check()
}

/** Reports when diagnostics or semantic colors were first applied, at the following paint. */
export function reportFirst(type: "diagnosticsShown" | "semanticShown") {
  if (reported.has(type)) return
  reported.add(type)
  afterNextPaint().then(() => postEvent(type, {at: now()}))
}
const reported = new Set<string>()

const keystrokes: number[] = []
const paints: number[] = []

async function typeText(driver: EditorDriver, text: string, delayMs: number) {
  for (const character of text) {
    const start = performance.now()
    driver.type(character)
    keystrokes.push(performance.now() - start)
    afterNextPaint().then(end => paints.push(end - start))
    await sleep(delayMs)
  }
}

function takeKeystrokeSummary(): string[] {
  const sorted = keystrokes.splice(0).sort((a, b) => a - b)
  const frames = paints.splice(0).sort((a, b) => a - b)
  if (!sorted.length) return ["no keystrokes"]
  const at = (values: number[], p: number) => values[Math.min(values.length - 1, Math.floor(values.length * p))]
  // The page clock ticks in whole milliseconds (WebKit); the mean of many rounded samples is still close to the truth.
  const mean = (values: number[]) => values.reduce((a, b) => a + b, 0) / Math.max(1, values.length)
  return [
    `keystroke handling: n ${sorted.length}, p50 ${at(sorted, 0.5).toFixed(2)} ms, p99 ${at(sorted, 0.99).toFixed(2)} ms, max ${sorted.at(-1)!.toFixed(2)} ms, mean ${mean(sorted).toFixed(2)} ms`,
    `input -> after next paint: n ${frames.length}, p50 ${at(frames, 0.5).toFixed(1)} ms, p99 ${at(frames, 0.99).toFixed(1)} ms, max ${(frames.at(-1) ?? 0).toFixed(1)} ms, mean ${mean(frames).toFixed(1)} ms`,
  ]
}

/** Types the last character of `text` and resolves with the time until the completion list is shown. */
async function typeAndWaitForCompletion(driver: EditorDriver, text: string, delayMs: number) {
  await typeText(driver, text.slice(0, -1), delayMs)
  const before = traffic.completions
  const start = performance.now()
  const shown = new Promise<{ms: number, requestMs: number, responseMs: number}>((resolve, reject) => {
    const check = () => {
      if (traffic.completions > before && driver.completionOpen()) {
        observer.disconnect()
        completionWaiters.delete(check)
        clearTimeout(timeout)
        resolve({
          ms: performance.now() - start,
          requestMs: traffic.completionRequestAt - start,
          responseMs: traffic.completionResponseAt - start,
        })
      }
    }
    const observer = new MutationObserver(check)
    observer.observe(document.body, {childList: true, subtree: true, attributes: true, attributeFilter: ["class", "style"]})
    completionWaiters.add(check)
    const timeout = setTimeout(() => {
      observer.disconnect()
      completionWaiters.delete(check)
      const caret = driver.text().length
      reject(new Error(`No completion list after "${text}" (responses ${traffic.completions - before}); text before the caret: ${JSON.stringify(driver.text().slice(Math.max(0, caret - 60)))}`))
    }, 10000)
  })
  // Like the AvaloniaEdit scenario, the triggering keystroke is not part of the keystroke statistics.
  driver.type(text.slice(-1))
  return {...await shown, items: traffic.lastCompletionItems}
}

function percentile(sorted: number[], p: number) {
  return sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)]
}

/** Registers the measurement commands. The host brackets each one with a phase of its own UI-thread probe. */
export function installMeasurements(driver: EditorDriver, createScratchEditor?: () => void) {
  lsp.subscribe(observeServerMessage)
  const send = lsp.send
  lsp.send = message => {
    if (message.slice(0, 160).includes('"method":"textDocument/completion"')) traffic.completionRequestAt = performance.now()
    send(message)
  }
  const probe = new StallProbe()
  postEvent("pageInfo", pageInfo())

  const run = (command: HostCommand, body: (lines: string[]) => Promise<void>) => {
    const lines: string[] = []
    probe.begin()
    body(lines).then(
      () => postEvent("result", {id: command.id, lines: [probe.end(command.phase ?? command.type), ...lines]}),
      (e: any) => postEvent("result", {id: command.id, failed: String(e?.stack ?? e), lines}),
    )
  }

  onCommand("search", command => {
    const check = driver.search(command.pattern, command.replacement)
    const passed = check.found && check.matches > 0 && check.replaced && check.undone
    postEvent("result", {id: command.id, lines: [`search "${command.pattern}": ${passed ? "PASS" : "FAIL"} (found ${check.found}, matches ${check.matches}, replace all ${check.replaced}, undo ${check.undone})`]})
  })

  onCommand("completionLatency", command => run(command, async lines => {
    const delay = command.typingDelayMs
    const latencies: {trigger: string, ms: number, items: number, requestMs: number, responseMs: number}[] = []
    driver.focus()
    driver.setCaret(driver.text().length)
    for (let i = 0; i < command.rounds; i++) {
      const word = await typeAndWaitForCompletion(driver, "\nD", delay)
      latencies.push({trigger: "word", ...word})
      await typeText(driver, "evice", delay)
      const member = await typeAndWaitForCompletion(driver, ".", delay)
      latencies.push({trigger: "member", ...member})
      await typeText(driver, "ReadTemp();", delay)
    }
    await sleep(500)
    lines.push("  " + takeKeystrokeSummary().join("\n  "))
    lines.push(`  first completion: ${latencies[0].ms.toFixed(0)} ms (${latencies[0].items} items)`)
    for (const trigger of ["word", "member"]) {
      const warm = latencies.slice(2).filter(l => l.trigger == trigger).map(l => l.ms).sort((a, b) => a - b)
      const items = latencies.filter(l => l.trigger == trigger).at(-1)!.items
      lines.push(`  warm completion (${trigger}, ${items} items): p50 ${percentile(warm, 0.5).toFixed(0)} ms, p95 ${percentile(warm, 0.95).toFixed(0)} ms, max ${warm.at(-1)!.toFixed(0)} ms (n=${warm.length})`)
      const stage = (key: "requestMs" | "responseMs") => percentile(latencies.slice(2).filter(l => l.trigger == trigger).map(l => l[key]).sort((a, b) => a - b), 0.5).toFixed(0)
      lines.push(`    stages after the keystroke (p50): request sent ${stage("requestMs")} ms, response received ${stage("responseMs")} ms, list shown ${percentile(warm, 0.5).toFixed(0)} ms`)
    }
  }))

  onCommand("typing", command => run(command, async lines => {
    driver.focus()
    if (command.text != null) driver.setText(command.text)
    driver.setCaret(command.caretLine ? driver.lineEnd(command.caretLine) : driver.text().length)
    await sleep(command.settleMs ?? 0)
    await typeText(driver, command.typed, command.typingDelayMs)
    await sleep(command.afterMs ?? 300)
    driver.closeCompletion()
    lines.push("  " + takeKeystrokeSummary().join("\n  "))
  }))

  onCommand("openLarge", command => run(command, async lines => {
    const start = performance.now()
    driver.setText(command.text)
    let visible = 0, highlighted = 0
    while (!highlighted) {
      await new Promise(requestAnimationFrame)
      if (!visible && driver.hasRenderedLines()) visible = performance.now() - start
      if (visible && driver.isHighlighted()) highlighted = performance.now() - start
      if (performance.now() - start > 10000) throw new Error("not highlighted after 10 s")
    }
    await sleep(500)
    lines.push(`  ${driver.lineCount()} lines visible after ${visible.toFixed(0)} ms, highlighted after ${highlighted.toFixed(0)} ms (checked once per frame)`)
  }))

  onCommand("scroll", command => run(command, async lines => {
    let frames = 0, framesWithout = 0, linesWithout = 0
    for (let line = 1; line <= driver.lineCount(); line += command.step) {
      driver.scrollToLine(line)
      await afterNextPaint()
      frames++
      const pending = driver.linesWithoutTokens()
      linesWithout += pending
      if (pending > 0) framesWithout++
    }
    await sleep(300)
    lines.push(`  scrolling a page per frame: ${framesWithout} of ${frames} frames showed lines the tokenizer had not reached (${linesWithout} line-frames)`)
  }))

  onCommand("caret", command => {
    driver.setCaret(driver.lineEnd(command.line))
    driver.focus()
    afterNextPaint().then(() => postEvent("result", {id: command.id, caret: driver.caretRect(), lines: []}))
  })

  onCommand("scratch", command => {
    createScratchEditor?.()
    postEvent("result", {id: command.id, lines: []})
  })
}
