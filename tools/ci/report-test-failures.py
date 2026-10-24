#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Turns failed tests into GitHub annotations so they show on the run summary.

Usage: report-test-failures.py <results dir> [<log file or directory of step logs>]
Reads every .trx file under the results directory. If no test failed (a build step
failed, or the test host crashed before writing results), annotates the last lines of
the most recent step log instead.
"""
import pathlib
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def main() -> int:
    results = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path("TestResults")
    output = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else None

    trx_files = sorted(results.rglob("*.trx")) if results.exists() else []
    failures = 0

    for trx in trx_files:
        root = ET.parse(trx).getroot()
        for result in root.iterfind(".//t:UnitTestResult", NS):
            if result.get("outcome") != "Failed":
                continue
            failures += 1
            name = result.get("testName", "unknown test")
            message = result.findtext(".//t:ErrorInfo/t:Message", default="", namespaces=NS).strip()
            stack = result.findtext(".//t:ErrorInfo/t:StackTrace", default="", namespaces=NS).strip()
            stack = "\n".join(stack.splitlines()[:6])
            print(f"::error title={escape(name)}::{escape(message + chr(10) + stack)}")

    if failures == 0 and output is not None and output.exists():
        logs = sorted(output.glob("*.txt"), key=lambda f: f.stat().st_mtime) if output.is_dir() else [output]
        if logs:
            tail = logs[-1].read_text(encoding="utf-8", errors="replace").splitlines()[-40:]
            print(f"::error title={escape(logs[-1].name)} (last lines)::{escape(chr(10).join(tail))}")

    print(f"{failures} failed test(s) in {len(trx_files)} result file(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
