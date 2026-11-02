# Runs the editor bake-off on Windows (win-x64, WebView2): the same apps, scenarios and probes as run-linux.sh.
# Results go to -Out (default measurements\bakeoff-windows).
#
#   pwsh tools\bakeoff\run-windows.ps1 [-Out dir] [-Runs 5] [-E2eCount 60] [-SkipBuild] [-Only startup,bench,memory,e2e,sizes]
#
# Prerequisites: .NET 10 SDK, Node 22 with npm (the editor pages are bundled during the build), the Visual Studio C++
# build tools (NativeAOT links with link.exe), and the WebView2 runtime (part of Windows 11).
param(
    [string]$Out = "",
    [int]$Runs = 5,
    [int]$E2eCount = 60,
    [switch]$SkipBuild,
    [string[]]$Only = @("startup", "bench", "memory", "e2e", "sizes")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Out) { $Out = Join-Path $root "measurements\bakeoff-windows" }
New-Item -ItemType Directory -Force $Out | Out-Null
Set-Location $root

if (-not $SkipBuild) {
    dotnet build CodeEditor.slnx -c Release -warnaserror *> (Join-Path $Out "build.log")
    if ($LASTEXITCODE) { throw "build failed, see build.log" }
    dotnet publish samples\Darp.CodeEditor.Demo.Aot -c Release -r win-x64 *> (Join-Path $Out "publish-a-aot.log")
    dotnet publish samples\Darp.CodeEditor.WebDemo.Aot -c Release -r win-x64 *> (Join-Path $Out "publish-b-aot.log")
}

$aJit = "samples\Darp.CodeEditor.Demo\bin\Release\net10.0\Darp.CodeEditor.Demo.exe"
$aAot = "samples\Darp.CodeEditor.Demo.Aot\bin\Release\net10.0\win-x64\publish\Darp.CodeEditor.Demo.Aot.exe"
$bJit = "samples\Darp.CodeEditor.WebDemo\bin\Release\net10.0\Darp.CodeEditor.WebDemo.exe"
$bAot = "samples\Darp.CodeEditor.WebDemo.Aot\bin\Release\net10.0\win-x64\publish\Darp.CodeEditor.WebDemo.Aot.exe"
$probe = "tools\Darp.CodeEditor.Probe\bin\Release\net10.0\Darp.CodeEditor.Probe.dll"

$apps = @(
    @{ Name = "a-jit"; Exe = $aJit; Args = @() },
    @{ Name = "a-aot"; Exe = $aAot; Args = @() },
    @{ Name = "b-jit-monaco"; Exe = $bJit; Args = @("--editor", "monaco") },
    @{ Name = "b-jit-codemirror"; Exe = $bJit; Args = @("--editor", "codemirror") },
    @{ Name = "b-aot-monaco"; Exe = $bAot; Args = @("--editor", "monaco") },
    @{ Name = "b-aot-codemirror"; Exe = $bAot; Args = @("--editor", "codemirror") }
)

# Starts an app, waits until it wrote $WaitFor, runs $Action with its process id, then stops it and its children.
function With-App([string]$Exe, [string[]]$Arguments, [string]$WaitFor, [int]$TimeoutSeconds, [scriptblock]$Action) {
    Remove-Item $WaitFor -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path $WaitFor) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Seconds 3
    & $Action $process.Id
    taskkill /PID $process.Id /T /F | Out-Null
    Start-Sleep -Seconds 1
}

if ($Only -contains "startup") {
    foreach ($app in $apps) {
        for ($run = 1; $run -le $Runs; $run++) {
            $report = Join-Path $Out "startup-$($app.Name)-$run.txt"
            Start-Process -FilePath $app.Exe -ArgumentList (@("--smoke", "--out", $report) + $app.Args) -Wait
            dotnet $probe startup -- $app.Exe @($app.Args) >> (Join-Path $Out "startup-onscreen-$($app.Name).txt")
        }
    }
}

if ($Only -contains "bench") {
    foreach ($app in $apps) {
        $report = Join-Path $Out "bench-$($app.Name).txt"
        Start-Process -FilePath $app.Exe -ArgumentList (@("--bench", "--out", $report) + $app.Args) -Wait
    }
}

if ($Only -contains "memory") {
    foreach ($app in $apps) {
        foreach ($mode in @("smoke", "bench")) {
            $name = if ($mode -eq "smoke") { "idle" } else { "bench" }
            $report = Join-Path $Out "memory-$($app.Name)-$name.report"
            $result = Join-Path $Out "memory-$($app.Name)-$name.txt"
            With-App $app.Exe (@("--$mode", "--hold", "--out", $report) + $app.Args) $report 600 {
                param($processId) dotnet $probe memory --pid $processId --out $result
            }
        }
    }
}

if ($Only -contains "e2e") {
    # A C# document without language features in each AOT demo; the probe clicks behind line 1 and types there.
    foreach ($app in @(
            @{ Name = "a-aot"; Exe = $aAot; Args = @() },
            @{ Name = "b-aot-monaco"; Exe = $bAot; Args = @("--editor", "monaco") },
            @{ Name = "b-aot-codemirror"; Exe = $bAot; Args = @("--editor", "codemirror") })) {
        $target = Join-Path $Out "e2e-$($app.Name).json"
        $result = Join-Path $Out "e2e-$($app.Name).txt"
        With-App $app.Exe ($app.Args + @("--e2e", $target)) $target 60 {
            param($processId) dotnet $probe latency --target $target --count $E2eCount --out $result
        }
    }
}

if ($Only -contains "sizes") {
    $lines = foreach ($dir in @((Split-Path $aAot), (Split-Path $bAot))) {
        $files = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Extension -notin ".pdb", ".dbg" }
        "$dir (without .pdb):"
        $files | Sort-Object Length -Descending | ForEach-Object { "$($_.Length) $($_.FullName.Substring($dir.Length + 1))" }
        "total {0:F1} MB" -f (($files | Measure-Object Length -Sum).Sum / 1e6)
        ""
    }
    $runtime = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue
    $lines += "WebView2 runtime: $($runtime.pv)"
    $lines | Set-Content (Join-Path $Out "sizes.txt")
    (Select-String -Path (Join-Path $Out "publish-*.log") -Pattern " warning " | Measure-Object).Count |
        Set-Content (Join-Path $Out "aot-warnings.txt")
}

"results in $Out"
