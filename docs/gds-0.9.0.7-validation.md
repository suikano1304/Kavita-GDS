# Kavita-GDS 0.9.0.7 Validation

Validation date: 2026-06-06

This records the local Kavita-GDS validation performed for the 0.9.0.7 nightly merge. The validation used a disposable test container and a copied test database. The production container was not upgraded or restarted.

## Scope

- Upstream nightly version: 0.9.0.7.
- GDS-specific reader behavior after the upstream merge.
- Kavita+ unused path assumption, while preserving non-Plus library eligibility behavior.
- Test container startup, health, version reporting, database integrity, reader APIs, image APIs, cover APIs, and post-test logs.
- GDS source mount was verified as read-only inside the test container.

## Result

Extended validation passed.

- Health endpoint returned 200.
- Plugin version endpoint reported 0.9.0.7.
- SQLite integrity check returned `ok` before and after reader/API testing.
- No new media errors were created during the final validation run.
- No new 404, 500, fatal, SQLite, database-lock, or disk I/O errors were found in logs for the final validation run.

## Reader Coverage

The validation exercised representative GDS and local fixture samples across:

- TXT book reader, including long text pagination and a reported TXT regression fixture.
- Archive image reader for ZIP/CBZ-style content, including image, thumbnail, file dimension, and negative-page clamp behavior.
- EPUB book reader, including normal EPUBs, reported problem EPUBs, missing-navigation fallback, and a synthetic single-spine table-of-contents regression fixture.
- PDF reader, including raw PDF serving, PDF extraction metadata, and extracted page image rendering.
- Repeated chapter-info cache calls across text, archive, EPUB, and PDF samples.
- Series cover retrieval for samples known to have covers.

## Notes

- The synthetic single-spine EPUB fixture intentionally has no cover and very small HTML pages. It is used only to validate table-of-contents page mapping, not cover generation or page size.
- The missing-navigation EPUB fixture can return a very small generated wrapper page. The expected behavior is successful book-info, table-of-contents, and page access.
- Existing duplicate-path and loose-image counters in the copied test database were treated as baseline data and were not introduced by the 0.9.0.7 merge.
- The production container remained on the previous image during validation.

