"""Sums up yesterday's entries by category."""

import csv
from collections import Counter
from datetime import date, timedelta


def summarize(path: str) -> Counter[str]:
    yesterday = date.today() - timedelta(days=1)
    totals: Counter[str] = Counter()
    with open(path, newline="") as file:
        for row in csv.DictReader(file):
            if date.fromisoformat(row["date"]) == yesterday:
                totals[row["category"]] += int(row["amount"])
    return totals


if __name__ == "__main__":
    for category, amount in summarize("entries.csv").most_common():
        print(f"{category:<12} {amount:>8}")
