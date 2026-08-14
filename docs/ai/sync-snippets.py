#!/usr/bin/env python3
"""Copy the marked snippet regions of the sample project into the AI documentation.

Run from the repository root after editing samples/CobaltumOrm.Consumer:

    python3 docs/ai/sync-snippets.py

CobaltumOrm.Tests/AiDocumentationTests verifies the same copy, so a build fails when
the documentation and the sample project disagree.
"""

import re
import sys
from pathlib import Path

SOURCES = [
    "samples/CobaltumOrm.Consumer/AiGuideSamples.cs",
    "samples/CobaltumOrm.Consumer/Migrations.cs",
    "samples/CobaltumOrm.Consumer/Migrations/V20__add_display_name.sql",
]
DOCUMENTS = [
    "docs/ai/recipes.md",
    "docs/ai/recipes.ja.md",
]
START = re.compile(r"^(?://|--) <snippet ([a-z0-9-]+)>$")
END = re.compile(r"^(?://|--) </snippet>$")
MARKER = re.compile(r"^<!-- snippet: ([a-z0-9-]+) -->$")


def read_snippets(root):
    snippets = {}
    for relative in SOURCES:
        name = None
        body = []
        for line in (root / relative).read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            start = START.match(stripped)
            if start:
                name, body = start.group(1), []
                continue
            if name is None:
                continue
            if END.match(stripped):
                indent = min(
                    (len(item) - len(item.lstrip()) for item in body if item.strip()),
                    default=0,
                )
                snippets[name] = [
                    item[indent:] if item.strip() else "" for item in body
                ]
                name = None
                continue
            body.append(line)
    return snippets


def rewrite(document, snippets):
    lines = document.read_text(encoding="utf-8").splitlines()
    result = []
    index = 0
    while index < len(lines):
        result.append(lines[index])
        marker = MARKER.match(lines[index])
        if marker is None:
            index += 1
            continue
        name = marker.group(1)
        if name not in snippets:
            raise SystemExit(f"{document}: no snippet named '{name}' in the sample project")
        fence = lines[index + 1]
        closing = index + 2
        while lines[closing] != "```":
            closing += 1
        result.append(fence)
        result.extend(snippets[name])
        result.append("```")
        index = closing + 1
    document.write_text("\n".join(result) + "\n", encoding="utf-8")


def main():
    root = Path(__file__).resolve().parents[2]
    snippets = read_snippets(root)
    for relative in DOCUMENTS:
        rewrite(root / relative, snippets)
    return 0


if __name__ == "__main__":
    sys.exit(main())
