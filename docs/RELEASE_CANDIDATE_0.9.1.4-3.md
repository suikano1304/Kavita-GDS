# 0.9.1.4-3 release candidate

This candidate corrects OPDS acquisition metadata and merged comic downloads, reading-profile scaling persistence and account isolation, and font overrides for nested EPUB text. It also retries a book read once when a completed scan evicts its prepared cache.

Existing profile data and public profile DTOs are retained. Comic acquisition URLs use a `.zip` alias with a `.cbz` download filename and `application/vnd.comicbook+zip`; existing download URLs and EPUB/TXT/PDF paths remain supported. EPUB font changes affect the web reader only.

Publication remains gated on the complete reader/device regression checklist and all three architecture runtime checks. This document is candidate preparation, not a release or deployment announcement.
