# Packages

The libraries under `src/` are published as NuGet packages to the rosslight GitHub Packages feed,
`https://nuget.pkg.github.com/rosslight/index.json`. The repository is private and the packages inherit that: only
accounts and workflows with access to this repository can restore them.

## Which projects are packages

| Package | Holds |
| --- | --- |
| `Darp.CodeEditor` | The editor core and its LSP client. |
| `Darp.CodeEditor.Lsp` | The JSON-RPC and LSP layer, and `ChildProcess` for helper processes that end with the app. |
| `Darp.CodeEditor.Luau` | Luau for apps: the environment, the luau-lsp service, the Luau grammar, luau-lsp 1.70.1 for Windows, Linux and macOS in `tools/luau-lsp/`, and `buildTransitive/Darp.CodeEditor.Luau.targets`, which puts luau-lsp and the grammar next to the app. |
| `Darp.CodeEditor.CSharp` | C# for apps: the editor's C# service, the script environment (`Darp.CodeEditor.CSharp.Hosting`), the runner API (`Darp.CodeEditor.CSharp.Execution`), the language server and the script runner as portable IL in `tools/`, and `buildTransitive/Darp.CodeEditor.CSharp.targets`, which puts the helpers and their .NET runtime next to the app. |

`Directory.Build.targets` decides: a library under `src/` is a package, with its project name as the package ID.
Executables under `src/` and every project outside it (samples, tests, benchmarks, tools) are not. A project can
override this by setting `IsPackable` itself: `Darp.CodeEditor.CSharp.Hosting`, `.Execution` and `.Scripting` are not
packages of their own, because only `Darp.CodeEditor.CSharp` and the helpers use them. Package metadata and the version
live in `Directory.Build.props`.

Apps that reference `Darp.CodeEditor.CSharp` set `PublishReadyToRun=true` in their project, NativeAOT apps included:
publish compiles the helpers for the app's runtime identifier and copies the runtime from the runtime pack, which restore
then fetches. The README's *Packaging* section has the details. `tests/Darp.CodeEditor.Packaging.Tests` packs the four
packages into a folder and publishes an app outside the repository from them.

## Versions

| Kind | Version | Published by |
| --- | --- | --- |
| Release | `0.4.0` | Merging the release pull request that release-please keeps open on `main`. |
| Pull request preview | `0.4.1-pr.<pull request>.<run>.<attempt>` | Every push to a pull request from a branch of this repository. |
| Manual preview | `0.4.1-preview.<run>.<attempt>` | Running the *Preview packages* workflow by hand. |

release-please reads the conventional commit types on `main`: `feat` raises the minor version, `fix` and `perf` the
patch version, and a breaking change also raises only the minor version while the version is below 1.0. It writes the
new version into `Directory.Build.props`, updates `CHANGELOG.md` and tags the release `v<version>`. To publish an
existing release again, run the *Release Please* workflow by hand with its tag.

A preview is a prerelease of the patch after the last release, so it sorts above that release and below the next one.

## Restoring from the feed

The feed needs a GitHub token with `read:packages`, even for reading. On a developer machine, use a classic personal
access token:

```bash
dotnet nuget add source https://nuget.pkg.github.com/rosslight/index.json --name rosslight \
  --username <github-user> --password <token> --store-password-in-clear-text
```

If the consuming repository uses package source mapping, map `Darp.CodeEditor*` to that source.

In a GitHub Actions workflow of another repository, use its `GITHUB_TOKEN` with `packages: read`, and give that
repository read access in the settings of each package (*Manage Actions access*).
