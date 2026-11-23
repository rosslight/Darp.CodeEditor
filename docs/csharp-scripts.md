# C# scripts

Your app defines what its scripts can see. The editor checks scripts against that definition, the C# server compiles
them, and a separate process runs them. This page shows how to set that up in a JIT or NativeAOT app.

## Set up the projects

A script API has two sides. Only the bridge crosses between the app and the process that runs scripts:

| Project | Holds | Analysed for NativeAOT |
|---|---|---|
| Bridge | The two interfaces between the app and the session, and the data they exchange | Yes |
| Runner side | The session object, the globals and any drivers | No: it may use reflection or native interop |
| App | Editors, the app's half of the bridge, and the calls that start scripts | Yes |

A script API without reflection-heavy drivers may keep the bridge and the runner side in one project.

The bridge references `StreamJsonRpc`. The runner side references the bridge and `Darp.CodeEditor.CSharpScript.Runner`.
The app references the runner package and marks both projects, which is what scripts may use:

```xml
<PropertyGroup>
  <!-- Publishing compiles the C# helpers. It needs a RuntimeIdentifier too, also in a NativeAOT app. -->
  <PublishReadyToRun Condition="'$(_IsPublishing)' == 'true'">true</PublishReadyToRun>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Darp.CodeEditor.CSharpScript.Runner" />
  <ProjectReference Include="..\MyApp.Bridge\MyApp.Bridge.csproj" CSharpScriptReference="true" />
  <ProjectReference Include="..\MyApp.RunnerSide\MyApp.RunnerSide.csproj" CSharpScriptReference="true" />
</ItemGroup>
```

Marked projects and packages are published once, together, so NuGet resolves one version of each dependency. A marked
reference that needs another shared framework, such as ASP.NET Core, or a newer version of a package of the bridge than
the runner loads, fails the publish.

## Define the script API

The bridge's interfaces need `[JsonRpcContract]` and `[GenerateShape]`, and its project needs
`[assembly: ExportRpcContractProxies]`. `StartAsync` checks for that line in every build, because a NativeAOT app would
otherwise fail on its first call.

```csharp
--8<-- "csharp-scripts.cs:bridge"
```

The session object lives in the runner, once per session, and holds what the session's scripts share. It creates the
globals of each run, once per kind of script. The public members of the globals are what scripts see, so implement
`Create`, the factories and any disposal explicitly:

```csharp
--8<-- "csharp-scripts.cs:runner-side"
```

## Edit a script

One `CSharpScriptServer` serves the whole app. An environment says what a kind of script sees, and the server creates
its documents, which editors show like any other:

```csharp
--8<-- "csharp-scripts.cs:edit"
```

Completion, hover, signature help, diagnostics and semantic colors follow the environment. Dispose documents when the
user closes them, and the server when the app exits.

## Run a script

A session is one process, with one session object that its scripts share. Compile the document, start a session and
start the script in it:

```csharp
--8<-- "csharp-scripts.cs:run"
```

- **Overlapping runs:** a session can run several scripts at once, each with its own input and kind of globals.
- **Stopping:** `RunToEndAsync` cancels the run's stop token when its token is cancelled. With `killSessionAfter`, a
  script that doesn't stop in time gets the whole session killed. `StopAsync` stops a run without a token.
- **Starting early:** a session takes about 100 ms to answer. Start one when a page opens, to hide that wait.
- **Isolation:** a kill or a crash takes only its own session down. A session is not a security sandbox.

## Failures and the end of a session

`Completion` and `RunToEndAsync` say how a run ended:

```csharp
--8<-- "csharp-scripts.cs:failures"
```

A failure in a script is a `CSharpScriptException`, with the script's line, also for a callback that outlived its run.
When the process ends, runs and calls fail with `ConnectionLostException`, and `session.Exited` already says why:
`Disposed`, `Killed`, `StopTimedOut`, `Crashed` or `DisposeTimedOut`, with the exit code and the last 100 lines of
standard error.

`DisposeAsync` stops every run, waits for the calls in progress, disposes the session object and ends the process. It
doesn't throw for failures in the runner. `KillAsync` ends the process at once. A session that has ended doesn't
restart, so start a new one.

## Output and child programs

`CSharpScriptSessionOptions.Output` receives each line that the process writes, on any thread. A slow callback delays
nothing, and keeps the newest 10,000 lines waiting.

A program that a script starts reads an empty standard input, and what it prints arrives in `Output` unless the script
redirects it. It can't touch the connection to the app. A child that outlives the session doesn't hold up `Exited`.

## Publish

Publish with a runtime identifier. The app's output then has a `helpers` folder with one .NET runtime, the C# server,
the runner and the marked references. The helpers run on that runtime, so a NativeAOT or self-contained app needs no
.NET on the target machine:

```sh
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## Rules

- Mark the bridge and the runner side with `CSharpScriptReference="true"`.
- Add `[assembly: ExportRpcContractProxies]` to the bridge's project.
- Pass the run's stop token to everything that waits.
- A callback that outlives its run may use only what outlives the run: the session object, not the run's globals.
- Check `session.Exited.IsCompleted` before reusing a session, and start a new one after a kill or a crash.
