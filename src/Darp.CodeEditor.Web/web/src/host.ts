// The bridge to the Avalonia host. The page sends strings: "L" + an LSP message, or "E" + a JSON event. The host calls
// __host.lsp(message) and __host.command(object) through script evaluation.

export type Theme = "dark" | "light"

export interface HostCommand {
  type: string
  [key: string]: any
}

declare global {
  interface Window {
    invokeCSharpAction?: (message: string) => void
    __host: {lsp(message: string): void, command(command: HostCommand): void}
  }
}

const query = new URLSearchParams(location.search)

export const config = {
  language: query.get("language") ?? "plaintext",
  uri: query.get("uri") ?? "file:///untitled.txt",
  theme: (query.get("theme") ?? "dark") as Theme,
  lsp: query.get("lsp") == "1",
  measure: query.get("measure") == "1",
}

/** Wall-clock milliseconds since 1970, comparable with the host's process start time. */
export const now = () => performance.timeOrigin + performance.now()

function post(message: string) {
  if (window.invokeCSharpAction) window.invokeCSharpAction(message)
  else console.log(message)
}

export function postEvent(type: string, data: object = {}) {
  post("E" + JSON.stringify({type, ...data}))
}

const lspListeners = new Set<(message: string) => void>()
const commandHandlers = new Map<string, (command: HostCommand) => void>()

export const lsp = {
  send(message: string) { post("L" + message) },
  subscribe(listener: (message: string) => void) { lspListeners.add(listener) },
  unsubscribe(listener: (message: string) => void) { lspListeners.delete(listener) },
}

export function onCommand(type: string, handler: (command: HostCommand) => void) {
  commandHandlers.set(type, handler)
}

window.__host = {
  lsp(message) {
    for (const listener of lspListeners) listener(message)
  },
  command(command) {
    const handler = commandHandlers.get(command.type)
    if (!handler) {
      postEvent("error", {message: `Unknown command ${command.type}`})
      return
    }
    try {
      handler(command)
    } catch (e: any) {
      postEvent("error", {message: String(e?.message ?? e), stack: e?.stack})
    }
  },
}

window.addEventListener("error", e => postEvent("error", {message: String(e.message), stack: e.error?.stack}))
window.addEventListener("unhandledrejection", e => postEvent("error", {message: String(e.reason?.message ?? e.reason), stack: e.reason?.stack}))

/** Ctrl+S belongs to the host: the page forwards it instead of the browser handling it. */
export function forwardHostShortcuts(target: HTMLElement) {
  target.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() == "s") {
      e.preventDefault()
      e.stopPropagation()
      postEvent("save")
    }
  }, true)
}

export function setPageTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme
}
