# Measures both demos on Windows: the in-process benchmarks (--bench), highlighted text on screen after launch, and
# keystroke to changed pixels (Probe latency). Results go to -Out (default measurements\windows).
#
#   pwsh tools\measure\run-windows.ps1 [-Out dir] [-Runs 5] [-E2eCount 60] [-SkipBuild] [-Only bench,startup,e2e]
#
# The latency probe injects real keystrokes with SendInput. Before every key it checks that the demo's window is the
# foreground window and aborts otherwise, so do not use the machine while it runs; switching windows ends the run.
# Prerequisites: .NET 10 SDK and the Visual Studio C++ build tools (NativeAOT links with link.exe).
param(
    [string]$Out = "",
    [int]$Runs = 5,
    [int]$E2eCount = 60,
    [switch]$SkipBuild,
    [string[]]$Only = @("bench", "startup", "e2e")
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
        Start-Process -FilePath $demo.Exe -ArgumentList @("--bench", "--out", $report) -Wait
    }
}

if ($Only -contains "startup") {
    foreach ($demo in $demos) {
        for ($run = 1; $run -le $Runs; $run++) {
            dotnet $probe startup -- $demo.Exe >> (Join-Path $Out "startup-onscreen-$($demo.Name).txt")
        }
    }
}

if ($Only -contains "e2e") {
    # A C# document without language features; the probe clicks behind line 1 and types there.
    foreach ($demo in $demos) {
        $target = Join-Path $Out "e2e-$($demo.Name).json"
        $result = Join-Path $Out "e2e-$($demo.Name).txt"
        Remove-Item $target -ErrorAction SilentlyContinue
        $process = Start-Process -FilePath $demo.Exe -ArgumentList @("--e2e", $target) -PassThru
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Start-Sleep -Seconds 3
        dotnet $probe latency --target $target --count $E2eCount --out $result
        if ($LASTEXITCODE) { Write-Warning "latency $($demo.Name) aborted, see $result" }
        taskkill /PID $process.Id /T /F | Out-Null
        Start-Sleep -Seconds 1
    }
}

"results in $Out"
