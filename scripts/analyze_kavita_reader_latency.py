#!/usr/bin/env python3
"""Correlate slow Kavita reader requests with DB file and cache state.

This is a read-only helper. It parses Kavita HTTP request logs, looks up slow
reader chapter requests in kavita.db, and checks whether the chapter cache
folder exists. Output is anonymized by default so it can be shared without
exposing titles, paths, or raw ids.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from summarize_kavita_scan_logs import anonymize, parse_request_logs


@dataclass
class ChapterFileInfo:
    chapter_id: str
    chapter_pages: int
    library_id: int
    library_name: str
    file_count: int
    file_bytes: int
    file_pages: int
    formats: list[int]
    extensions: list[str]


def mb(size: int) -> float:
    return round(size / 1024 / 1024, 3)


def cache_state(cache_dir: Path, chapter_id: str) -> dict[str, object]:
    path = cache_dir / chapter_id
    if not path.exists():
        return {
            "cache_exists": False,
            "cache_file_count": 0,
            "cache_bytes": 0,
            "cache_mb": 0,
        }

    files = [item for item in path.rglob("*") if item.is_file()]
    size = sum(item.stat().st_size for item in files)
    return {
        "cache_exists": True,
        "cache_file_count": len(files),
        "cache_bytes": size,
        "cache_mb": mb(size),
    }


def load_chapter_info(db: Path, chapter_ids: Iterable[str]) -> dict[str, ChapterFileInfo]:
    ids = sorted({int(item) for item in chapter_ids if item and item.isdigit()})
    if not ids:
        return {}

    placeholders = ",".join("?" for _ in ids)
    query = f"""
        SELECT
            c.Id AS ChapterId,
            c.Pages AS ChapterPages,
            mf.Format AS Format,
            COALESCE(mf.Extension, '') AS Extension,
            mf.Pages AS FilePages,
            mf.Bytes AS Bytes,
            s.LibraryId AS LibraryId,
            COALESCE(l.Name, '') AS LibraryName
        FROM Chapter c
        JOIN MangaFile mf ON mf.ChapterId = c.Id
        JOIN Volume v ON v.Id = c.VolumeId
        JOIN Series s ON s.Id = v.SeriesId
        JOIN Library l ON l.Id = s.LibraryId
        WHERE c.Id IN ({placeholders})
    """

    grouped: dict[str, dict[str, object]] = {}
    with sqlite3.connect(f"file:{db}?mode=ro", uri=True) as conn:
        conn.row_factory = sqlite3.Row
        for row in conn.execute(query, ids):
            chapter_id = str(row["ChapterId"])
            item = grouped.setdefault(
                chapter_id,
                {
                    "chapter_pages": int(row["ChapterPages"]),
                    "library_id": int(row["LibraryId"]),
                    "library_name": row["LibraryName"],
                    "file_count": 0,
                    "file_bytes": 0,
                    "file_pages": 0,
                    "formats": set(),
                    "extensions": set(),
                },
            )
            item["file_count"] = int(item["file_count"]) + 1
            item["file_bytes"] = int(item["file_bytes"]) + int(row["Bytes"] or 0)
            item["file_pages"] = int(item["file_pages"]) + int(row["FilePages"] or 0)
            item["formats"].add(int(row["Format"]))
            if row["Extension"]:
                item["extensions"].add(row["Extension"])

    result: dict[str, ChapterFileInfo] = {}
    for chapter_id, item in grouped.items():
        result[chapter_id] = ChapterFileInfo(
            chapter_id=chapter_id,
            chapter_pages=int(item["chapter_pages"]),
            library_id=int(item["library_id"]),
            library_name=str(item["library_name"]),
            file_count=int(item["file_count"]),
            file_bytes=int(item["file_bytes"]),
            file_pages=int(item["file_pages"]),
            formats=sorted(item["formats"]),
            extensions=sorted(item["extensions"]),
        )
    return result


def build_item(
    request,
    info: ChapterFileInfo | None,
    cache_dir: Path,
    show_library_names: bool,
    show_request_ids: bool,
) -> dict[str, object]:
    item: dict[str, object] = {
        "timestamp": request.timestamp,
        "method": request.method,
        "endpoint": request.endpoint,
        "status": request.status,
        "ms": request.ms,
        "page": request.page,
    }

    if request.chapter_id:
        item["chapter_key"] = anonymize(request.chapter_id)
        if show_request_ids:
            item["chapter_id"] = request.chapter_id

    if info is None:
        item["db_match"] = False
        return item

    item.update(
        {
            "db_match": True,
            "library_key": anonymize(info.library_name or str(info.library_id)),
            "chapter_pages": info.chapter_pages,
            "file_count": info.file_count,
            "file_bytes": info.file_bytes,
            "file_mb": mb(info.file_bytes),
            "file_pages": info.file_pages,
            "formats": info.formats,
            "extensions": info.extensions,
        }
    )
    if show_library_names:
        item["library_id"] = info.library_id
        item["library_name"] = info.library_name

    item.update(cache_state(cache_dir, info.chapter_id))
    return item


def summarize(items: list[dict[str, object]]) -> dict[str, object]:
    by_endpoint: dict[str, dict[str, object]] = {}
    by_extension: dict[str, dict[str, object]] = {}
    cache_hits = 0
    cache_misses = 0

    for item in items:
        endpoint = f"{item['method']} {item['endpoint']}"
        endpoint_item = by_endpoint.setdefault(endpoint, {"endpoint": endpoint, "count": 0, "max_ms": 0.0})
        endpoint_item["count"] = int(endpoint_item["count"]) + 1
        endpoint_item["max_ms"] = max(float(endpoint_item["max_ms"]), float(item["ms"]))

        if item.get("cache_exists"):
            cache_hits += 1
        else:
            cache_misses += 1

        extensions = item.get("extensions")
        if isinstance(extensions, list):
            for ext in extensions:
                ext_item = by_extension.setdefault(ext, {"extension": ext, "count": 0, "max_ms": 0.0, "file_mb_sum": 0.0})
                ext_item["count"] = int(ext_item["count"]) + 1
                ext_item["max_ms"] = max(float(ext_item["max_ms"]), float(item["ms"]))
                ext_item["file_mb_sum"] = round(float(ext_item["file_mb_sum"]) + float(item.get("file_mb") or 0), 3)

    return {
        "slow_reader_request_count": len(items),
        "cache_exists_count": cache_hits,
        "cache_missing_count": cache_misses,
        "by_endpoint": sorted(by_endpoint.values(), key=lambda item: (int(item["count"]), float(item["max_ms"])), reverse=True),
        "by_extension": sorted(by_extension.values(), key=lambda item: (int(item["count"]), float(item["max_ms"])), reverse=True),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("logs", nargs="+", type=Path, help="Kavita log file(s)")
    parser.add_argument("--db", required=True, type=Path, help="Path to kavita.db")
    parser.add_argument("--cache-dir", required=True, type=Path, help="Path to Kavita config/cache directory")
    parser.add_argument("--slow-request-ms", type=float, default=3000.0, help="HTTP request threshold")
    parser.add_argument("--limit", type=int, default=20, help="Slow reader requests to include")
    parser.add_argument("--json-output", type=Path, help="Write machine-readable summary")
    parser.add_argument("--show-library-names", action="store_true", help="Include raw library names and ids")
    parser.add_argument("--show-request-ids", action="store_true", help="Include raw chapter ids")
    args = parser.parse_args()

    requests = [
        request
        for request in parse_request_logs(args.logs)
        if request.ms >= args.slow_request_ms
        and request.chapter_id
        and request.endpoint.startswith("/api/reader/")
    ]
    requests = sorted(requests, key=lambda request: request.ms, reverse=True)[: args.limit]
    infos = load_chapter_info(args.db, [request.chapter_id for request in requests if request.chapter_id])
    items = [
        build_item(
            request,
            infos.get(request.chapter_id or ""),
            args.cache_dir,
            args.show_library_names,
            args.show_request_ids,
        )
        for request in requests
    ]
    payload = {"summary": summarize(items), "slow_reader_requests": items}

    print("## reader latency correlation")
    print(payload["summary"])
    print("\n## slow reader requests")
    for item in items:
        print(item)

    if args.json_output:
        with args.json_output.open("w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        print(f"\nWrote JSON summary: {args.json_output}")


if __name__ == "__main__":
    main()
