# Contributing

## Tests

`dotnet test` runs the tests. They also run as a NativeAOT app, which shows failures that only NativeAOT has. Publish
the tests and the fixture process they start into one folder, for any runtime identifier, then run the executable:

```sh
dotnet publish tests/Darp.CodeEditor.Tests.BeforeSetup -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests -c Release -r linux-x64 -o artifacts/aot-tests
artifacts/aot-tests/Darp.CodeEditor.Tests
```
