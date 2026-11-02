// Luau syntax on top of Lua: the keywords continue, export, type and typeof, compound assignments and backtick
// strings with {interpolation}. Type annotations are colored like identifiers; luau-lsp semantic tokens would add more.

import type {StreamParser, StringStream} from "@codemirror/language"

export const luauKeywords = [
  "and", "break", "continue", "do", "else", "elseif", "end", "export", "false", "for", "function", "if", "in", "local",
  "nil", "not", "or", "repeat", "return", "then", "true", "type", "typeof", "until", "while",
]

export const luauBuiltins = [
  "assert", "error", "getmetatable", "ipairs", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawlen",
  "rawset", "require", "select", "setmetatable", "tonumber", "tostring", "unpack", "xpcall", "bit32", "buffer",
  "coroutine", "debug", "math", "os", "string", "table", "task", "utf8",
]

/** Monarch rules for Monaco, extending Monaco's Lua definition. */
export function luauMonarch(lua: any): any {
  const root = lua.tokenizer.root.filter((rule: any) => !(Array.isArray(rule) && String(rule[0]) == String(/[a-zA-Z_]\w*/)))
  return {
    ...lua,
    tokenPostfix: ".luau",
    keywords: luauKeywords,
    builtins: luauBuiltins,
    operators: [...lua.operators, "+=", "-=", "*=", "/=", "//", "//=", "%=", "^=", "..=", "->", "::", "?"],
    tokenizer: {
      ...lua.tokenizer,
      root: [
        [/[a-zA-Z_]\w*/, {cases: {"@keywords": {token: "keyword.$0"}, "@builtins": "type.identifier", "@default": "identifier"}}],
        [/`/, "string", "@interpolated"],
        ...root,
      ],
      interpolated: [
        [/[^`\\{]+/, "string"],
        [/\\./, "string.escape"],
        [/\{/, {token: "delimiter.bracket", next: "@interpolation"}],
        [/`/, "string", "@pop"],
      ],
      interpolation: [
        [/\}/, {token: "delimiter.bracket", next: "@pop"}],
        {include: "@root"},
      ],
    },
  }
}

interface LuauState {
  /** Set while inside a long bracket string or comment: its closing bracket and token. */
  long: {close: string, token: string} | null
  /** Depth of {} inside an interpolated string: >0 means code. */
  interpolation: number[]
}

const keywords = new Set(luauKeywords)
const builtins = new Set(luauBuiltins)

function readLong(stream: StringStream, state: LuauState) {
  const long = state.long!
  const end = stream.string.indexOf(long.close, stream.pos)
  if (end < 0) stream.skipToEnd()
  else {
    stream.pos = end + long.close.length
    state.long = null
  }
  return long.token
}

function readInterpolated(stream: StringStream, state: LuauState) {
  while (!stream.eol()) {
    const ch = stream.next()
    if (ch == "\\") stream.next()
    else if (ch == "`") { state.interpolation.pop(); return "string" }
    else if (ch == "{") { state.interpolation[state.interpolation.length - 1] = 1; stream.backUp(1); return "string" }
  }
  return "string"
}

/** A CodeMirror stream parser for Luau. */
export const luauStreamParser: StreamParser<LuauState> = {
  name: "luau",
  startState: () => ({long: null, interpolation: []}),
  copyState: state => ({long: state.long, interpolation: [...state.interpolation]}),
  token(stream, state) {
    if (state.long) return readLong(stream, state)
    const depth = state.interpolation.length
    if (depth && state.interpolation[depth - 1] == 0) return readInterpolated(stream, state)
    if (stream.eatSpace()) return null
    if (depth && stream.peek() == "{" && state.interpolation[depth - 1] == 1) { stream.next(); state.interpolation[depth - 1] = 2; return "bracket" }
    if (depth && stream.peek() == "}" && state.interpolation[depth - 1] == 2) { stream.next(); state.interpolation[depth - 1] = 0; return "bracket" }
    let long = stream.match(/^--\[(=*)\[/) as RegExpMatchArray | null
    if (long) { state.long = {close: `]${long[1]}]`, token: "comment"}; return readLong(stream, state) }
    if (stream.match("--")) { stream.skipToEnd(); return "comment" }
    long = stream.match(/^\[(=*)\[/) as RegExpMatchArray | null
    if (long) { state.long = {close: `]${long[1]}]`, token: "string"}; return readLong(stream, state) }
    const ch = stream.next()!
    if (ch == '"' || ch == "'") {
      while (!stream.eol()) {
        const next = stream.next()
        if (next == "\\") stream.next()
        else if (next == ch) break
      }
      return "string"
    }
    if (ch == "`") { state.interpolation.push(0); return readInterpolated(stream, state) }
    if (/\d/.test(ch) || (ch == "." && stream.match(/^\d/, false))) {
      stream.match(/^(?:x[\da-fA-F_]+|b[01_]+|[\d_]*(?:\.[\d_]*)?(?:[eE][+-]?\d+)?)/)
      return "number"
    }
    if (/[a-zA-Z_]/.test(ch)) {
      stream.eatWhile(/\w/)
      const word = stream.current()
      return keywords.has(word) ? "keyword" : builtins.has(word) ? "builtin" : "variable"
    }
    if (/[+\-*/%^#=<>~.:?]/.test(ch)) { stream.eatWhile(/[+\-*/%^#=<>~.:]/); return "operator" }
    return null
  },
  languageData: {commentTokens: {line: "--", block: {open: "--[[", close: "]]"}}},
}
