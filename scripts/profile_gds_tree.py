#!/usr/bin/env python3
"""Profile read-only GDS/rclone tree traversal.

This helps separate Kavita scanner work from filesystem discovery latency. By
default it redacts path names and prints stable path hashes so output can be
shared without exposing library contents.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import time
from dataclasses import dataclass, field
from pathlib import Path


def stable_key(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()[:12]


def display_path(path: Path, root: Path, show_names: bool) -> str:
    try:
        rel = path.relative_to(root)
    except ValueError:
        rel = path
    text = "." if str(rel) == "." else str(rel)
    return text if show_names else stable_key(text)


@dataclass
class WalkStats:
    dirs: int = 0
    files: int = 0
    errors: int = 0
    bytes: int = 0
    scandir_calls: int = 0
    slowest_scandir_ms: float = 0.0
    slowest_scandir_path: Path | None = None
    ext_counts: collections.Counter[str] = field(default_factory=collections.Counter)
    error_counts: collections.Counter[str] = field(default_factory=collections.Counter)
    max_depth_reached: int = 0

    def add(self, other: "WalkStats") -> None:
        self.dirs += other.dirs
        self.files += other.files
        self.errors += other.errors
        self.bytes += other.bytes
        self.scandir_calls += other.scandir_calls
        self.ext_counts.update(other.ext_counts)
        self.error_counts.update(other.error_counts)
        self.max_depth_reached = max(self.max_depth_reached, other.max_depth_reached)
        if other.slowest_scandir_ms > self.slowest_scandir_ms:
            self.slowest_scandir_ms = other.slowest_scandir_ms
            self.slowest_scandir_path = other.slowest_scandir_path


def list_dir(path: Path, stats: WalkStats) -> list[os.DirEntry[str]]:
    started = time.monotonic()
    try:
        with os.scandir(path) as handle:
            entries = list(handle)
    except OSError as exc:
        stats.errors += 1
        stats.error_counts[type(exc).__name__] += 1
        return []
    elapsed_ms = (time.monotonic() - started) * 1000
    stats.scandir_calls += 1
    if elapsed_ms > stats.slowest_scandir_ms:
        stats.slowest_scandir_ms = elapsed_ms
        stats.slowest_scandir_path = path
    return entries


def walk_tree(
    path: Path,
    root: Path,
    max_depth: int | None,
    started_at: float,
    time_limit: float | None,
) -> WalkStats:
    stats = WalkStats()
    stack: list[tuple[Path, int]] = [(path, 0)]

    while stack:
        if time_limit is not None and time.monotonic() - started_at >= time_limit:
            stats.error_counts["TimeLimitReached"] += 1
            break

        current, depth = stack.pop()
        stats.max_depth_reached = max(stats.max_depth_reached, depth)
        entries = list_dir(current, stats)
        for entry in entries:
            entry_path = Path(entry.path)
            try:
                if entry.is_dir(follow_symlinks=False):
                    stats.dirs += 1
                    if max_depth is None or depth + 1 < max_depth:
                        stack.append((entry_path, depth + 1))
                elif entry.is_file(follow_symlinks=False):
                    stats.files += 1
                    ext = entry_path.suffix.lower() or "(none)"
                    stats.ext_counts[ext] += 1
                    try:
                        stats.bytes += entry.stat(follow_symlinks=False).st_size
                    except OSError as exc:
                        stats.errors += 1
                        stats.error_counts[type(exc).__name__] += 1
                else:
                    stats.errors += 1
                    stats.error_counts["other-entry-type"] += 1
            except OSError as exc:
                stats.errors += 1
                stats.error_counts[type(exc).__name__] += 1

    return stats


def stats_to_dict(stats: WalkStats, root: Path, show_names: bool) -> dict[str, object]:
    return {
        "dirs": stats.dirs,
        "files": stats.files,
        "errors": stats.errors,
        "bytes": stats.bytes,
        "scandir_calls": stats.scandir_calls,
        "slowest_scandir_ms": round(stats.slowest_scandir_ms, 3),
        "slowest_scandir_path": (
            display_path(stats.slowest_scandir_path, root, show_names)
            if stats.slowest_scandir_path is not None
            else None
        ),
        "max_depth_reached": stats.max_depth_reached,
        "top_extensions": dict(stats.ext_counts.most_common(12)),
        "error_counts": dict(stats.error_counts),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Profile read-only GDS/rclone tree traversal")
    parser.add_argument("root", type=Path, help="Root directory to profile")
    parser.add_argument("--max-depth", type=int, default=None, help="Maximum depth below each top-level child")
    parser.add_argument("--time-limit", type=float, default=None, help="Stop after this many seconds")
    parser.add_argument("--show-names", action="store_true", help="Print real path names instead of stable hashes")
    parser.add_argument("--sort-by", choices=["elapsed", "files", "dirs", "path"], default="elapsed")
    args = parser.parse_args()

    root = args.root
    started_at = time.monotonic()
    root_stats = WalkStats()
    root_entries = list_dir(root, root_stats)
    children = sorted((Path(entry.path) for entry in root_entries), key=lambda item: str(item).casefold())

    total = WalkStats()
    total.add(root_stats)
    rows: list[dict[str, object]] = []

    for child in children:
        if args.time_limit is not None and time.monotonic() - started_at >= args.time_limit:
            break
        child_started = time.monotonic()
        child_stats = walk_tree(child, root, args.max_depth, started_at, args.time_limit)
        elapsed_ms = (time.monotonic() - child_started) * 1000
        total.add(child_stats)
        row = {
            "path": display_path(child, root, args.show_names),
            "elapsed_ms": round(elapsed_ms, 3),
            **stats_to_dict(child_stats, root, args.show_names),
        }
        rows.append(row)
        print(json.dumps({"child": row}, ensure_ascii=False, sort_keys=True), flush=True)

    if args.sort_by == "elapsed":
        rows.sort(key=lambda row: float(row["elapsed_ms"]), reverse=True)
    elif args.sort_by == "files":
        rows.sort(key=lambda row: int(row["files"]), reverse=True)
    elif args.sort_by == "dirs":
        rows.sort(key=lambda row: int(row["dirs"]), reverse=True)
    else:
        rows.sort(key=lambda row: str(row["path"]))

    elapsed_total_ms = (time.monotonic() - started_at) * 1000
    summary = {
        "root": display_path(root, root, args.show_names),
        "elapsed_ms": round(elapsed_total_ms, 3),
        "child_count": len(children),
        "profiled_child_count": len(rows),
        "time_limit_reached": args.time_limit is not None and time.monotonic() - started_at >= args.time_limit,
        **stats_to_dict(total, root, args.show_names),
        "slowest_children": rows[:10],
    }
    print(json.dumps({"summary": summary}, ensure_ascii=False, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
