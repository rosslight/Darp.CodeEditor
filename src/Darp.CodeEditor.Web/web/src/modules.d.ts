// Monaco's Lua definition ships without types: the Monarch language and its configuration.
declare module "monaco-editor/languages/definitions/lua/lua" {
  export const conf: import("monaco-editor/editor").languages.LanguageConfiguration
  export const language: any
}
