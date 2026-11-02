// Bundles the editor pages into dist/, which the .NET build copies next to the app as editor/.
import * as esbuild from "esbuild"
import {cpSync, mkdirSync, rmSync} from "node:fs"

rmSync("dist", {recursive: true, force: true})
mkdirSync("dist")

const common = {
  bundle: true,
  minify: true,
  format: "iife",
  target: "es2022",
  legalComments: "linked",
  loader: {".ttf": "file"},
  assetNames: "[name]",
  logLevel: "warning",
}

await Promise.all([
  esbuild.build({...common, entryPoints: {monaco: "src/monaco.ts"}, outdir: "dist"}),
  esbuild.build({...common, entryPoints: {"editor.worker": "src/monaco-worker.ts"}, outdir: "dist"}),
  esbuild.build({...common, entryPoints: {codemirror: "src/codemirror.ts"}, outdir: "dist"}),
])

for (const file of ["monaco.html", "codemirror.html", "page.css"]) cpSync(`src/${file}`, `dist/${file}`)
cpSync("node_modules/monaco-editor/LICENSE", "dist/monaco-editor.LICENSE.txt")
cpSync("node_modules/monaco-editor/ThirdPartyNotices.txt", "dist/monaco-editor.ThirdPartyNotices.txt")
