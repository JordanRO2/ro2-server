#!/usr/bin/env python3
"""Render a .trx test run as a GitHub Actions job summary plus failure annotations.

Called by ci.yml after `dotnet test`. Reads every .trx under the results directory,
writes a markdown table (total / passed / failed / skipped, plus each failure's
message) to $GITHUB_STEP_SUMMARY, and emits a `::error::` annotation per failed
test so failures show inline on the run and on the changed files.

It never changes the job's pass/fail: `dotnet test` already set that. This step
always exits 0 so a reporting hiccup cannot turn a green run red (or vice versa).
Zero third-party dependencies: TRX is plain XML and the runner ships Python 3.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def area_of(test_name):
    # "RO2.Tests.Packets.Foo.Bar" -> "Packets"; group by the segment after RO2.Tests.
    parts = test_name.split(".")
    try:
        i = parts.index("Tests")
        return parts[i + 1] if i + 1 < len(parts) else "(root)"
    except ValueError:
        return parts[0] if parts else "(root)"


def load(results_dir):
    total = passed = failed = skipped = 0
    failures = []  # (name, message, stacktrace)
    areas = {}  # area -> [passed, failed, skipped]
    files = sorted(glob.glob(os.path.join(results_dir, "**", "*.trx"), recursive=True))
    for path in files:
        root = ET.parse(path).getroot()
        counters = root.find(f"{NS}ResultSummary/{NS}Counters")
        if counters is not None:
            total += int(counters.get("total", 0))
            passed += int(counters.get("passed", 0))
            failed += int(counters.get("failed", 0))
            # xUnit reports "not executed" for [Fact(Skip=...)] / DataFact skips.
            skipped += int(counters.get("notExecuted", 0)) + int(counters.get("disconnected", 0))
        for r in root.iter(f"{NS}UnitTestResult"):
            outcome = r.get("outcome")
            name = r.get("testName", "(unknown test)")
            a = areas.setdefault(area_of(name), [0, 0, 0])
            if outcome == "Passed":
                a[0] += 1
            elif outcome == "Failed":
                a[1] += 1
                err = r.find(f"{NS}Output/{NS}ErrorInfo")
                msg = (err.findtext(f"{NS}Message", "") if err is not None else "").strip()
                trace = (err.findtext(f"{NS}StackTrace", "") if err is not None else "").strip()
                failures.append((name, msg, trace))
            else:
                a[2] += 1
    return files, total, passed, failed, skipped, failures, areas


def coverage_rate(results_dir):
    # coverlet writes one coverage.cobertura.xml per run under a GUID dir; take the first.
    for path in sorted(glob.glob(os.path.join(results_dir, "**", "coverage.cobertura.xml"), recursive=True)):
        try:
            root = ET.parse(path).getroot()
            lr = root.get("line-rate")
            if lr is not None:
                return round(float(lr) * 100, 1)
        except Exception:
            pass
    return None


def main():
    # The summary file is written UTF-8; make the stdout fallback (and annotations)
    # UTF-8 too so the emoji verdict does not crash a non-UTF-8 console (e.g. Windows).
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    results_dir = sys.argv[1] if len(sys.argv) > 1 else "TestResults"
    files, total, passed, failed, skipped, failures, areas = load(results_dir)
    cov = coverage_rate(results_dir)

    out = []
    if not files:
        out.append("### Test results\n\nNo .trx file was produced (the test step did not run).\n")
    else:
        verdict = "✅ all passing" if failed == 0 else f"❌ {failed} failing"
        out.append("### Test results\n")
        cov_txt = f", {cov}% line coverage" if cov is not None else ""
        out.append(f"**{verdict}** — {passed} passed, {failed} failed, {skipped} skipped, {total} total{cov_txt}.\n")
        out.append("| Outcome | Count |\n|---|---:|\n")
        out.append(f"| Passed | {passed} |\n| Failed | {failed} |\n| Skipped | {skipped} |\n| Total | {total} |\n")
        # Per-area breakdown, so a reader sees WHICH subsystems ran and which were skipped.
        if areas:
            out.append("\n<details><summary>By area</summary>\n\n| Area | Passed | Failed | Skipped |\n|---|---:|---:|---:|\n")
            for area in sorted(areas):
                p, f_, s = areas[area]
                out.append(f"| {area} | {p} | {f_} | {s} |\n")
            out.append("\n</details>\n")
        if skipped:
            out.append(
                f"\n> {skipped} tests skipped: the client-derived `data/` tables are gitignored (copyrighted) "
                "and absent on CI, so every `[DataFact]`/`[DataTheory]` that loads them is skipped. "
                "Set `RO2_DATA_DIR` (or extract via `data-pipeline/`) to run them.\n"
            )
        if failures:
            out.append("\n#### Failures\n")
            for name, msg, _ in failures:
                first = msg.splitlines()[0] if msg else "(no message)"
                out.append(f"- **{name}** — {first}\n")

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as f:
            f.write("".join(out))
    else:
        sys.stdout.write("".join(out))

    # Inline annotations: one per failed test, so they surface on the run page.
    for name, msg, trace in failures:
        line = (msg or "test failed").splitlines()[0].replace("\n", " ")
        if trace:
            line += " | " + trace.splitlines()[0].strip()
        # ::error:: must be a single line.
        print(f"::error title=Test failed: {name}::{line[:800]}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
