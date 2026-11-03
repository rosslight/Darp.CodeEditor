# Measures both demos on Windows: the in-process benchmarks (--bench) and highlighted text on screen after launch.
# Results go to -Out (default measurements\windows).
#
#   pwsh tools\measure\run-windows.ps1 [-Out dir] [-Runs 5] [-SkipBuild] [-Only bench,startup]
#
# Nothing here injects input. Key-to-pixel latency is not measured on Windows: SendInput inserts into the system input
# stream, not into a window, so a key checked against the foreground window can still reach another one, and that is
# not acceptable on a machine someone works on. The bench's keystroke timings come from inside the demo process.
# Prerequisites: .NET 10 SDK and the Visual Studio C++ build tools (NativeAOT links with link.exe).
param(
    [string]$Out = "",
    [int]$Runs = 5,
    [switch]$SkipBuild,
    [string[]]$Only = @("bench", "startup")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Out) { $Out = Join-Path $root "measurements\windows" }
New-Item -ItemType Directory -Force $Out | Out-Null
Set-Location $root

if (-not $SkipBuild) {
    dotnet build CodeEditor.slnx -c Release -warnaserror *> (Join-Path $Out "build.log")
    if ($LASTEXITCODE) { throw "build failed, see build.log" }
    dotnet publish samples\Darp.CodeEditor.Demo.Aot -c Release -r win-x64 *> (Join-Path $Out "publish-aot.log")
    if ($LASTEXITCODE) { throw "publish failed, see publish-aot.log" }
}

$demos = @(
    @{ Name = "jit"; Exe = "samples\Darp.CodeEditor.Demo\bin\Release\net10.0\Darp.CodeEditor.Demo.exe" },
    @{ Name = "aot"; Exe = "samples\Darp.CodeEditor.Demo.Aot\bin\Release\net10.0\win-x64\publish\Darp.CodeEditor.Demo.Aot.exe" }
)
$probe = "tools\Darp.CodeEditor.Probe\bin\Release\net10.0\Darp.CodeEditor.Probe.dll"

if ($Only -contains "bench") {
    foreach ($demo in $demos) {
        $report = Join-Path $Out "bench-$($demo.Name).txt"
        $process = Start-Process -FilePath $demo.Exe -ArgumentList @("--bench", "--out", $report) -Wait -PassThru
        if ($process.ExitCode) { throw "The $($demo.Name) bench failed (exit code $($process.ExitCode)), see $report." }
    }
}

if ($Only -contains "startup") {
    foreach ($demo in $demos) {
        for ($run = 1; $run -le $Runs; $run++) {
            # The probe starts the demo, watches the screen, then ends the demo and fails if it is still running.
            dotnet $probe startup -- $demo.Exe >> (Join-Path $Out "startup-onscreen-$($demo.Name).txt")
            if ($LASTEXITCODE) { throw "The startup probe failed for the $($demo.Name) demo (exit code $LASTEXITCODE)." }
        }
    }
}

"results in $Out"
