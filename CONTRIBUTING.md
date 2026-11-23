# Contributing

## Tests

`dotnet test` runs the tests. They also run as a NativeAOT app, which shows failures that only NativeAOT has. Publish
the tests and the fixture processes they start into one folder, for any runtime identifier, then run the executable:

```sh
dotnet publish tests/Darp.CodeEditor.Tests.BeforeSetup -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests.FakeServer -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests.SimpleTheme -c Release -r linux-x64 -o artifacts/aot-tests
dotnet publish tests/Darp.CodeEditor.Tests -c Release -r linux-x64 -o artifacts/aot-tests
artifacts/aot-tests/Darp.CodeEditor.Tests
```

## Sample app

[`samples/Darp.CodeEditor.Sample`](samples/Darp.CodeEditor.Sample) shows how an app uses the API.
`dotnet run --project samples/Darp.CodeEditor.Sample` runs it; its README says how to publish it as NativeAOT.

## Documentation

The [guides](https://rosslight.github.io/Darp.CodeEditor/) are in `docs/` and built with Zensical. A pull request that
changes public behavior updates the page that describes it.

```sh
uv tool install zensical==0.1.3
zensical serve
```

C# examples are regions of the single-file apps in `samples/docs`, which a page includes by file and region name. CI
compiles the apps and builds the site, so a broken example, region or link fails the pull request.

## Benchmarks

`dotnet run -c Release --project benchmarks/Darp.CodeEditor.Benchmarks -- --filter '*'` runs all benchmarks. Each pull
request compares only the `CI` category (`--anyCategories CI`) with its target branch.
