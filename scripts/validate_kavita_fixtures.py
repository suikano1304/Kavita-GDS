#!/usr/bin/env python3
"""Validate the local Kavita-GDS fixture library through the Kavita API.

This is intended to run inside lxc1 where kavita-test listens on 127.0.0.1:5658
and the test config is mounted at /mnt/data/docker/kavita-test/config.
It reads the local Kavita API key from the test DB and never prints it.
"""

import json
import sqlite3
import subprocess
import time
import urllib.parse
import urllib.request

BASE = "http://127.0.0.1:5658/api"
DB = "/mnt/data/docker/kavita-test/config/kavita.db"
LIBRARY_ID = 10


def db_query(sql):
    with sqlite3.connect(DB) as con:
        con.row_factory = sqlite3.Row
        return [dict(row) for row in con.execute(sql)]


def api_key():
    rows = db_query("SELECT Key FROM AppUserAuthKey ORDER BY Id LIMIT 1")
    if not rows:
        raise RuntimeError("No API key found")
    return rows[0]["Key"]


KEY = api_key()


def url(path, params=None):
    params = dict(params or {})
    params["apiKey"] = KEY
    return f"{BASE}{path}?{urllib.parse.urlencode(params)}"


def request(path, params=None, expect_json=False):
    req = urllib.request.Request(url(path, params), method="GET")
    with urllib.request.urlopen(req, timeout=30) as res:
        body = res.read()
        if res.status != 200:
            raise RuntimeError(f"HTTP {res.status} for {path}")
        if expect_json:
            return json.loads(body.decode("utf-8"))
        return body


def post(path, params=None):
    req = urllib.request.Request(url(path, params), data=b"", method="POST")
    with urllib.request.urlopen(req, timeout=30) as res:
        res.read()
        if res.status != 200:
            raise RuntimeError(f"HTTP {res.status} for POST {path}")


def last_scanned():
    return db_query(f"SELECT LastScanned FROM Library WHERE Id={LIBRARY_ID}")[0]["LastScanned"]


def force_scan_and_wait(pass_no):
    before = last_scanned()
    post("/library/scan", {"libraryId": LIBRARY_ID, "force": "true"})
    deadline = time.time() + 180
    while time.time() < deadline:
        current = last_scanned()
        if current != before:
            return current
        time.sleep(2)
    raise RuntimeError(f"pass {pass_no}: scan did not complete before timeout")


def fixture_chapters():
    return db_query(
        """
        SELECT
          s.Id AS SeriesId,
          s.Name AS SeriesName,
          v.Id AS VolumeId,
          c.Id AS ChapterId,
          c.Pages AS ChapterPages,
          mf.Id AS FileId,
          mf.Format AS Format,
          mf.Pages AS FilePages,
          mf.FilePath AS FilePath,
          c.CoverImage AS ChapterCover
        FROM Series s
        JOIN Volume v ON v.SeriesId=s.Id
        JOIN Chapter c ON c.VolumeId=v.Id
        JOIN MangaFile mf ON mf.ChapterId=c.Id
        WHERE s.LibraryId=10
        ORDER BY s.Name, v.MinNumber, c.MinNumber, c.Id
        """
    )


def page_samples(pages):
    if pages <= 1:
        return [0]

    deduped = []
    for page in [0, max(0, pages // 2), pages - 1]:
        if page not in deduped:
            deduped.append(page)
    return deduped


def validate_all(pass_no):
    rows = fixture_chapters()
    counters = {
        "total": len(rows),
        "info_fail": 0,
        "nav_fail": 0,
        "page_fail": 0,
        "zero_bytes": 0,
        "zero_pages": 0,
        "missing_covers": 0,
    }
    failures = []

    for row in rows:
        chapter_id = row["ChapterId"]
        series_id = row["SeriesId"]
        volume_id = row["VolumeId"]
        fmt = row["Format"]
        pages = row["ChapterPages"] or row["FilePages"] or 0

        if pages <= 0:
            counters["zero_pages"] += 1
            failures.append(f"{chapter_id}: zero pages")
            continue

        if not row["ChapterCover"]:
            counters["missing_covers"] += 1
            failures.append(f"{chapter_id}: missing cover")

        try:
            info = request(
                "/reader/chapter-info",
                {"chapterId": chapter_id, "includeDimensions": "false"},
                True,
            )
            if int(info.get("pages") or info.get("Pages") or 0) != int(pages):
                failures.append(f"{chapter_id}: chapter-info pages mismatch")
                counters["info_fail"] += 1
        except Exception as exc:
            counters["info_fail"] += 1
            failures.append(f"{chapter_id}: chapter-info {exc}")

        for nav in ("next-chapter", "prev-chapter"):
            try:
                body = request(
                    f"/reader/{nav}",
                    {
                        "seriesId": series_id,
                        "volumeId": volume_id,
                        "currentChapterId": chapter_id,
                    },
                )
                int(body.decode("utf-8"))
            except Exception as exc:
                counters["nav_fail"] += 1
                failures.append(f"{chapter_id}: {nav} {exc}")

        page_path = "/reader/image" if fmt == 1 else f"/book/{chapter_id}/book-page"
        if fmt == 3:
            try:
                info = request(f"/book/{chapter_id}/book-info", {}, True)
                if int(info.get("pages") or info.get("Pages") or 0) != int(pages):
                    counters["info_fail"] += 1
                    failures.append(f"{chapter_id}: book-info pages mismatch")
                chapters = request(f"/book/{chapter_id}/chapters", {}, True)
                if not isinstance(chapters, list):
                    counters["info_fail"] += 1
                    failures.append(f"{chapter_id}: chapters response not list")
            except Exception as exc:
                counters["info_fail"] += 1
                failures.append(f"{chapter_id}: book metadata {exc}")

        for page in page_samples(int(pages)):
            try:
                params = {"page": page}
                if fmt == 1:
                    params["chapterId"] = chapter_id
                body = request(page_path, params)
                if len(body) == 0:
                    counters["zero_bytes"] += 1
                    failures.append(f"{chapter_id}: page {page} zero bytes")
            except Exception as exc:
                counters["page_fail"] += 1
                failures.append(f"{chapter_id}: page {page} {exc}")

    print(
        "pass={pass_no} total={total} info_fail={info_fail} nav_fail={nav_fail} "
        "page_fail={page_fail} zero_bytes={zero_bytes} zero_pages={zero_pages} "
        "missing_covers={missing_covers}".format(pass_no=pass_no, **counters),
        flush=True,
    )

    if failures:
        for failure in failures[:50]:
            print(f"FAIL {failure}", flush=True)
        raise RuntimeError(f"pass {pass_no}: {len(failures)} failures")


def main():
    image = subprocess.check_output(
        ["docker", "inspect", "-f", "{{.Image}}", "kavita-test"], text=True
    ).strip()
    print(f"image={image}", flush=True)
    for pass_no in range(1, 4):
        scanned = force_scan_and_wait(pass_no)
        validate_all(pass_no)
        print(f"pass={pass_no} last_scanned={scanned}", flush=True)


if __name__ == "__main__":
    main()
