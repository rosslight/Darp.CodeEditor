"""Summarizes a bake-off result folder (run-linux.sh or run-windows.ps1) as Markdown tables of medians.

    python3 tools/bakeoff/summarize.py measurements/bakeoff > summary.md
"""
import pathlib
import re
import statistics
import sys

folder = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "measurements/bakeoff")
apps = ["a-jit", "a-aot", "b-jit-monaco", "b-jit-codemirror", "b-aot-monaco", "b-aot-codemirror"]


def read(name):
    path = folder / name
    return path.read_text(errors="replace") if path.exists() else ""


def median(values):
    values = [v for v in values if v is not None]
    return f"{statistics.median(values):.0f}" if values else "–"


def milestone(text, label):
    match = re.search(rf"{re.escape(label)}:\s+(\d+) ms", text)
    return float(match.group(1)) if match else None


def startup_rows():
    labels = [
        ("window opened", "window opened"),
        ("web view adapter created", "web view adapter created"),
        ("page script running", "page script running"),
        ("editor text visible", "text visible (before paint)"),
        ("editor text highlighted", "text highlighted (before paint)"),
        ("C# service ready", "C# service ready"),
        ("LSP initialized (painted)", "LSP initialized in the page"),
        ("semantic colors shown", "semantic colors shown"),
        ("diagnostics shown", "diagnostics shown"),
    ]
    runs = {app: [read(p.name) for p in sorted(folder.glob(f"startup-{app}-*.txt"))] for app in apps}
    print("| ms after process start (median of runs) | " + " | ".join(apps) + " |")
    print("|---|" + "---|" * len(apps))
    for label, title in labels:
        print(f"| {title} | " + " | ".join(median([milestone(t, label) for t in runs[app]]) for app in apps) + " |")
    json = [median([float(m.group(1)) if (m := re.search(r"diagnostics shown (\d+) ms after the tab", t)) else None for t in runs[app]]) for app in apps]
    print("| JSON tab: diagnostics shown, ms after the tab was first shown | " + " | ".join(json) + " |")
    onscreen = []
    for app in apps:
        values = [float(m) for m in re.findall(r"highlighted text (\d+) ms", read(f"startup-onscreen-{app}.txt"))]
        onscreen.append(median(values))
    print("| **highlighted text on screen** (probe, from launching the process) | " + " | ".join(onscreen) + " |")
    print(f"\nruns per app: {', '.join(f'{app} {len(runs[app])}' for app in apps)}\n")


def phase_lines(text, phase):
    """The host line, the page line (WebView demos) and the indented detail lines of one phase."""
    lines = text.splitlines()
    result = []
    for i, line in enumerate(lines):
        if line.startswith(phase):
            result.append(line)
            for detail in lines[i + 1:]:
                if detail.startswith(" ") or detail.startswith(phase):
                    if detail.startswith(phase):
                        result.append(detail)
                    else:
                        result.append(detail)
                else:
                    break
            break
    return result


def bench():
    for app in apps:
        text = read(f"bench-{app}.txt")
        if not text:
            continue
        print(f"### {app}\n\n```")
        for line in text.splitlines():
            if re.match(r"^(C#|JSON|plain|textmate|monaco|codemirror|startup)", line) or line.startswith("  ") or line.startswith("search"):
                print(line)
        print("```\n")


def memory():
    print("| total memory over the process tree | idle after --smoke | after --bench |")
    print("|---|---|---|")
    for app in apps:
        cells = []
        for mode in ["idle", "bench"]:
            match = re.search(r"total over (\d+) processes: (\w+) ([\d.]+) MB, ([\w ]+) ([\d.]+) MB", read(f"memory-{app}-{mode}.txt"))
            cells.append(f"{match.group(3)} MB {match.group(2)}, {match.group(5)} MB {match.group(4)} ({match.group(1)} processes)" if match else "–")
        print(f"| {app} | " + " | ".join(cells) + " |")
    print()


def e2e():
    print("| keystroke -> changed pixels | type 'm' | backspace | timeouts |")
    print("|---|---|---|---|")
    for name in ["a-aot", "b-aot-monaco", "b-aot-codemirror", "b-aot-monaco-nocompositing", "b-aot-codemirror-nocompositing"]:
        text = read(f"e2e-{name}.txt")
        if not text:
            continue
        typed = re.search(r"type 'm'\s*: (.*)", text)
        deleted = re.search(r"backspace\s*: (.*)", text)
        timeouts = re.search(r"timeouts .*?: (\d+)", text)
        print(f"| {name} | {typed.group(1) if typed else '–'} | {deleted.group(1) if deleted else '–'} | {timeouts.group(1) if timeouts else '–'} |")
    print()


print(f"# Bake-off summary: {folder}\n\n## Startup\n")
startup_rows()
print("## End-to-end input latency\n")
e2e()
print("## Memory\n")
memory()
print("## Sizes\n\n```")
print(read("sizes.txt").strip())
print("```\n\n## Benchmarks\n")
bench()
