#!/usr/bin/env python3
"""Summarize Kavita scanner timing from log files without exposing titles.

This is a read-only log parser. By default it redacts series names and prints
stable hashes for slow series so reports can be shared without leaking library
contents.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from statistics import median


BEGIN_LIBRARY_RE = re.compile(r"Beginning file scan on (?P<library>.+)$")
EMPTY_RE = re.compile(r" is empty or has no matching file types$")
FINISHED_LIBRARY_RE = re.compile(
    r"Finished library scan of (?:(?P<files>\d+) files and )?"
    r"(?P<series>\d+) series in (?P<ms>\d+) milliseconds for "
    r"(?P<library>.+?)(?:\. There were no changes)?$"
)
FINISHED_SCAN_RE = re.compile(r"Finished scan in (?P<ms>\d+) milliseconds\.")
FINISHED_SERIES_RE = re.compile(r"Finished series update on (?P<series>.+) in (?P<ms>\d+) ms$")
FOUND_RE = re.compile(r"Found (?P<series>\d+) Series that need processing in (?P<ms>\d+) ms$")
PROCESSING_RE = re.compile(r"Processing series (?P<series>.+) with (?P<files>\d+) files$")
START_ALL_RE = re.compile(r"Starting Scan of All Libraries, Forced: (?P<forced>true|false)", re.IGNORECASE)
TIMESTAMP_RE = re.compile(r"^\[Kavita\] \[(?P<timestamp>\d{4}-\d{2}-\d{2} [^\]]+)\]")
REQUEST_RE = re.compile(
    r"HTTP (?P<method>[A-Z]+) (?P<target>\"[^\"]+\"|\S+) responded "
    r"(?P<status>\d+) in (?P<ms>\d+(?:\.\d+)?) ms"
)


@dataclass
class SeriesUpdate:
    name: str
    ms: int
    files: int | None = None


@dataclass
class LibraryScan:
    library: str
    start: str | None = None
    forced: bool | None = None
    found_series: int | None = None
    found_ms: int | None = None
    scan_ms: int | None = None
    total_ms: int | None = None
    finished_files: int | None = None
    finished_series: int | None = None
    no_changes: bool = False
    empty_folder_logs: int = 0
    updates: list[SeriesUpdate] = field(default_factory=list)


@dataclass
class RequestTiming:
    timestamp: str | None
    method: str
    endpoint: str
    status: int
    ms: float
    chapter_id: str | None = None
    page: str | None = None


def timestamp_from_line(line: str) -> str | None:
    match = TIMESTAMP_RE.search(line)
    if match:
        return match.group("timestamp")
    return None


def anonymize(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:12]


def sanitize_endpoint(path: str) -> str:
    parts = [":id" if part.isdigit() else part for part in path.split("/")]
    return "/".join(parts)


def percentile(values: list[int], percent: float) -> int | None:
    if not values:
        return None
    ordered = sorted(values)
    index = round((len(ordered) - 1) * percent)
    return ordered[index]


def parse_logs(paths: list[Path]) -> list[LibraryScan]:
    scans: list[LibraryScan] = []
    current: LibraryScan | None = None
    forced: bool | None = None
    pending_files_by_series: dict[str, int] = {}

    for path in paths:
        with path.open("r", encoding="utf-8", errors="ignore") as handle:
            for line in handle:
                line = line.rstrip("\n")

                start_all = START_ALL_RE.search(line)
                if start_all:
                    forced = start_all.group("forced").lower() == "true"

                begin = BEGIN_LIBRARY_RE.search(line)
                if begin:
                    current = LibraryScan(
                        library=begin.group("library"),
                        start=timestamp_from_line(line),
                        forced=forced,
                    )
                    scans.append(current)
                    pending_files_by_series = {}
                    continue

                if current is None:
                    continue

                if EMPTY_RE.search(line):
                    current.empty_folder_logs += 1
                    continue

                found = FOUND_RE.search(line)
                if found:
                    current.found_series = int(found.group("series"))
                    current.found_ms = int(found.group("ms"))
                    continue

                processing = PROCESSING_RE.search(line)
                if processing:
                    pending_files_by_series[processing.group("series")] = int(processing.group("files"))
                    continue

                finished_series = FINISHED_SERIES_RE.search(line)
                if finished_series:
                    name = finished_series.group("series")
                    current.updates.append(
                        SeriesUpdate(
                            name=name,
                            ms=int(finished_series.group("ms")),
                            files=pending_files_by_series.get(name),
                        )
                    )
                    continue

                finished_scan = FINISHED_SCAN_RE.search(line)
                if finished_scan:
                    current.scan_ms = int(finished_scan.group("ms"))
                    continue

                finished_library = FINISHED_LIBRARY_RE.search(line)
                if finished_library:
                    current.total_ms = int(finished_library.group("ms"))
                    current.finished_files = (
                        int(finished_library.group("files"))
                        if finished_library.group("files") is not None
                        else None
                    )
                    current.finished_series = int(finished_library.group("series"))
                    current.no_changes = "There were no changes" in line
                    current.library = finished_library.group("library")
                    current = None
                    pending_files_by_series = {}

    return scans


def parse_request_logs(paths: list[Path]) -> list[RequestTiming]:
    requests: list[RequestTiming] = []
    for path in paths:
        with path.open("r", encoding="utf-8", errors="ignore") as handle:
            for line in handle:
                request = REQUEST_RE.search(line)
                if not request:
                    continue

                target = request.group("target").strip('"')
                parsed = urlsplit(target)
                query = parse_qs(parsed.query)
                requests.append(
                    RequestTiming(
                        timestamp=timestamp_from_line(line),
                        method=request.group("method"),
                        endpoint=sanitize_endpoint(parsed.path),
                        status=int(request.group("status")),
                        ms=float(request.group("ms")),
                        chapter_id=(query.get("chapterId") or [None])[0],
                        page=(query.get("page") or [None])[0],
                    )
                )
    return requests


def scan_to_dict(
    scan: LibraryScan,
    show_library_names: bool,
    show_series_names: bool,
    slow_limit: int,
) -> dict[str, object]:
    update_ms = [update.ms for update in scan.updates]
    slow_updates = sorted(scan.updates, key=lambda update: update.ms, reverse=True)[:slow_limit]
    processed_files = sum(update.files or 0 for update in scan.updates)
    result: dict[str, object] = {
        "start": scan.start,
        "library_key": anonymize(scan.library),
        "forced": scan.forced,
        "found_series": scan.found_series,
        "found_ms": scan.found_ms,
        "processed_series": len(scan.updates),
        "processed_files": processed_files,
        "series_update_ms_sum": sum(update_ms),
        "series_update_ms_p50": int(median(update_ms)) if update_ms else None,
        "series_update_ms_p95": percentile(update_ms, 0.95),
        "series_update_ms_max": max(update_ms) if update_ms else None,
        "scan_ms": scan.scan_ms,
        "total_ms": scan.total_ms,
        "finished_files": scan.finished_files,
        "finished_series": scan.finished_series,
        "no_changes": scan.no_changes,
        "empty_folder_logs": scan.empty_folder_logs,
        "slow_series": [],
    }
    if show_library_names:
        result["library"] = scan.library
    for update in slow_updates:
        item: dict[str, object] = {
            "series_key": anonymize(update.name),
            "ms": update.ms,
            "files": update.files,
        }
        if show_series_names:
            item["series_name"] = update.name
        result["slow_series"].append(item)
    return result


def request_to_dict(request: RequestTiming, show_request_ids: bool) -> dict[str, object]:
    item: dict[str, object] = {
        "timestamp": request.timestamp,
        "method": request.method,
        "endpoint": request.endpoint,
        "status": request.status,
        "ms": request.ms,
    }
    if request.chapter_id is not None:
        item["chapter_key"] = anonymize(request.chapter_id)
    if request.page is not None:
        item["page"] = request.page
    if show_request_ids:
        item["chapter_id"] = request.chapter_id
    return item


def summarize_requests(
    requests: list[RequestTiming],
    slow_request_ms: float,
    slow_limit: int,
    show_request_ids: bool,
) -> dict[str, object]:
    slow = [request for request in requests if request.ms >= slow_request_ms]
    by_endpoint: dict[str, dict[str, object]] = {}
    for request in slow:
        key = f"{request.method} {request.endpoint}"
        item = by_endpoint.setdefault(
            key,
            {"endpoint": key, "count": 0, "max_ms": 0.0, "total_ms": 0.0},
        )
        item["count"] = int(item["count"]) + 1
        item["max_ms"] = max(float(item["max_ms"]), request.ms)
        item["total_ms"] = float(item["total_ms"]) + request.ms

    for item in by_endpoint.values():
        count = int(item["count"])
        item["avg_ms"] = round(float(item["total_ms"]) / count, 3) if count else 0
        item["max_ms"] = round(float(item["max_ms"]), 3)
        item["total_ms"] = round(float(item["total_ms"]), 3)

    return {
        "request_count": len(requests),
        "slow_request_ms": slow_request_ms,
        "slow_request_count": len(slow),
        "slowest_requests": [
            request_to_dict(request, show_request_ids)
            for request in sorted(slow, key=lambda item: item.ms, reverse=True)[:slow_limit]
        ],
        "slow_by_endpoint": sorted(
            by_endpoint.values(),
            key=lambda item: (int(item["count"]), float(item["max_ms"])),
            reverse=True,
        ),
    }


def scan_health(scans: list[dict[str, object]]) -> dict[str, object]:
    finished = [scan for scan in scans if scan.get("total_ms") is not None]
    non_forced = [scan for scan in finished if scan.get("forced") is not True]
    forced = [scan for scan in finished if scan.get("forced") is True]
    no_change = [scan for scan in finished if scan.get("no_changes") is True]
    non_forced_churn = [
        scan for scan in non_forced
        if int(scan.get("processed_series") or 0) > 0
    ]
    no_change_with_processing = [
        scan for scan in no_change
        if int(scan.get("processed_series") or 0) > 0
    ]

    return {
        "scan_count": len(scans),
        "finished_scan_count": len(finished),
        "forced_scan_count": len(forced),
        "non_forced_scan_count": len(non_forced),
        "no_change_scan_count": len(no_change),
        "non_forced_churn_scan_count": len(non_forced_churn),
        "no_change_with_processing_count": len(no_change_with_processing),
        "processed_series_sum": sum(int(scan.get("processed_series") or 0) for scan in finished),
        "processed_files_sum": sum(int(scan.get("processed_files") or 0) for scan in finished),
        "non_forced_processed_series_sum": sum(int(scan.get("processed_series") or 0) for scan in non_forced),
        "non_forced_processed_files_sum": sum(int(scan.get("processed_files") or 0) for scan in non_forced),
        "found_series_sum": sum(int(scan.get("found_series") or 0) for scan in finished),
        "total_ms_sum": sum(int(scan.get("total_ms") or 0) for scan in finished),
        "total_ms_max": max((int(scan.get("total_ms") or 0) for scan in finished), default=0),
        "found_ms_sum": sum(int(scan.get("found_ms") or 0) for scan in finished),
        "series_update_ms_sum": sum(int(scan.get("series_update_ms_sum") or 0) for scan in finished),
        "empty_folder_logs_sum": sum(int(scan.get("empty_folder_logs") or 0) for scan in finished),
    }


def gate_line(status: str, name: str, details: dict[str, object]) -> None:
    print({
        "status": status,
        "gate": name,
        **details,
    })


def print_scan_json_comparison(before_path: str, after_payload: list[dict[str, object]]) -> None:
    with open(before_path, "r", encoding="utf-8") as handle:
        before_payload = json.load(handle)
    before = scan_health(before_payload)
    after = scan_health(after_payload)
    print("\n## scan baseline comparison")
    print({"before": before, "after": after})


def print_scan_postflight_gates(before_path: str, after_payload: list[dict[str, object]]) -> bool:
    with open(before_path, "r", encoding="utf-8") as handle:
        before_payload = json.load(handle)
    before = scan_health(before_payload)
    after = scan_health(after_payload)
    failed = False

    print("\n## scan postflight gates")
    no_change_after = int(after["no_change_with_processing_count"])
    if no_change_after == 0:
        gate_line("PASS", "no-change scans have no processing", {"after": no_change_after})
    else:
        gate_line("FAIL", "no-change scans processed series", {"after": no_change_after})
        failed = True

    before_churn = int(before["non_forced_processed_series_sum"])
    after_churn = int(after["non_forced_processed_series_sum"])
    if after_churn < before_churn:
        gate_line("PASS", "non-forced processed series decreased", {"before": before_churn, "after": after_churn})
    elif after_churn == before_churn:
        gate_line("WARN", "non-forced processed series unchanged", {"before": before_churn, "after": after_churn})
    else:
        gate_line("FAIL", "non-forced processed series increased", {"before": before_churn, "after": after_churn})
        failed = True

    before_scans = int(before["non_forced_churn_scan_count"])
    after_scans = int(after["non_forced_churn_scan_count"])
    if after_scans < before_scans:
        gate_line("PASS", "non-forced churn scan count decreased", {"before": before_scans, "after": after_scans})
    elif after_scans == before_scans:
        gate_line("WARN", "non-forced churn scan count unchanged", {"before": before_scans, "after": after_scans})
    else:
        gate_line("FAIL", "non-forced churn scan count increased", {"before": before_scans, "after": after_scans})
        failed = True

    gate_line("PASS", "scan timing metrics recorded", {
        "before_total_ms_sum": before["total_ms_sum"],
        "after_total_ms_sum": after["total_ms_sum"],
        "before_found_ms_sum": before["found_ms_sum"],
        "after_found_ms_sum": after["found_ms_sum"],
        "before_series_update_ms_sum": before["series_update_ms_sum"],
        "after_series_update_ms_sum": after["series_update_ms_sum"],
    })
    return failed


def print_table(scans: list[LibraryScan], show_library_names: bool) -> None:
    library_column = "library" if show_library_names else "library_key"
    print(f"start\t{library_column}\tforced\tfound\tprocessed\tproc_files\tfound_ms\ttotal_ms\tempty\tno_changes")
    for scan in scans:
        print(
            f"{scan.start or ''}\t"
            f"{scan.library if show_library_names else anonymize(scan.library)}\t"
            f"{scan.forced}\t"
            f"{scan.found_series if scan.found_series is not None else ''}\t"
            f"{len(scan.updates)}\t"
            f"{sum(update.files or 0 for update in scan.updates)}\t"
            f"{scan.found_ms if scan.found_ms is not None else ''}\t"
            f"{scan.total_ms if scan.total_ms is not None else ''}\t"
            f"{scan.empty_folder_logs}\t"
            f"{scan.no_changes}"
        )


def print_summary(
    scans: list[LibraryScan],
    requests: list[RequestTiming],
    show_library_names: bool,
    show_series_names: bool,
    show_request_ids: bool,
    slow_limit: int,
    slow_request_ms: float,
) -> None:
    print("## scan log summary")
    print({"library_scan_count": len(scans)})

    finished = [scan for scan in scans if scan.total_ms is not None]
    slowest = sorted(finished, key=lambda scan: scan.total_ms or 0, reverse=True)[:10]
    print("\n## slowest library scans")
    for scan in slowest:
        print(scan_to_dict(scan, show_library_names, show_series_names, slow_limit))

    by_library: dict[str, list[LibraryScan]] = {}
    for scan in finished:
        by_library.setdefault(scan.library, []).append(scan)

    print("\n## aggregate by library")
    for library in sorted(by_library):
        items = by_library[library]
        total_values = [item.total_ms or 0 for item in items]
        found_values = [item.found_series or 0 for item in items]
        processed_values = [len(item.updates) for item in items]
        item = {
            "library_key": anonymize(library),
            "scans": len(items),
            "total_ms_sum": sum(total_values),
            "total_ms_max": max(total_values) if total_values else 0,
            "found_series_sum": sum(found_values),
            "processed_series_sum": sum(processed_values),
            "empty_folder_logs_sum": sum(item.empty_folder_logs for item in items),
        }
        if show_library_names:
            item["library"] = library
        print(item)

    print("\n## slow request summary")
    request_summary = summarize_requests(requests, slow_request_ms, slow_limit, show_request_ids)
    for key in ("request_count", "slow_request_ms", "slow_request_count"):
        print({key: request_summary[key]})

    print("\n## slowest requests")
    for item in request_summary["slowest_requests"]:
        print(item)

    print("\n## slow requests by endpoint")
    for item in request_summary["slow_by_endpoint"]:
        print(item)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("logs", nargs="+", type=Path, help="Kavita log file(s)")
    parser.add_argument("--json-output", help="Write machine-readable summary to this JSON file")
    parser.add_argument("--table", action="store_true", help="Print a compact tab-separated table")
    parser.add_argument("--show-library-names", action="store_true", help="Include raw library names in output")
    parser.add_argument("--show-series-names", action="store_true", help="Include raw series names in slow-series output")
    parser.add_argument("--show-request-ids", action="store_true", help="Include raw chapter ids in slow request output")
    parser.add_argument("--slow-limit", type=int, default=5, help="Slow series entries to keep per scan")
    parser.add_argument("--slow-request-ms", type=float, default=1000.0, help="HTTP request threshold for slow request summary")
    parser.add_argument("--request-json-output", help="Write machine-readable slow request summary to this JSON file")
    parser.add_argument("--compare-json", help="Compare current scan summary with a previous scan JSON")
    parser.add_argument("--postflight-gates", action="store_true", help="Print PASS/WARN/FAIL gates for a --compare-json run")
    parser.add_argument("--fail-on-gate-failure", action="store_true", help="Exit non-zero if any --postflight-gates check fails")
    args = parser.parse_args()
    if args.postflight_gates and not args.compare_json:
        parser.error("--postflight-gates requires --compare-json")
    if args.fail_on_gate_failure and not args.postflight_gates:
        parser.error("--fail-on-gate-failure requires --postflight-gates")

    scans = parse_logs(args.logs)
    requests = parse_request_logs(args.logs)
    scan_payload = [
        scan_to_dict(scan, args.show_library_names, args.show_series_names, args.slow_limit)
        for scan in scans
    ]
    if args.table:
        print_table(scans, args.show_library_names)
    else:
        print_summary(
            scans,
            requests,
            args.show_library_names,
            args.show_series_names,
            args.show_request_ids,
            args.slow_limit,
            args.slow_request_ms,
        )

    if args.json_output:
        with open(args.json_output, "w", encoding="utf-8") as handle:
            json.dump(scan_payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        print(f"\nWrote JSON summary: {args.json_output}")

    if args.request_json_output:
        payload = summarize_requests(requests, args.slow_request_ms, args.slow_limit, args.show_request_ids)
        with open(args.request_json_output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        print(f"\nWrote request JSON summary: {args.request_json_output}")

    if args.compare_json:
        print_scan_json_comparison(args.compare_json, scan_payload)
    if args.postflight_gates:
        failed = print_scan_postflight_gates(args.compare_json, scan_payload)
        if failed and args.fail_on_gate_failure:
            raise SystemExit(1)


if __name__ == "__main__":
    main()
