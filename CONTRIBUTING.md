# Contributing

## Tests

`dotnet test` runs the tests. They also run as a NativeAOT app, which shows failures that only NativeAOT has. Publish
the tests and the fixture processes they start into one folder, for any runtime identifier, then run the executable:

```sh
dotnet publish tests/Darp.CodeEditor.Tests.BeforeSetup -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests.FakeServer -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests -c Release -r linux-x64 -o artifacts/aot-tests
artifacts/aot-tests/Darp.CodeEditor.Tests
```

## Sample app

[`samples/Darp.CodeEditor.Sample`](samples/Darp.CodeEditor.Sample) shows how an app uses the API.
`dotnet run --project samples/Darp.CodeEditor.Sample` runs it; its README says how to publish it as NativeAOT.

## Benchmarks

`dotnet run -c Release --project benchmarks/Darp.CodeEditor.Benchmarks -- --filter '*'` runs all benchmarks. Each pull
request compares only the `CI` category (`--anyCategories CI`) with its target branch.

## AvaloniaEdit preview

Until AvaloniaEdit releases the fixes that the editor needs, `Avalonia.AvaloniaEdit` comes from a preview feed on
GitHub Packages (`nuget.config`). GitHub Packages needs a token even for public packages: restore reads one with the
`read:packages` scope from an environment variable, for example the GitHub CLI's:

```sh
export NuGetPackageSourceCredentials_AvaloniaEditPreview="Username=x;Password=$(gh auth token)"
```
