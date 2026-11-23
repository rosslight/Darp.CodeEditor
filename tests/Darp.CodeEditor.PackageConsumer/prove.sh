#!/usr/bin/env bash
# Proves the NuGet packages as a customer uses them, on this machine's platform: packs them, copies the consumer outside
# the repository's MSBuild imports, then builds, publishes and runs it. Usage: prove.sh <rid> [empty work directory].
set -euo pipefail

rid=$1
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=${2:-$(mktemp -d)}
extension=""
[[ $rid == win-* ]] && extension=.exe
# A new version each run: NuGet keeps the content of a version in its global folder for good.
version=0.0.0-consumer.$(date +%s)

cp -R "$repo/tests/Darp.CodeEditor.PackageConsumer/." "$work/"
cp "$repo/global.json" "$work/"
dotnet pack "$repo/src/Darp.CodeEditor" -c Release -o "$work/feed" "-p:Version=$version"
dotnet pack "$repo/src/Darp.CodeEditor.CSharpScript" -c Release -o "$work/feed" "-p:Version=$version" -p:IsPackable=true
cd "$work"
export DarpCodeEditorVersion=$version

# The publish must fail, and say this.
expect_failure() {
    local project=$1 message=$2 log
    if log=$(dotnet publish "$project" -c Release -r "$rid" -o "out/failing" 2>&1); then
        echo "$project published, but it must fail." >&2
        exit 1
    fi
    grep -q "$message" <<<"$log" || { echo "$log" >&2; echo "$project failed without '$message'." >&2; exit 1; }
    echo "$project failed as it should: $message"
}

echo "=== Development launch"
dotnet build Consumer -c Debug
"Consumer/bin/Debug/net10.0/Consumer$extension"

echo "=== JIT"
dotnet publish Consumer -c Release -r "$rid" --self-contained false -o out/jit
# The app runs on the installed .NET, wherever it is. The variable only proves that its helper looks in ../dotnet.
DOTNET_ROOT=/nonexistent dotnet out/jit/Consumer.dll
dotnet build PayloadProbe -c Release -r "$rid" --self-contained false -o out/probe
DOTNET_ROOT="$PWD/out/jit/helpers/dotnet" DOTNET_MULTILEVEL_LOOKUP=0 "out/probe/PayloadProbe$extension" out/jit

echo "=== NativeAOT"
dotnet publish Consumer -c Release -r "$rid" -p:PublishAot=true -o out/aot
DOTNET_ROOT=/nonexistent "out/aot/Consumer$extension"
DOTNET_ROOT="$PWD/out/aot/helpers/dotnet" DOTNET_MULTILEVEL_LOOKUP=0 "out/probe/PayloadProbe$extension" out/aot

echo "=== A republish leaves nothing stale"
dotnet publish Consumer -c Release -r "$rid" --self-contained false -o out/jit -p:WithoutCompatibleA=true
if [[ -e out/jit/helpers/script-api/CompatibleA.dll ]] || grep -q CompatibleA out/jit/helpers/script-references.txt; then
    echo "A removed reference stayed in the payload." >&2
    exit 1
fi

echo "=== What must fail"
expect_failure Incompatible/App NU1107
expect_failure SharedFramework/App Microsoft.AspNetCore.App
echo "All passed."
