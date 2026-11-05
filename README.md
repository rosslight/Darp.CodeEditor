# Darp.CodeEditor

A code editor library for Avalonia desktop apps: syntax highlighting, any language through a language server, and C#
scripts with a host-defined API that run in a separate process.

Pre-release: the packages and their API are being designed. Start with [docs/design.md](docs/design.md) for the
goals and structure and [docs/api.md](docs/api.md) for the API draft. The first prototype, with its decision log and
measurements, is on the [`draft`](https://github.com/rosslight/Darp.CodeEditor/tree/draft) branch.

## Tests

`dotnet test` runs the tests. They also run as a NativeAOT app, which shows failures that only NativeAOT has. Publish
the tests and the fixture process they start into one folder, for any runtime identifier, then run the executable:

```sh
dotnet publish tests/Darp.CodeEditor.Tests.BeforeSetup -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests -c Release -r linux-x64 -o artifacts/aot-tests
artifacts/aot-tests/Darp.CodeEditor.Tests
```
