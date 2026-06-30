#!/usr/bin/env python3
"""Read-only Kavita-GDS scanner diagnostics.

The script never writes to the Kavita database or media folders. It summarizes
the failure modes that matter most for GDS scans:

- Pages=0 media rows
- duplicate MangaFile.FilePath rows
- MediaError distribution
- SQLite foreign key violations
- duplicate cleanup candidate classification
- MediaError cause classification
- optional archive validation for Pages=0 archives
- optional source-cover/config-cache risk classification
- optional TXT source-cover/config-cache classification
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sqlite3
import sys
import zipfile

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif"}
ARCHIVE_EXTENSIONS = {".zip", ".cbz", ".rar", ".cbr", ".7z", ".7zip", ".cb7", ".tar.gz", ".cbt"}
COVER_FILE_NAMES = ("cover.jpg", "cover.jpeg", "cover.png", "cover.webp")
METADATA_FILE_NAMES = ("kavita.yaml", "kavita.yml")
SERVER_SETTING_KEYS = {
    1: "CacheDirectory",
    3: "LoggingLevel",
    4: "Port",
    5: "BackupDirectory",
    9: "BaseUrl",
    11: "InstallVersion",
    12: "BookmarkDirectory",
    17: "EnableFolderWatching",
    18: "TotalLogs",
    21: "IpAddresses",
    22: "EncodeMediaAs",
    24: "CacheSize",
    27: "CoverImageSize",
    38: "FirstInstallDate",
    39: "FirstInstallVersion",
    41: "PdfRenderResolution",
}
CORE_TABLES = (
    "Library",
    "LibraryFileTypeGroup",
    "FolderPath",
    "Series",
    "Volume",
    "Chapter",
    "MangaFile",
    "MediaError",
    "ServerSetting",
    "ManualMigrationHistory",
    "__EFMigrationsHistory",
)


def connect_readonly(db_path: str) -> sqlite3.Connection:
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    return con


def mapped_path(path: str, container_root: str, host_root: str) -> str:
    prefix = container_root.rstrip("/") + "/"
    if path.startswith(prefix):
        return host_root.rstrip("/") + "/" + path[len(prefix):]
    return path


def candidate_metadata_dirs(file_path: str, folder_path: str | None = None) -> list[str]:
    candidates: list[str] = []
    if folder_path:
        candidates.append(folder_path)
    current = os.path.dirname(file_path)
    for _ in range(4):
        if not current or current == "/":
            break
        candidates.append(current)
        current = os.path.dirname(current)

    deduped: list[str] = []
    seen: set[str] = set()
    for item in candidates:
        if item in seen:
            continue
        seen.add(item)
        deduped.append(item)
    return deduped


def has_source_cover_hint(file_path: str, folder_path: str | None = None) -> tuple[bool, bool]:
    has_cover_file = False
    has_yaml_cover = False
    for directory in candidate_metadata_dirs(file_path, folder_path):
        try:
            names = set(os.listdir(directory))
        except OSError:
            continue

        lowered = {name.lower() for name in names}
        if any(name in lowered for name in COVER_FILE_NAMES):
            has_cover_file = True

        for metadata_name in METADATA_FILE_NAMES:
            if metadata_name not in lowered:
                continue
            metadata_path = os.path.join(directory, next(name for name in names if name.lower() == metadata_name))
            try:
                with open(metadata_path, "r", encoding="utf-8", errors="ignore") as handle:
                    for line in handle:
                        if line.strip().lower().startswith("cover:"):
                            has_yaml_cover = True
                            break
            except OSError:
                pass

    return has_cover_file, has_yaml_cover


def classify_yaml_cover_value(value: str | None) -> str:
    value = (value or "").strip().strip('"').strip("'")
    if not value:
        return "empty"
    if value.upper() == "TEXT":
        return "text-marker"
    if value.startswith(("http://", "https://")):
        return "url"
    if value.startswith("data:image/"):
        return "data-uri"
    if len(value) > 200 and re.fullmatch(r"[A-Za-z0-9+/=]+", value[:240] or ""):
        return "base64-like"
    return "other"


def read_yaml_cover_value(file_path: str, folder_path: str | None = None) -> str | None:
    for directory in candidate_metadata_dirs(file_path, folder_path):
        try:
            names = os.listdir(directory)
        except OSError:
            continue

        for metadata_name in METADATA_FILE_NAMES:
            metadata_path = next(
                (os.path.join(directory, name) for name in names if name.lower() == metadata_name),
                None,
            )
            if metadata_path is None:
                continue
            try:
                with open(metadata_path, "r", encoding="utf-8", errors="ignore") as handle:
                    for line in handle:
                        if line.strip().lower().startswith("cover:"):
                            return line.strip()[len("cover:"):].strip()
                return ""
            except OSError:
                return None

    return None


def print_rows(title: str, rows: list[sqlite3.Row]) -> None:
    print(f"\n## {title}")
    if not rows:
        print("(none)")
        return
    for row in rows:
        print(dict(row))


def rows_to_dicts(rows: list[sqlite3.Row]) -> list[dict[str, object]]:
    return [dict(row) for row in rows]


def table_exists(con: sqlite3.Connection, table_name: str) -> bool:
    return con.execute(
        "select 1 from sqlite_master where type = 'table' and name = ?",
        (table_name,),
    ).fetchone() is not None


def core_table_count_rows(con: sqlite3.Connection) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for table_name in CORE_TABLES:
        if not table_exists(con, table_name):
            rows.append({"Table": table_name, "Exists": False, "Rows": None})
            continue
        quoted = table_name.replace('"', '""')
        count = con.execute(f'select count(*) from "{quoted}"').fetchone()[0]
        rows.append({"Table": table_name, "Exists": True, "Rows": count})
    return rows


def ef_migration_history_rows(con: sqlite3.Connection) -> list[dict[str, object]]:
    if not table_exists(con, "__EFMigrationsHistory"):
        return []
    return rows_to_dicts(list(con.execute("""
        select MigrationId, ProductVersion
        from __EFMigrationsHistory
        order by MigrationId desc
        limit 12
    """)))


def ef_migration_summary(con: sqlite3.Connection) -> dict[str, object]:
    if not table_exists(con, "__EFMigrationsHistory"):
        return {"exists": False, "count": 0, "latest": None, "earliest": None}
    row = con.execute("""
        select count(*) as Count, min(MigrationId) as Earliest, max(MigrationId) as Latest
        from __EFMigrationsHistory
    """).fetchone()
    return {
        "exists": True,
        "count": row["Count"],
        "earliest": row["Earliest"],
        "latest": row["Latest"],
        "latest_rows": ef_migration_history_rows(con),
    }


def manual_migration_history_rows(con: sqlite3.Connection) -> list[dict[str, object]]:
    if not table_exists(con, "ManualMigrationHistory"):
        return []
    return rows_to_dicts(list(con.execute("""
        select Id, ProductVersion, Name, RanAt
        from ManualMigrationHistory
        order by Id desc
        limit 20
    """)))


def manual_migration_summary(con: sqlite3.Connection) -> dict[str, object]:
    if not table_exists(con, "ManualMigrationHistory"):
        return {"exists": False, "count": 0, "latest": None, "latest_rows": []}
    row = con.execute("""
        select count(*) as Count, max(Id) as Latest
        from ManualMigrationHistory
    """).fetchone()
    return {
        "exists": True,
        "count": row["Count"],
        "latest": row["Latest"],
        "latest_rows": manual_migration_history_rows(con),
    }


def server_setting_rows(con: sqlite3.Connection) -> list[dict[str, object]]:
    if not table_exists(con, "ServerSetting"):
        return []
    placeholders = ",".join("?" for _ in SERVER_SETTING_KEYS)
    rows = list(con.execute(f"""
        select Key, Value
        from ServerSetting
        where Key in ({placeholders})
        order by Key
    """, tuple(SERVER_SETTING_KEYS)))
    return [
        {
            "Key": row["Key"],
            "Name": SERVER_SETTING_KEYS.get(int(row["Key"]), str(row["Key"])),
            "Value": row["Value"],
        }
        for row in rows
    ]


def foreign_key_rows(con: sqlite3.Connection) -> list[sqlite3.Row]:
    return list(con.execute("pragma foreign_key_check"))


def library_rows(con: sqlite3.Connection) -> list[sqlite3.Row]:
    return list(con.execute("""
        select l.Id, l.Name, l.Type, l.EnableMetadata, l.AllowMetadataMatching, l.FolderWatching,
               count(distinct s.Id) as Series,
               count(distinct mf.Id) as Files,
               sum(case when mf.Pages = 0 then 1 else 0 end) as Pages0
        from Library l
        left join Series s on s.LibraryId = l.Id
        left join Volume v on v.SeriesId = s.Id
        left join Chapter c on c.VolumeId = v.Id
        left join MangaFile mf on mf.ChapterId = c.Id
        group by l.Id
        order by l.Id
    """))


def pages0_rows(con: sqlite3.Connection) -> list[sqlite3.Row]:
    return list(con.execute("""
        select l.Id as LibraryId, l.Name as LibraryName, lower(coalesce(mf.Extension, '')) as Ext, count(*) as Count
        from MangaFile mf
        join Chapter c on c.Id = mf.ChapterId
        join Volume v on v.Id = c.VolumeId
        join Series s on s.Id = v.SeriesId
        join Library l on l.Id = s.LibraryId
        where mf.Pages = 0
        group by l.Id, Ext
        order by l.Id, Count desc
    """))


def duplicate_file_path_rows(con: sqlite3.Connection) -> list[sqlite3.Row]:
    return list(con.execute("""
        with d as (
            select FilePath
            from MangaFile
            group by FilePath
            having count(*) > 1
        )
        select l.Id as LibraryId, l.Name as LibraryName, lower(coalesce(mf.Extension, '')) as Ext,
               count(distinct d.FilePath) as Groups,
               count(*) as RowRefs
        from d
        join MangaFile mf on mf.FilePath = d.FilePath
        join Chapter c on c.Id = mf.ChapterId
        join Volume v on v.Id = c.VolumeId
        join Series s on s.Id = v.SeriesId
        join Library l on l.Id = s.LibraryId
        group by l.Id, Ext
        order by l.Id, RowRefs desc
    """))


def media_error_rows(con: sqlite3.Connection, limit: int | None = None) -> list[sqlite3.Row]:
    query = """
        select lower(coalesce(Extension, '')) as Ext,
               substr(coalesce(Comment, ''), 1, 100) as Comment,
               count(*) as Count
        from MediaError
        group by Ext, Comment
        order by Count desc
    """
    if limit is not None:
        query += "\nlimit ?"
        return list(con.execute(query, (limit,)))
    return list(con.execute(query))


def classify_media_error(ext: str, comment: str, details: str) -> str:
    ext = (ext or "").lower()
    comment_lower = (comment or "").lower()
    details_lower = (details or "").lower()

    if "encrypted pdf" in comment_lower or "encryption not supported" in details_lower:
        return "pdf-encrypted"
    if ext == "pdf":
        return "pdf-malformed-or-metadata"

    if ext in ("cbz", "zip", "cbr", "rar"):
        if "cannot be read" in comment_lower or "not supported" in comment_lower:
            return "archive-unreadable-or-unsupported"
        return "archive-other"

    if ext == "epub":
        if "unable to parse any meaningful" in comment_lower:
            return "epub-unrecognized-by-parser"
        if "end of central directory" in details_lower:
            return "epub-not-a-valid-zip"
        if "container.xml" in details_lower:
            return "epub-missing-container"
        if "nav item not found" in details_lower:
            return "epub-missing-nav"
        if "manifest" in details_lower:
            return "epub-invalid-manifest"
        if "not found in the epub file" in details_lower or "does not exist" in details_lower:
            return "epub-missing-referenced-file"
        if "number of pages" in comment_lower:
            return "epub-page-count-failed"
        return "epub-other"

    if "unable to parse any meaningful" in comment_lower:
        return "scanner-unrecognized-file"
    return "other"


def media_error_classification_rows(con: sqlite3.Connection) -> list[dict[str, object]]:
    rows = list(con.execute("""
        select lower(coalesce(Extension, '')) as Ext,
               coalesce(Comment, '') as Comment,
               coalesce(Details, '') as Details,
               count(*) as Count
        from MediaError
        group by Ext, Comment, Details
    """))
    counter: collections.Counter[tuple[str, str]] = collections.Counter()
    for row in rows:
        ext = row["Ext"]
        category = classify_media_error(ext, row["Comment"], row["Details"])
        counter[(ext, category)] += int(row["Count"])

    return [
        {"Ext": ext, "Category": category, "Count": count}
        for (ext, category), count in sorted(counter.items(), key=lambda item: (-item[1], item[0]))
    ]


def duplicate_cleanup_candidate_rows(con: sqlite3.Connection) -> list[sqlite3.Row]:
    return list(con.execute("""
        with dup as (
            select mf.FilePath, count(*) rowrefs,
                   count(distinct s.Id) series_count,
                   count(distinct v.Id) volume_count,
                   count(distinct c.Id) chapter_count,
                   count(distinct coalesce(c.Range, '')) range_count,
                   count(distinct c.Pages) chapter_page_values,
                   min(l.Id) library_id,
                   min(l.Name) library_name,
                   lower(coalesce(mf.Extension, '')) ext
            from MangaFile mf
            join Chapter c on c.Id = mf.ChapterId
            join Volume v on v.Id = c.VolumeId
            join Series s on s.Id = v.SeriesId
            join Library l on l.Id = s.LibraryId
            where mf.FilePath is not null and mf.FilePath <> ''
            group by mf.FilePath
            having count(*) > 1
        ), classified as (
            select *,
                   case
                       when series_count = 1 and volume_count = 1 then 'same_series_same_volume'
                       when series_count = 1 then 'same_series_multi_volume'
                       else 'cross_series'
                   end as kind
            from dup
        )
        select library_id as LibraryId, library_name as LibraryName, ext as Ext, kind as Kind,
               count(*) as Groups,
               sum(rowrefs) as RowRefs,
               sum(case when chapter_page_values = 1 then 1 else 0 end) as SamePageGroups,
               sum(case when range_count = 1 then 1 else 0 end) as SameRangeGroups
        from classified
        group by library_id, library_name, ext, kind
        order by library_id, ext, kind
    """))


def summarize_foreign_keys(con: sqlite3.Connection) -> None:
    print("\n## foreign_key_check")
    try:
        rows = foreign_key_rows(con)
    except sqlite3.DatabaseError as exc:
        print(f"error: {exc}")
        return

    if not rows:
        print("(none)")
        return

    for row in rows[:40]:
        print(dict(row))
    if len(rows) > 40:
        print(f"... {len(rows) - 40} more")


def summarize_startup_state(con: sqlite3.Connection) -> None:
    print("\n## startup/migration state")
    print("core_table_counts")
    for row in core_table_count_rows(con):
        print(row)

    print("ef_migration_summary", ef_migration_summary(con))
    print("manual_migration_summary", manual_migration_summary(con))

    settings = server_setting_rows(con)
    print("server_settings")
    if not settings:
        print("(none)")
    for row in settings:
        print(row)


def summarize_db(con: sqlite3.Connection) -> None:
    print_rows("libraries", library_rows(con))
    print_rows("pages0 by library/ext", pages0_rows(con))
    print_rows("duplicate file paths by library/ext", duplicate_file_path_rows(con))
    print_rows("media errors by ext/comment", media_error_rows(con, limit=40))
    print("\n## media error classification")
    rows = media_error_classification_rows(con)
    if not rows:
        print("(none)")
    for row in rows:
        print(row)


def duplicate_structure_counter(con: sqlite3.Connection) -> collections.Counter[tuple[int, int, int, int, int, int, int]]:
    rows = list(con.execute("""
        with d as (
            select FilePath, count(*) as Cnt
            from MangaFile
            group by FilePath
            having count(*) > 1
        )
        select l.Id as LibraryId, mf.FilePath, mf.Pages, s.Id as SeriesId,
               v.Id as VolumeId, c.Id as ChapterId, c.Range as ChapterRange
        from d
        join MangaFile mf on mf.FilePath = d.FilePath
        join Chapter c on c.Id = mf.ChapterId
        join Volume v on v.Id = c.VolumeId
        join Series s on s.Id = v.SeriesId
        join Library l on l.Id = s.LibraryId
        order by l.Id, mf.FilePath
    """))
    by_path: dict[tuple[int, str], list[sqlite3.Row]] = collections.defaultdict(list)
    for row in rows:
        by_path[(row["LibraryId"], row["FilePath"])].append(row)

    summary: collections.Counter[tuple[int, int, int, int, int, int, int]] = collections.Counter()
    for (library_id, _), items in by_path.items():
        summary[(
            library_id,
            len(items),
            len({item["SeriesId"] for item in items}),
            len({item["VolumeId"] for item in items}),
            len({item["ChapterId"] for item in items}),
            len({item["ChapterRange"] for item in items}),
            len({item["Pages"] for item in items}),
        )] += 1
    return summary


def summarize_duplicate_structure(con: sqlite3.Connection) -> None:
    summary = duplicate_structure_counter(con)
    print("\n## duplicate structure")
    print("(LibraryId, RowRefs, Series, Volumes, Chapters, Ranges, PageValues) -> Groups")
    if not summary:
        print("(none)")
        return
    for key, count in sorted(summary.items()):
        print(f"{key} -> {count}")


def summarize_duplicate_cleanup_candidates(con: sqlite3.Connection) -> None:
    print_rows("duplicate cleanup candidates", duplicate_cleanup_candidate_rows(con))


def build_json_summary(
    con: sqlite3.Connection,
    container_root: str,
    host_root: str,
    include_archive_validation: bool,
    include_cover_validation: bool,
    check_cover_source_files: bool,
) -> dict[str, object]:
    integrity_check = con.execute("pragma integrity_check").fetchone()[0]
    duplicate_structure = [
        {
            "LibraryId": key[0],
            "RowRefs": key[1],
            "Series": key[2],
            "Volumes": key[3],
            "Chapters": key[4],
            "Ranges": key[5],
            "PageValues": key[6],
            "Groups": count,
        }
        for key, count in sorted(duplicate_structure_counter(con).items())
    ]
    summary: dict[str, object] = {
        "integrity_check": integrity_check,
        "foreign_key_check": rows_to_dicts(foreign_key_rows(con)),
        "core_table_counts": core_table_count_rows(con),
        "ef_migration_summary": ef_migration_summary(con),
        "manual_migration_summary": manual_migration_summary(con),
        "server_settings": server_setting_rows(con),
        "libraries": rows_to_dicts(library_rows(con)),
        "pages0_by_library_ext": rows_to_dicts(pages0_rows(con)),
        "duplicate_file_paths_by_library_ext": rows_to_dicts(duplicate_file_path_rows(con)),
        "duplicate_structure": duplicate_structure,
        "duplicate_cleanup_candidates": rows_to_dicts(duplicate_cleanup_candidate_rows(con)),
        "media_errors_by_ext_comment": rows_to_dicts(media_error_rows(con)),
        "media_error_classification": media_error_classification_rows(con),
    }
    if include_archive_validation:
        summary["pages0_archive_validation"] = pages0_archive_validation_rows(con, container_root, host_root)
    if include_cover_validation:
        summary["cover_source_cache_risk"] = cover_source_cache_rows(
            con, container_root, host_root, check_cover_source_files
        )
        summary["txt_cover_state"] = txt_cover_state_rows(
            con, container_root, host_root, check_cover_source_files
        )
    return summary


def row_key(row: dict[str, object], fields: tuple[str, ...]) -> tuple[object, ...]:
    return tuple(row.get(field) for field in fields)


def print_count_delta(
    title: str,
    before_rows: list[dict[str, object]],
    after_rows: list[dict[str, object]],
    key_fields: tuple[str, ...],
    count_fields: tuple[str, ...],
) -> None:
    before = {row_key(row, key_fields): row for row in before_rows}
    after = {row_key(row, key_fields): row for row in after_rows}
    keys = sorted(set(before) | set(after))

    print(f"\n## {title}")
    changed = False
    for key in keys:
        before_row = before.get(key, {})
        after_row = after.get(key, {})
        deltas: dict[str, int] = {}
        for field in count_fields:
            before_value = int(before_row.get(field) or 0)
            after_value = int(after_row.get(field) or 0)
            if after_value != before_value:
                deltas[field] = after_value - before_value
        if not deltas:
            continue
        changed = True
        print({
            "key": dict(zip(key_fields, key)),
            "before": {field: before_row.get(field, 0) for field in count_fields},
            "after": {field: after_row.get(field, 0) for field in count_fields},
            "delta": deltas,
        })
    if not changed:
        print("(no changes)")


def print_json_comparison(before_path: str, after: dict[str, object]) -> None:
    with open(before_path, "r", encoding="utf-8") as handle:
        before = json.load(handle)

    print("\n## baseline comparison")
    print({
        "integrity_check_before": before.get("integrity_check"),
        "integrity_check_after": after.get("integrity_check"),
        "foreign_key_violations_before": len(before.get("foreign_key_check", [])),
        "foreign_key_violations_after": len(after.get("foreign_key_check", [])),
        "ef_migration_latest_before": before.get("ef_migration_summary", {}).get("latest"),
        "ef_migration_latest_after": after.get("ef_migration_summary", {}).get("latest"),
        "manual_migration_count_before": before.get("manual_migration_summary", {}).get("count"),
        "manual_migration_count_after": after.get("manual_migration_summary", {}).get("count"),
    })

    print_count_delta(
        "pages0 delta",
        before.get("pages0_by_library_ext", []),
        after.get("pages0_by_library_ext", []),
        ("LibraryId", "LibraryName", "Ext"),
        ("Count",),
    )
    print_count_delta(
        "duplicate file path delta",
        before.get("duplicate_file_paths_by_library_ext", []),
        after.get("duplicate_file_paths_by_library_ext", []),
        ("LibraryId", "LibraryName", "Ext"),
        ("Groups", "RowRefs"),
    )
    print_count_delta(
        "duplicate cleanup candidate delta",
        before.get("duplicate_cleanup_candidates", []),
        after.get("duplicate_cleanup_candidates", []),
        ("LibraryId", "LibraryName", "Ext", "Kind"),
        ("Groups", "RowRefs", "SamePageGroups", "SameRangeGroups"),
    )
    print_count_delta(
        "pages0 archive validation delta",
        before.get("pages0_archive_validation", []),
        after.get("pages0_archive_validation", []),
        ("LibraryId", "Ext"),
        ("files", "exists", "readable", "images", "nested_archives", "missing", "errors"),
    )
    print_count_delta(
        "media error classification delta",
        before.get("media_error_classification", []),
        after.get("media_error_classification", []),
        ("Ext", "Category"),
        ("Count",),
    )
    print_count_delta(
        "cover source/cache delta",
        before.get("cover_source_cache_risk", []),
        after.get("cover_source_cache_risk", []),
        ("LibraryId", "HasSourceCover", "UsesExpectedCacheName"),
        ("Series",),
    )
    print_count_delta(
        "txt cover state delta",
        before.get("txt_cover_state", []),
        after.get("txt_cover_state", []),
        ("LibraryId", "LibraryName"),
        (
            "series",
            "files",
            "series_with_config_cover",
            "series_with_source_cover_file",
            "series_without_any_cover_hint",
        ),
    )


def sum_count(rows: list[dict[str, object]], field: str, kind: str | None = None) -> int:
    total = 0
    for row in rows:
        if kind is not None and row.get("Kind") != kind:
            continue
        total += int(row.get(field) or 0)
    return total


def gate_line(status: str, name: str, details: dict[str, object]) -> None:
    print({
        "status": status,
        "gate": name,
        **details,
    })


def recoverable_pages0_archive_count(summary: dict[str, object]) -> int | None:
    rows = summary.get("pages0_archive_validation")
    if not isinstance(rows, list):
        return None

    total = 0
    for row in rows:
        if not isinstance(row, dict):
            continue
        files = int(row.get("files") or 0)
        images = int(row.get("images") or 0)
        nested_archives = int(row.get("nested_archives") or 0)
        errors = int(row.get("errors") or 0)
        missing = int(row.get("missing") or 0)
        if images > 0 and nested_archives == 0 and errors == 0 and missing == 0:
            total += files
    return total


def gds_config_cover_reference_count(summary: dict[str, object]) -> int | None:
    rows = summary.get("cover_source_cache_risk")
    if not isinstance(rows, list):
        return None
    total = 0
    for row in rows:
        if not isinstance(row, dict):
            continue
        if row.get("UsesExpectedCacheName") is True:
            total += int(row.get("Series") or 0)
    return total


def txt_cover_metric(summary: dict[str, object], field: str) -> int | None:
    rows = summary.get("txt_cover_state")
    if not isinstance(rows, list):
        return None
    seen = False
    total = 0
    for row in rows:
        if not isinstance(row, dict):
            continue
        if field not in row:
            continue
        seen = True
        total += int(row.get(field) or 0)
    return total if seen else None


def print_postflight_gates(before_path: str, after: dict[str, object]) -> bool:
    with open(before_path, "r", encoding="utf-8") as handle:
        before = json.load(handle)

    print("\n## postflight gates")
    failed = False

    integrity_after = after.get("integrity_check")
    if integrity_after == "ok":
        gate_line("PASS", "sqlite integrity", {"after": integrity_after})
    else:
        gate_line("FAIL", "sqlite integrity", {"after": integrity_after})
        failed = True

    fk_before = len(before.get("foreign_key_check", []))
    fk_after = len(after.get("foreign_key_check", []))
    if fk_after == 0:
        gate_line("PASS", "foreign keys", {"before": fk_before, "after": fk_after})
    else:
        gate_line("FAIL", "foreign keys", {"before": fk_before, "after": fk_after})
        failed = True

    pages0_before = sum_count(before.get("pages0_by_library_ext", []), "Count")
    pages0_after = sum_count(after.get("pages0_by_library_ext", []), "Count")
    if pages0_after < pages0_before:
        gate_line("PASS", "Pages=0 debt decreased", {"before": pages0_before, "after": pages0_after})
    elif pages0_after == pages0_before:
        gate_line("WARN", "Pages=0 debt unchanged", {"before": pages0_before, "after": pages0_after})
    else:
        gate_line("FAIL", "Pages=0 debt increased", {"before": pages0_before, "after": pages0_after})
        failed = True

    recoverable_pages0_before = recoverable_pages0_archive_count(before)
    recoverable_pages0_after = recoverable_pages0_archive_count(after)
    if recoverable_pages0_before is not None and recoverable_pages0_after is not None:
        if recoverable_pages0_after < recoverable_pages0_before:
            gate_line("PASS", "recoverable Pages=0 archives decreased", {
                "before": recoverable_pages0_before,
                "after": recoverable_pages0_after,
            })
        elif recoverable_pages0_after == recoverable_pages0_before:
            gate_line("WARN", "recoverable Pages=0 archives unchanged", {
                "before": recoverable_pages0_before,
                "after": recoverable_pages0_after,
            })
        else:
            gate_line("FAIL", "recoverable Pages=0 archives increased", {
                "before": recoverable_pages0_before,
                "after": recoverable_pages0_after,
            })
            failed = True
    else:
        gate_line("WARN", "recoverable Pages=0 archive gate skipped", {
            "reason": "run before and after diagnostics with --check-archives",
        })

    same_dup_before = sum_count(
        before.get("duplicate_cleanup_candidates", []),
        "Groups",
        "same_series_same_volume",
    )
    same_dup_after = sum_count(
        after.get("duplicate_cleanup_candidates", []),
        "Groups",
        "same_series_same_volume",
    )
    if same_dup_after < same_dup_before:
        gate_line("PASS", "same-series duplicate groups decreased", {
            "before": same_dup_before,
            "after": same_dup_after,
        })
    elif same_dup_after == same_dup_before:
        gate_line("WARN", "same-series duplicate groups unchanged", {
            "before": same_dup_before,
            "after": same_dup_after,
        })
    else:
        gate_line("FAIL", "same-series duplicate groups increased", {
            "before": same_dup_before,
            "after": same_dup_after,
        })
        failed = True

    cross_dup_before = sum_count(before.get("duplicate_cleanup_candidates", []), "Groups", "cross_series")
    cross_dup_after = sum_count(after.get("duplicate_cleanup_candidates", []), "Groups", "cross_series")
    if cross_dup_after <= cross_dup_before:
        gate_line("PASS", "cross-series duplicate groups did not increase", {
            "before": cross_dup_before,
            "after": cross_dup_after,
        })
    else:
        gate_line("FAIL", "cross-series duplicate groups increased", {
            "before": cross_dup_before,
            "after": cross_dup_after,
        })
        failed = True

    media_errors_before = sum_count(before.get("media_errors_by_ext_comment", []), "Count")
    media_errors_after = sum_count(after.get("media_errors_by_ext_comment", []), "Count")
    if media_errors_after <= media_errors_before:
        gate_line("PASS", "media errors did not increase", {
            "before": media_errors_before,
            "after": media_errors_after,
        })
    else:
        gate_line("FAIL", "media errors increased", {
            "before": media_errors_before,
            "after": media_errors_after,
        })
        failed = True

    cover_refs_before = gds_config_cover_reference_count(before)
    cover_refs_after = gds_config_cover_reference_count(after)
    if cover_refs_before is not None and cover_refs_after is not None:
        if cover_refs_after >= cover_refs_before:
            gate_line("PASS", "GDS config cover references did not decrease", {
                "before": cover_refs_before,
                "after": cover_refs_after,
            })
        else:
            gate_line("FAIL", "GDS config cover references decreased", {
                "before": cover_refs_before,
                "after": cover_refs_after,
            })
            failed = True
    else:
        gate_line("WARN", "cover cache gate skipped", {
            "reason": "run before and after diagnostics with --check-covers",
        })

    txt_config_before = txt_cover_metric(before, "series_with_config_cover")
    txt_config_after = txt_cover_metric(after, "series_with_config_cover")
    if None not in (txt_config_before, txt_config_after):
        assert txt_config_before is not None
        assert txt_config_after is not None
        if txt_config_after < txt_config_before:
            gate_line("FAIL", "TXT config covers decreased", {
                "before": txt_config_before,
                "after": txt_config_after,
            })
            failed = True
        else:
            gate_line("PASS", "TXT config covers did not decrease", {
                "before": txt_config_before,
                "after": txt_config_after,
            })
    else:
        gate_line("WARN", "TXT config cover gate skipped", {
            "reason": "run before and after diagnostics with --check-covers",
        })

    txt_missing_before = txt_cover_metric(before, "series_without_any_cover_hint")
    txt_missing_after = txt_cover_metric(after, "series_without_any_cover_hint")
    if None not in (txt_config_before, txt_config_after, txt_missing_before, txt_missing_after):
        assert txt_config_before is not None
        assert txt_config_after is not None
        assert txt_missing_before is not None
        assert txt_missing_after is not None
        if txt_missing_after < txt_missing_before:
            gate_line("PASS", "TXT missing-cover debt decreased", {
                "config_covers_before": txt_config_before,
                "config_covers_after": txt_config_after,
                "missing_cover_debt_before": txt_missing_before,
                "missing_cover_debt_after": txt_missing_after,
            })
        elif txt_missing_after == txt_missing_before:
            gate_line("WARN", "TXT missing-cover debt unchanged", {
                "config_covers_before": txt_config_before,
                "config_covers_after": txt_config_after,
                "missing_cover_debt_before": txt_missing_before,
                "missing_cover_debt_after": txt_missing_after,
            })
        else:
            gate_line("FAIL", "TXT missing-cover debt increased", {
                "config_covers_before": txt_config_before,
                "config_covers_after": txt_config_after,
                "missing_cover_debt_before": txt_missing_before,
                "missing_cover_debt_after": txt_missing_after,
            })
            failed = True
    else:
        gate_line("WARN", "TXT missing-cover debt gate skipped", {
            "reason": "run before and after diagnostics with --check-covers --check-cover-source-files",
        })

    return failed


def pages0_archive_validation_rows(con: sqlite3.Connection, container_root: str, host_root: str) -> list[dict[str, object]]:
    rows = list(con.execute("""
        select l.Id as LibraryId, lower(coalesce(mf.Extension, '')) as Ext, mf.FilePath
        from MangaFile mf
        join Chapter c on c.Id = mf.ChapterId
        join Volume v on v.Id = c.VolumeId
        join Series s on s.Id = v.SeriesId
        join Library l on l.Id = s.LibraryId
        where mf.Pages = 0 and lower(coalesce(mf.Extension, '')) in ('.zip', '.cbz')
        order by l.Id, Ext
    """))

    stats: dict[tuple[int, str], collections.Counter[str]] = collections.defaultdict(collections.Counter)
    for row in rows:
        key = (row["LibraryId"], row["Ext"])
        stats[key]["files"] += 1
        file_path = mapped_path(row["FilePath"], container_root, host_root)
        if not os.path.exists(file_path):
            stats[key]["missing"] += 1
            continue
        stats[key]["exists"] += 1
        try:
            with zipfile.ZipFile(file_path) as archive:
                for name in archive.namelist():
                    if name.endswith("/"):
                        continue
                    ext = os.path.splitext(name.lower())[1]
                    if ext in IMAGE_EXTENSIONS:
                        stats[key]["images"] += 1
                    elif ext in ARCHIVE_EXTENSIONS:
                        stats[key]["nested_archives"] += 1
                stats[key]["readable"] += 1
        except Exception:
            stats[key]["errors"] += 1

    return [
        {
            "LibraryId": key[0],
            "Ext": key[1],
            **dict(counter),
        }
        for key, counter in sorted(stats.items())
    ]


def summarize_pages0_archives(con: sqlite3.Connection, container_root: str, host_root: str) -> None:
    rows = pages0_archive_validation_rows(con, container_root, host_root)
    print("\n## pages0 archive validation")
    if not rows:
        print("(none)")
        return
    for row in rows:
        key = (row["LibraryId"], row["Ext"])
        counter = {k: v for k, v in row.items() if k not in {"LibraryId", "Ext"}}
        print(key, counter)


def cover_source_cache_rows(
    con: sqlite3.Connection,
    container_root: str,
    host_root: str,
    check_source_files: bool,
) -> list[dict[str, object]]:
    rows = list(con.execute("""
        select l.Id as LibraryId, s.Id as SeriesId, s.FolderPath, s.CoverImage as SeriesCover,
               v.CoverImage as VolumeCover, c.CoverImage as ChapterCover
        from Series s
        join Library l on l.Id = s.LibraryId
        left join Volume v on v.SeriesId = s.Id
        left join Chapter c on c.VolumeId = v.Id
        where l.Type = 6
    """))

    series: dict[int, dict[str, object]] = {}
    for row in rows:
        item = series.setdefault(row["SeriesId"], {
            "library_id": row["LibraryId"],
            "folder": row["FolderPath"] or "",
            "series_cover": row["SeriesCover"] or "",
            "volume_covers": set(),
            "chapter_covers": set(),
        })
        if row["VolumeCover"]:
            item["volume_covers"].add(row["VolumeCover"])
        if row["ChapterCover"]:
            item["chapter_covers"].add(row["ChapterCover"])

    summary: collections.Counter[tuple[int, bool | None, bool]] = collections.Counter()
    for series_id, item in series.items():
        has_source_cover = None
        if check_source_files:
            folder = mapped_path(str(item["folder"]), container_root, host_root)
            has_source_cover = any(os.path.exists(os.path.join(folder, name)) for name in ("cover.jpg", "cover.png", "cover.webp"))
        expected = {f"_s{series_id}.jpg", f"_s{series_id}.png", f"_s{series_id}.webp"}
        uses_expected_cache = (
            item["series_cover"] in expected
            or bool(item["volume_covers"] & expected)
            or bool(item["chapter_covers"] & expected)
        )
        library_id = int(item["library_id"])
        summary[(library_id, has_source_cover, uses_expected_cache)] += 1

    return [
        {
            "LibraryId": key[0],
            "HasSourceCover": key[1],
            "UsesExpectedCacheName": key[2],
            "Series": count,
            "RiskWithoutSourceCover": key[2] and key[1] is False,
            "SourceCoverProbe": check_source_files,
        }
        for key, count in sorted(summary.items(), key=lambda item: (item[0][0], str(item[0][1]), item[0][2]))
    ]


def summarize_cover_risk(
    con: sqlite3.Connection,
    container_root: str,
    host_root: str,
    check_source_files: bool,
) -> None:
    rows = cover_source_cache_rows(con, container_root, host_root, check_source_files)
    print("\n## cover source/cache risk")
    print("source_cover_probe", check_source_files)
    print("(LibraryId, HasSourceCover, UsesExpectedCacheName) -> Series")
    risk_by_library: collections.Counter[int] = collections.Counter()
    for row in rows:
        key = (row["LibraryId"], row["HasSourceCover"], row["UsesExpectedCacheName"])
        print(f"{key} -> {row['Series']}")
        if row["RiskWithoutSourceCover"]:
            risk_by_library[int(row["LibraryId"])] += int(row["Series"])
    print("risk_by_library", dict(sorted(risk_by_library.items())))


def txt_cover_state_rows(
    con: sqlite3.Connection,
    container_root: str,
    host_root: str,
    check_source_files: bool,
) -> list[dict[str, object]]:
    rows = list(con.execute("""
        select l.Id as LibraryId, l.Name as LibraryName, s.Id as SeriesId,
               s.FolderPath, s.CoverImage as SeriesCover, mf.FilePath
        from MangaFile mf
        join Chapter c on c.Id = mf.ChapterId
        join Volume v on v.Id = c.VolumeId
        join Series s on s.Id = v.SeriesId
        join Library l on l.Id = s.LibraryId
        where lower(coalesce(mf.Extension, '')) = '.txt'
        order by l.Id, s.Id
    """))

    series: dict[int, dict[str, object]] = {}
    for row in rows:
        item = series.setdefault(row["SeriesId"], {
            "library_id": row["LibraryId"],
            "library_name": row["LibraryName"],
            "series_cover": row["SeriesCover"] or "",
            "files": [],
            "folder": mapped_path(row["FolderPath"] or "", container_root, host_root),
        })
        item["files"].append(mapped_path(row["FilePath"], container_root, host_root))

    summary: dict[int, collections.Counter[str]] = collections.defaultdict(collections.Counter)
    names: dict[int, str] = {}
    for item in series.values():
        library_id = int(item["library_id"])
        names[library_id] = str(item["library_name"])
        summary[library_id]["series"] += 1
        files = item["files"]
        summary[library_id]["files"] += len(files)
        if item["series_cover"]:
            summary[library_id]["series_with_config_cover"] += 1

        source_cover = False
        yaml_cover_kinds: collections.Counter[str] = collections.Counter()
        if check_source_files:
            for file_path in files:
                has_cover_file, has_yaml_cover = has_source_cover_hint(file_path, str(item["folder"]))
                source_cover = source_cover or has_cover_file
                if has_yaml_cover:
                    yaml_cover_kinds[classify_yaml_cover_value(read_yaml_cover_value(file_path, str(item["folder"])))] += 1

            if source_cover:
                summary[library_id]["series_with_source_cover_file"] += 1
            for kind in sorted(yaml_cover_kinds):
                summary[library_id][f"series_with_yaml_{kind}"] += 1

            has_usable_yaml_cover = any(yaml_cover_kinds[kind] > 0 for kind in ("base64-like", "data-uri", "url"))
            if not item["series_cover"] and not source_cover and not has_usable_yaml_cover:
                summary[library_id]["series_without_any_cover_hint"] += 1
        elif not item["series_cover"]:
            summary[library_id]["series_without_config_cover"] += 1

    output: list[dict[str, object]] = []
    for library_id in sorted(summary):
        counter = summary[library_id]
        output.append({
            "LibraryId": library_id,
            "LibraryName": names[library_id],
            "SourceCoverProbe": check_source_files,
            **dict(counter),
        })
    return output


def summarize_text_cover_state(
    con: sqlite3.Connection,
    container_root: str,
    host_root: str,
    check_source_files: bool,
) -> None:
    rows = txt_cover_state_rows(con, container_root, host_root, check_source_files)
    print("\n## txt cover state")
    print("source_cover_probe", check_source_files)
    if not rows:
        print("(none)")
        return
    for row in rows:
        library_id = row["LibraryId"]
        name = row["LibraryName"]
        counter = {k: v for k, v in row.items() if k not in {"LibraryId", "LibraryName"}}
        print(library_id, name, counter)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", required=True, help="Path to kavita.db")
    parser.add_argument("--container-root", default="/mnt/gds", help="Media root stored in DB paths")
    parser.add_argument("--host-root", default="/mnt/gds2", help="Readable media root for this script")
    parser.add_argument("--check-archives", action="store_true", help="Open Pages=0 ZIP/CBZ files and classify contents")
    parser.add_argument("--check-covers", action="store_true", help="Check config cover state without probing source files")
    parser.add_argument("--check-cover-source-files", action="store_true", help="Also probe source cover files and YAML cover hints")
    parser.add_argument("--json-output", help="Write machine-readable baseline summary to this JSON file")
    parser.add_argument("--compare-json", help="Compare current summary with a previous --json-output file")
    parser.add_argument("--postflight-gates", action="store_true", help="Print PASS/WARN/FAIL gates for a --compare-json run")
    parser.add_argument("--fail-on-gate-failure", action="store_true", help="Exit non-zero if any --postflight-gates check fails")
    args = parser.parse_args()
    if args.postflight_gates and not args.compare_json:
        parser.error("--postflight-gates requires --compare-json")
    if args.fail_on_gate_failure and not args.postflight_gates:
        parser.error("--fail-on-gate-failure requires --postflight-gates")
    if args.check_cover_source_files and not args.check_covers:
        parser.error("--check-cover-source-files requires --check-covers")

    con = connect_readonly(args.db)
    json_summary = build_json_summary(
        con,
        args.container_root,
        args.host_root,
        args.check_archives,
        args.check_covers,
        args.check_cover_source_files,
    ) if args.json_output or args.compare_json else None
    print("integrity_check", con.execute("pragma integrity_check").fetchone()[0])
    summarize_foreign_keys(con)
    summarize_startup_state(con)
    summarize_db(con)
    summarize_duplicate_structure(con)
    summarize_duplicate_cleanup_candidates(con)
    if args.check_archives:
        summarize_pages0_archives(con, args.container_root, args.host_root)
    if args.check_covers:
        summarize_cover_risk(con, args.container_root, args.host_root, args.check_cover_source_files)
        summarize_text_cover_state(con, args.container_root, args.host_root, args.check_cover_source_files)
    if args.json_output:
        with open(args.json_output, "w", encoding="utf-8") as handle:
            json.dump(json_summary, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        print(f"\nWrote JSON summary: {args.json_output}")
    if args.compare_json:
        print_json_comparison(args.compare_json, json_summary)
    if args.postflight_gates:
        failed = print_postflight_gates(args.compare_json, json_summary)
        if failed and args.fail_on_gate_failure:
            sys.exit(1)


if __name__ == "__main__":
    main()
