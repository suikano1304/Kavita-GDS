# Kavita-GDS 테스트 컨테이너 검증 기록 - 2026-06-01

## 목적

- 운영 `kavita` 컨테이너는 유지한다.
- 운영 스캔 완료 후 운영 config를 복제해 `kavita-test`에서 official Kavita `0.9.0.6` nightly 기반 테스트 이미지를 검증한다.
- GDS 원본은 read-only로 유지하고, 반복 추가/삭제/재스캔 검증은 local fixture에서만 수행한다.
- 검증 완료 전까지 `git commit`, `git push`, release/package publish는 하지 않는다.

## 경로

- 운영 compose: `/opt/compose/kavita/docker-compose.yml` (lxc1)
- 테스트 compose: `/opt/compose/kavita-test/docker-compose.yml` (lxc1)
- 테스트 config: `/your/kavita-test/config` (lxc1)
- 테스트 fixture: `/your/kavita-test/fixtures` (lxc1)
- 빌드 staging: `/your/kavita-test/build` (lxc1)
- 소스 작업 tree: `<local-kavita-source>`

## 2026-06-02 운영 재스캔 후속

`9.0.6-1` stream-discovery 빌드를 운영에 반영한 뒤 대형 GDS 라이브러리 강제 스캔을 진행했다.

완료 확인:

- `성인 만화`: `2026-06-02 09:29:16 KST`
- `production-library-a`: `2026-06-02 12:34:55 KST`
- `연재중`: `2026-06-02 13:25:30 KST`

`production-library-c`은 `2026-06-02 13:31:10 KST`에 시작했고, discovery 완료 후 `13675` series가 산출됐다. 이후 per-series 단계에서 `WordCountAnalyzerService`가 강제 실행되며 일부 EPUB series에 수십 초에서 130초까지 소요되는 병목이 확인됐다.

새 후속 패치:

- source commit: `17253b3 fix: skip GDS word count during library scans`
- package patch: `patches/9.0.6-1/0002-fix-skip-GDS-word-count-during-library-scans.patch`
- test/production image ID after rebuild: `sha256:<redacted>`

의도:

- GDS library scan은 DB update와 cover generation을 계속 수행한다.
- GDS library scan 중 강제 word-count는 건너뛴다.
- 별도 analyze/word-count 기능은 유지한다.
- 사용자가 요청한 커버 중심 강제 스캔 시간을 현실적인 범위로 줄인다.

`LOCAL-FIXTURES` 재검증:

```text
Finished library scan of 118 files and 27 series in 12013 milliseconds for LOCAL-FIXTURES
pass=1 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
```

동일 로그 범위에서 `WordCountAnalyzerService` 로그는 발생하지 않았다.

## 테스트 이미지

- official baseline: `ghcr.io/kareadita/kavita:nightly-0.9.0.6`
- official revision: `<redacted-hash>`
- 최종 태그: `local/kavita-gds:9.0.6-1`, `local/kavita-gds:9.0.6-1-test`, `local/kavita-gds:latest`
- 최종 Image ID: `sha256:<redacted>`
- 최종 build time: `2026-06-01T00:47:34Z`
- 빌드 결과: 성공
- 빌드 경고:
  - 기존 npm dependency audit 경고
  - Angular Sass/CommonJS/budget 경고
  - 기존 .NET nullable/analyzer 및 package advisory 경고

중간 테스트 이미지 ID(`4aa4a776...`, `c5368ead...`, `bdfefabf...`, `fdf1f5d...`)는 fixture 검증 및 운영 이슈 재현 중 사용한 임시 산출물이며 공개 패키지에는 포함하지 않는다.

최종 runtime 확인:

- `/usr/bin/sqlite3` 포함
- `NanumGothic-Regular.ttf`, `NanumGothic-Bold.ttf`, `NanumGothic-ExtraBold.ttf` 포함
- `fc-match NanumGothic` 정상

## 테스트 compose

`/opt/compose/kavita-test/docker-compose.yml`:

- 컨테이너명: `kavita-test`
- 포트: `5658:5000`
- 이미지: `local/kavita-gds:9.0.6-1-test`
- config mount: `/your/kavita-test/config:/kavita/config`
- GDS mount: `/your/gds/mount:/mnt/gds:ro`
- fixture mount: `/your/kavita-test/fixtures:/fixtures:rw`
- `WAIT_ANCHOR_DIRS`: `<redacted-media-path>`

## NPM

- lxc3 NPM DB backup: `/your/npm-data/data/database.sqlite.bak-kavita-test-20260601-0450`
- Proxy host id: `26`
- Domain: `tkavita.suikano.net`
- Forward target: `192.168.10.6:5658`
- Certificate id: `6`
- SSL forced: enabled
- WebSocket upgrade: enabled
- HSTS: enabled
- HTTP/2: disabled, matching current production Kavita proxy
- Nginx config: `/your/npm-data/data/nginx/proxy_host/26.conf`
- Verification: `nginx -t` successful, Nginx reloaded

## Fixture 목록

Fixture root: `/your/kavita-test/fixtures`

Initial fixture size/count:

- `cbz`: `66M`, `26` CBZ files
- `zip`: `182M`, `10` ZIP files
- `epub`: `7.9M`, normal EPUB files
- `epub-problem`: `29M`, known problematic EPUB files
- `txt`: `605K`, `11` TXT files

Expanded fixture size/count after 2026-06-01 07:00 KST:

- `cbz`: `39` CBZ files
- `zip`: `26` ZIP files
- `epub`: `27` normal EPUB files
- `epub-problem`: `3` known problematic EPUB files
- `txt`: `22` TXT files
- total media: `117` files

Expanded source directories and sample titles are redacted from public docs.

Sample coverage:

- CBZ: 5 representative multi-volume/single-volume samples
- ZIP: 5 representative multi-volume/single-volume samples
- EPUB: normal EPUB samples and retained problem EPUB samples
- TXT: representative plain-text samples

TXT samples:

- `노게임 노라이프`
- `드레스 차림의 내가 높으신 분들의 가정교사가 된 사건`
- `인간 조조`
- `극지방을 향한 대도전`
- `비밀의 화원`

Note: TXT는 처음에 디렉터리 단위로 복사했다가 PDF/ZIP이 함께 포함되어 `594M`까지 커졌다. 즉시 해당 partial fixture를 삭제하고 DB에 기록된 `.txt` 파일만 다시 복사했다.

## 운영 스캔 게이트

Status: 진행 중. 운영 반영 대기.

2026-06-01 05:09 KST 기준 운영 DB `Library.LastScanned`:

```text
<redacted> production-library-a       2026-06-01 02:24:02.4762286
2 연재중          2026-06-01 03:17:44.992242
3 성인 만화       2026-06-01 04:21:11.3521712
<redacted> production-library-a          2026-06-01 04:25:33.3351495
<redacted> production-library-b            2026-06-01 04:26:14.7380056
<redacted> production-library-b   2026-06-01 04:26:54.1473306
<redacted> production-library-e     2026-06-01 04:45:05.2705731
<redacted> production-library-c    2026-05-27 00:31:39.6602245
<redacted> production-library-d          2026-05-31 19:59:51.9126311
```

운영 로그:

- `2026-06-01 04:45:06 KST` `production-library-c` scan 시작
- `2026-06-01 04:49:10 KST` scan already running 메시지 확인
- `2026-06-01 04:59:10 KST` scan already running 메시지 확인
- `2026-06-01 05:09:10 KST` scan already running 메시지 확인

추가 관찰:

- `kavita` 컨테이너 health: healthy
- `kavita` resource usage at 05:09 KST: CPU about `0.58%`, memory about `589MiB`
- `kavita.db-wal` mtime: `2026-06-01 04:48:02 KST`
- Kavita process에서 host GDS path open file은 확인되지 않음

운영 config 복제와 `kavita-test` 기동은 이 게이트 완료 후 진행한다.

2026-06-01 07:30 KST 재확인:

- `kavita` production image: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-8`
- `kavita` health: healthy
- `kavita-test` image: `local/kavita-gds:9.0.6-1-test`
- `kavita-test` health: healthy
- `production-library-c` scan은 아직 진행 중이다.
- Evidence: `2026-06-01 04:45:06 KST`에 `Beginning file scan on production-library-c`, `2026-06-01 07:30:06 KST`에도 `<redacted-media-path>` 경로 처리 로그가 계속 출력됨.
- Production DB `Library.LastScanned`: `production-library-c` remains `2026-05-27 00:31:39.6602245`, `production-library-d` remains `2026-05-31 19:59:51.9126311`.
- Production `docker stats`: `kavita` about `112%` CPU, `860.9MiB`; `kavita-test` about `0.30%` CPU, `348.9MiB`.
- Conclusion: production image replacement must remain blocked until the production scan finishes and `Library.LastScanned` is updated or the user explicitly decides to stop/override the running scan.

2026-06-01 07:30 KST rclone 재확인:

- `rclone-gds.service`: active
- RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`
- RC shows read/list transfer activity only.

2026-06-01 07:31 KST 재확인:

- Production `Library.LastScanned` remains unchanged for `production-library-c` and `production-library-d`.
- `2026-06-01 07:29:10 KST` production log: `A Scan is already running, rescheduling ScanSeries in 10 minutes`.
- `2026-06-01 07:30:06 KST` production log still reports `웹소설` paths being inspected.
- Production SQLite WAL mtime: `2026-06-01 07:30:10 KST`.
- Production `docker stats`: `kavita` about `1.26%` CPU, `875.9MiB`; `kavita-test` about `0.37%` CPU, `350.1MiB`.
- rclone RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`.
- rclone log through `07:31:32 KST`: `to upload 0, uploading 0`.
- Conclusion remains unchanged: do not replace production image while the production scan is still active.

2026-06-01 07:32 KST 재확인:

- Current host time: `2026-06-01 07:32:42 KST`.
- Production `Library.LastScanned` still unchanged for `production-library-c` and `production-library-d`.
- `2026-06-01 07:31:54 KST` production log still reports a `웹소설` path being inspected.
- Production `docker stats`: `kavita` about `100.74%` CPU, `664.7MiB`; `kavita-test` about `0.36%` CPU, `350.8MiB`.
- rclone RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`.
- rclone RC active transfers are reads from `<redacted-media-path>`.
- rclone log through `07:31:32 KST`: `to upload 0, uploading 0`.
- Conclusion remains unchanged: production scan is still active; production replacement and GitHub release remain blocked by the agreed gate.

2026-06-01 07:52 KST 재확인 및 Web UI 승인:

- User confirmed `tkavita.suikano.net` Web UI validation is complete.
- Confirmed by user: EPUB problem samples open, page counts/navigation are acceptable, TXT cover glyphs are acceptable, and multi-volume cover samples are acceptable.
- Production `Library.LastScanned` still unchanged for `production-library-c` and `production-library-d`.
- `2026-06-01 07:49:10 KST` production log: `A Scan is already running, rescheduling ScanSeries in 10 minutes`.
- Production DB/WAL mtime remains active through `2026-06-01 07:52:45 KST`.
- Production `docker stats`: `kavita` about `9.84%` CPU, `905.3MiB`; `kavita-test` about `0.55%` CPU, `462MiB`.
- rclone RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`.
- rclone active transfers are reads from `<redacted-media-path>`.
- rclone log through `07:51:32 KST`: `to upload 0, uploading 0`.
- Conclusion: Web UI approval gate is complete. Production scan gate is still open, so production replacement and GitHub release remain blocked until scan completion or explicit user override.

## rclone 확인

- Service: `rclone-gds.service`
- State: active
- Mount options include `--read-only`
- RC: `:5275`
- Recent VFS cache logs show `to upload 0, uploading 0`
- RC `core/stats` at 2026-06-01 05:03 KST:
  - `errors`: `0`
  - `deletes`: `0`
  - `renames`: `0`
  - `serverSideCopies`: `0`
  - `serverSideMoves`: `0`

## 2026-06-01 06:08 KST 검증 업데이트

- `kavita-test` health: `Ok`
- 컨테이너 이미지: `local/kavita-gds:0.9.0.6-test-20260601-fontfix-coverfix2`
- Kavita reported version: `v0.9.0.6`
- Runtime font check: `fc-match NanumGothic` -> `NanumGothic-Regular.ttf`
- `LOCAL-FIXTURES` scan: `61 files`, `20 series`, finished `2026-06-01 06:07:25 KST`
- `refresh-metadata?libraryId=<redacted>&force=true&forceColorscape=true`: completed for `20` series
- TXT cover sample `series24530.png`: Korean title renders normally, no square-box tofu glyphs
- CBZ/ZIP multi-volume cover sample hashes after forced cover refresh:

```text
sample-a volume-1  <redacted-cover-file>  <redacted-hash>
sample-a volume-2  <redacted-cover-file>  <redacted-hash>
sample-a volume-3  <redacted-cover-file>  <redacted-hash>
sample-b volume-1  <redacted-cover-file>  <redacted-hash>
sample-b volume-2  <redacted-cover-file>  <redacted-hash>
sample-c volume-1  <redacted-cover-file>  <redacted-hash>
sample-c volume-2  <redacted-cover-file>  <redacted-hash>
```

확인된 수정 내용:

- 테스트 Dockerfile runtime에 `fontconfig`와 `NanumGothic` TTF를 포함했다.
- TXT title-cover SVG의 `font-family`를 fontconfig/Pango가 인식하는 unquoted family list로 유지했다.
- `kavita.yaml`의 `files` 항목에서 대상 파일명에 해당하는 `cover`만 사용하도록 GDS cover parser를 보강했다.
- 강제 metadata refresh에서 기존 동일 커버가 각 파일별 YAML cover로 정상 교체되는 것을 확인했다.

Resolved after 06:24 KST redeploy:

- 새 이미지 `local/kavita-gds:0.9.0.6-test-20260601-fontfix-coverfix-epubfix` 배포.
- 중복 manifest EPUB은 원본 파일을 수정하지 않고 `/kavita/config/temp/epub-manifest-repair` 아래 임시 복구본을 만든 뒤 읽는다.
- 복구 범위는 OPF manifest에서 `id`, `href`, `media-type`가 모두 같은 완전 중복 `item` 제거로 제한한다.
- 임시 복구 파일은 reader dispose 후 삭제되며, 검증 시 남은 temp file 없음.

Problem EPUB API 검증:

```text
problem-chapter-a book-info HTTP 200  problem-epub-sample-a
problem-chapter-b book-info HTTP 200  problem-epub-sample-b
problem-chapter-c book-info HTTP 200  problem-epub-sample-c
```

Metadata/word count 검증:

```text
refresh-metadata libraryId=<redacted> force=true forceColorscape=true -> completed for 20 series
series/analyze libraryId=<redacted> seriesId=<redacted> forceUpdate=true -> completed
<redacted-series-id>|epub-problem|589835|problem-chapter-a|180283
<redacted-series-id>|epub-problem|589835|problem-chapter-b|207839
<redacted-series-id>|epub-problem|589835|problem-chapter-c|201713
```

로그에는 원본 `VersOne.Epub.EpubPackageException`이 warning으로 기록된 뒤 `Repaired duplicate EPUB manifest items in a temporary copy`가 남고, 작업은 실패하지 않는다.

## 2026-06-01 06:53 KST EPUB reader/page-count 추가 검증

사용자 Web UI 확인에서 problem EPUB 3개가 열리지만 페이지가 `1/1`로 표시되고 다음/이전 이동이 되지 않는 증상이 보고됨.

원인:

- GDS scan 최적화 경로가 EPUB/PDF/TXT page count를 실제로 읽지 않고 `1`로 저장했다.
- 이 로직이 `/mnt/gds` 원격 GDS 파일뿐 아니라 local fixture `/fixtures`에도 적용되어 `Chapter.Pages`와 `MangaFile.Pages`가 모두 `1`이 됐다.

수정:

- `ProcessSeries.GetGdsPageCount`에서 `/mnt/gds` mount path만 기존 shortcut을 유지한다.
- `/fixtures` 같은 local file은 EPUB/PDF/TXT도 기존 Kavita page-count 계산을 사용한다.
- 기존 `Pages=1` row도 force scan 시 다시 계산되도록 local path에서는 `existingPages`를 그대로 재사용하지 않는다.

배포/재스캔:

- 새 이미지: `local/kavita-gds:9.0.6-1-test`
- Image ID: `<redacted-hash>`
- `kavita-test` container image hash matches the tag hash.
- `LOCAL-FIXTURES` force scan: `61 files`, `20 series`, completed at `2026-06-01 06:53:02 KST`.

Problem EPUB page-count 결과:

```text
problem-chapter-a Chapter.Pages=34  MangaFile.Pages=34  WordCount=180283
problem-chapter-b Chapter.Pages=49  MangaFile.Pages=49  WordCount=207839
problem-chapter-c Chapter.Pages=42  MangaFile.Pages=42  WordCount=201713
```

Reader API 결과:

```text
problem-chapter-a book-info pages=34, reader/chapter-info pages=34
problem-chapter-b book-info pages=49, reader/chapter-info pages=49
problem-chapter-c book-info pages=42, reader/chapter-info pages=42
seriesTotalPages=125
```

`book-page` 실제 page 응답:

```text
problem-chapter-a page 0/1/33 HTTP 200
problem-chapter-b page 0/1/48 HTTP 200
problem-chapter-c page 0/1/41 HTTP 200
```

EPUB resource 검증:

```text
problem-chapter-a first 3 font resources HTTP 200
problem-chapter-b first 3 font resources HTTP 200
problem-chapter-c first 3 font resources HTTP 200
```

이전 빌드에서 `OEBPS/Styles/../Fonts/...` 또는 정규화된 `OEBPS/Fonts/...` 리소스가 400을 반환하던 문제는 `BookService`에서 content key 정규화와 manifest/file-path fallback을 추가해 해결했다.

Next/previous chapter API:

```text
problem-chapter-b next=problem-chapter-a prev=-1
problem-chapter-a next=problem-chapter-c prev=problem-chapter-b
problem-chapter-c next=-1 prev=problem-chapter-a
```

정렬은 현재 fixture filename/sort order 기준이며, API는 edge를 제외하고 유효한 chapter id를 반환한다.

## 2026-06-01 06:55 KST 권별 커버 중복 재검증

사용자가 "1권 이후 커버들이 전부 1권과 동일한 커버로 생성"되는 의심 증상을 추가 보고함.

최신 `9.0.6-1-test` 이미지와 force metadata/scan 이후 DB 및 cover file hash를 확인했다.

결과:

- EPUB/CBZ/ZIP multi-file samples have distinct `Chapter.CoverImage` names.
- Hashes are also distinct per chapter/volume.
- TXT samples intentionally share generated series title covers.

대표 hash:

```text
epub-problem problem-chapter-a <redacted-hash>
epub-problem problem-chapter-b <redacted-hash>
epub-problem problem-chapter-c <redacted-hash>
epub-sample-a 01 <redacted-hash>
epub-sample-a 02 <redacted-hash>
epub-sample-a 03 <redacted-hash>
zip-sample-a 01 <redacted-hash>
zip-sample-a 02 <redacted-hash>
```

현재 테스트 이미지에서는 "2권 이후가 1권 커버와 동일"한 증상이 재현되지 않는다.

## 2026-06-01 07:00 KST 3회 반복 검증

`LOCAL-FIXTURES` 대상으로 force scan과 핵심 reader/cover checks를 3회 반복했다.

```text
pass 1: 61 files / 20 series, EPUB pages 34/49/42, sample cover hashes distinct=5, book-page problem-chapter-b page 48 HTTP 200
pass 2: 61 files / 20 series, EPUB pages 34/49/42, sample cover hashes distinct=5, book-page problem-chapter-b page 48 HTTP 200
pass 3: 61 files / 20 series, EPUB pages 34/49/42, sample cover hashes distinct=5, book-page problem-chapter-b page 48 HTTP 200
```

Scan durations:

```text
pass 1: 4003 ms
pass 2: 3690 ms
pass 3: 3718 ms
```

External access:

- `tkavita.suikano.net/api/health` via lxc3 NPM local resolve: `Ok`
- `kavita-test`: healthy

Host rclone gate:

- `rclone-gds.service`: active
- RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`
- Recent `/var/log/rclone.gds.log`: repeated `to upload 0, uploading 0`

Note:

- RC stats still shows read/download transfer activity from the read-only mount cache path, but no delete/rename/upload/write activity.

## 2026-06-01 07:26 KST 확장 fixture 최종 검증

최종 테스트 이미지:

- image: `local/kavita-gds:9.0.6-1-test`
- Image ID: `<redacted-hash>`
- container: `kavita-test`, health `healthy`
- `/api/health`: `Ok`

확장 scan 결과:

```text
LOCAL-FIXTURES force scan: 117 files, 26 series, 11037 ms
DB summary: 117 chapters, 26 series, zero pages 0, missing chapter covers 0
Archive: 65 files, 12 series, zero pages 0, missing covers 0
EPUB:    30 files, 7 series,  zero pages 0, missing covers 0
TXT:     22 files, 7 series,  zero pages 0, missing covers 0
```

3-pass full reader validation:

```text
pass=1 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
pass=2 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
pass=3 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
```

각 pass는 모든 `LOCAL-FIXTURES` chapter에 대해 다음을 확인했다.

- `reader/chapter-info` HTTP 200 and DB page count match
- `reader/next-chapter`, `reader/prev-chapter` HTTP 200 and integer response
- Archive first/middle/last page image HTTP 200 and non-zero bytes
- EPUB/TXT first/middle/last `book-page` HTTP 200 and non-zero bytes

Problem EPUB next/previous chapter API:

```text
chapter=problem-chapter-a next=problem-chapter-c prev=problem-chapter-b
chapter=problem-chapter-b next=problem-chapter-a prev=-1
chapter=problem-chapter-c next=-1 prev=problem-chapter-a
```

사용자가 보고한 "열리지만 1/1로 보이고 다음/이전 이동이 안 됨" 증상은 `Pages=1` scan shortcut이 local fixture EPUB에도 적용된 것이 원인이었다. 최종 빌드에서는 problem EPUB page count가 `34/49/42`로 유지되고, next/previous chapter API도 edge를 제외하고 유효한 chapter id를 반환한다.

로그 검증:

- 3-pass full reader validation since `2026-06-01 07:25:50 KST`: no `[Error]`, no exception, no failed Hangfire job, no `DirectoryNotFound`.
- Force scan since `2026-06-01 07:26:32 KST`: no `[Error]`, no exception, no failed Hangfire job, no `DirectoryNotFound`.
- EPUB duplicate manifest repair success path now logs without exception stack.
- `/kavita/config/temp/epub-manifest-repair` remaining temp files: `0`.

External/rclone final check:

- `tkavita.suikano.net/api/health` via lxc3 NPM local resolve: `Ok`.
- `rclone-gds.service`: `active`.
- rclone RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`.
- rclone RC still shows read transfer activity from `<redacted-media-path>`, with no write/delete/rename activity.

추가 수정:

- `DirectoryService.ClearDirectory` now ignores directories already removed by concurrent cleanup/cache operations. This prevents scan/reader cache overlap from producing `DirectoryNotFoundException` cleanup noise.

## 2026-06-01 06:20 KST 삭제/재추가 재스캔 검증

- ZIP fixture sample volume 2를 library root 밖 `/tmp/kavita-fixture-removed`로 이동.
- scan 후 `LOCAL-FIXTURES`: `60 files`, `20 series`; 해당 sample DB 파일 수 `1`.
- 같은 파일을 원래 위치로 복구.
- scan 후 `LOCAL-FIXTURES`: `61 files`, `20 series`; 해당 sample DB 파일 수 `2`.
- 재추가된 2권은 새 cover filename으로 생성됨.
- 1권/2권 hash:

```text
<redacted-cover-file>  <redacted-hash>
<redacted-cover-file>  <redacted-hash>
```

## 남은 검증 항목

- 운영 강제 스캔 완료 최종 재확인
- 운영 스캔 완료 후 rclone RC `core/stats` 최종 확인
- 운영 스캔 완료 후 오류 로그 최종 확인

## 2026-06-01 07:55-08:00 KST 운영 반영

사용자 지시:

- 기존 운영 스캔은 중단하고 운영에 반영한다.
- 운영 반영이 모두 끝난 뒤 강제 스캔을 돌린다.

운영 반영 전 백업:

- backup dir: `<local-kavita-backup>`
- compose backup: `<local-kavita-backup>`
- DB backup: `<local-kavita-backup>`
- appsettings backup: `<local-kavita-backup>`
- DB backup verification: `PRAGMA integrity_check = ok`, `Library count = 9`

운영 compose 변경:

```diff
- image: ghcr.io/suikano1304/kavita-gds:0.9.0.2-8
+ image: local/kavita-gds:9.0.6-1
```

운영 이미지:

- image: `local/kavita-gds:9.0.6-1`
- Image ID: `<redacted-hash>`
- 같은 Image ID가 test image `local/kavita-gds:9.0.6-1-test`에도 붙어 있음을 확인했다.

운영 재기동 및 마이그레이션:

- command: `docker compose -f /opt/compose/kavita/docker-compose.yml up -d`
- container: `kavita`, health `healthy`
- `/api/health`: `Ok`
- startup log:
  - `Database backed up to /kavita/config/temp/migration/0.9.0.2`
  - `Running Manual Migrations - complete`
  - `Running Migrations - complete`
  - `Kavita - v0.9.0.6`

마운트 확인:

```text
/your/kavita/config -> /kavita/config RW=true
/your/gds/mount -> /mnt/gds RW=false
```

운영 전체 강제 스캔:

- endpoint: `POST /api/library/scan-all?force=true`
- auth: 운영 `AppUserAuthKey` 관리자 키를 shell 변수로만 읽어 사용했고 키는 출력하지 않았다.
- first HTTP result: `200`
- scan start log:

```text
[ScannerService] Starting Scan of All Libraries, Forced: true
[ScannerService] Beginning file scan on production-library-a
```

운영 재시작 및 강제 스캔 재요청:

- first scan request 후 UI/API 요청에서 SQLite `disk I/O error` 500이 반복 발생했다.
- host storage check: `<host-storage>` and `/your/docker-data` space OK, `zpool status -x` = `all pools are healthy`.
- DB check via immutable SQLite URI: `PRAGMA integrity_check = ok`, `Library count = 9`.
- container 내부 `/kavita/config` write test OK.
- 조치: `docker restart kavita`
- 재시작 후:
  - container health `healthy`
  - `/api/library/libraries` HTTP `200`
  - `disk I/O` 로그 중단
- 강제 스캔 재요청:
  - endpoint: `POST /api/library/scan-all?force=true`
  - HTTP result: `200`
  - retry scan start log:

```text
[ScannerService] Starting Scan of All Libraries, Forced: true
[ScannerService] Beginning file scan on production-library-a
```

rclone read-only 확인:

- `rclone-gds.service`: `active`
- rclone RC `core/stats`: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`
- `/var/log/rclone.gds.log`: `to upload 0`, `uploading 0`
- retry scan 중 rclone RC도 동일하게 write/delete/rename/server-side move/copy 모두 `0`.

운영 Web/API spot check:

- 운영 Web UI/API 요청은 200 응답.
- duplicate manifest EPUB를 운영에서 열 때 repair warning이 발생했고, exception stack 없이 temporary copy repair path로 처리됨.
- `book-info`, `book-page`, next/previous chapter API가 200으로 응답했다.
- 재시작 이후 남은 `[Error]` 로그는 publisher image fetch 실패(`CoverDbService`, e.g. `LIMITBOOK`)로, DB I/O error와 별개다.

## 2026-06-01 08:31 KST Production EPUB Hotfix

사용자 Web UI 확인 중 운영에서 `reported page-count EPUB sample` EPUB가 `1/1`로 표시되고, `reported duplicate-manifest EPUB sample`가 `Incorrect EPUB manifest: item with href = "image-0001.jpg" is not unique.` 오류를 내는 문제가 확인됐다. test fixture는 `/fixtures` 경로라 page count가 실제 값으로 재계산됐지만, 운영 `/mnt/gds` 경로는 scanner shortcut 때문에 EPUB page count가 계속 `1`로 유지되는 차이가 있었다.

조치:

- scanner에서는 `/mnt/gds` EPUB/PDF/TXT에 대해 원격 파일 전체 읽기를 하지 않고 기존 shortcut을 유지했다.
- 대신 EPUB `book-info` 요청 시 실제 reading order count가 `1`보다 크고 DB page count가 `1` 이하이면 `Chapter.Pages`, `MangaFile.Pages`, `Volume.Pages`, `Series.Pages`를 즉시 갱신하도록 했다.
- duplicate manifest repair를 `BookService`의 EPUB open 경로 전체에 적용해 `book-info`, `book-page`, `chapters`, resource fetch, metadata/word-count 경로가 같은 보정 로직을 사용하게 했다.
- 운영 image tag `local/kavita-gds:9.0.6-1`을 당시 hotfix image로 교체하고 `kavita` 컨테이너를 재생성했다. 최종 공개 image는 이후 추가 보정을 포함한 `sha256:<redacted>`이다.

검증:

```text
production kavita health: healthy
internal root: HTTP 200, 30467 bytes
internal main-MWPUWZBY.js: HTTP 200, 108811 bytes
external kavita.suikano.net via NPM: HTTP 200, 30467 bytes

sample-chapter-a reported page-count EPUB sample 1권 book-info: HTTP 200, pages=15
sample-chapter-b reported page-count EPUB sample 2권 book-info: HTTP 200, pages=12
sample-chapter-c reported duplicate-manifest EPUB sample 1권 book-info: HTTP 200, pages=13
sample-chapter-d reported duplicate-manifest EPUB sample 2권 book-info: HTTP 200, pages=12

sample-chapter-a book-page page=2: HTTP 200, 1393 bytes
sample-chapter-c book-page page=2: HTTP 200, 3739 bytes
sample-chapter-d book-page page=2: HTTP 200, 44497 bytes
sample-chapter-c chapters: HTTP 200, 1017 bytes
sample-chapter-d chapters: HTTP 200, 977 bytes

DB after on-read repair:
sample-chapter-a Chapter.Pages=15 MangaFile.Pages=15
sample-chapter-b Chapter.Pages=12 MangaFile.Pages=12
sample-chapter-c Chapter.Pages=13 MangaFile.Pages=13
sample-chapter-d Chapter.Pages=12 MangaFile.Pages=12
```

rclone RC after verification:

```text
errors=0
deletes=0
renames=0
serverSideCopies=0
serverSideMoves=0
```

Recent production logs show duplicate manifest repair warnings for `reported duplicate-manifest EPUB sample` 1/2권, and no `responded 500` or `disk I/O` failures during this validation. External publisher image fetch errors may still appear and are unrelated to the EPUB reader fix.

운영 최종 강제 스캔:

- 2026-06-01 10:53 KST 기준 아직 실행하지 않았다.
- 현재는 사용자의 지시에 따라 운영 커버 metadata refresh를 계속 진행 중인 상태로 두고 GitHub push를 먼저 수행한다.
- 커버 재생성 완료 후 `POST /api/library/scan-all?force=true`를 별도 후속 단계로 실행하고, 완료 로그와 rclone read-only 결과를 추가 기록한다.

GitHub release package 준비:

- package: `<public-docs-repo>/kavita-gds.tar.gz`
- inner Docker archive: `docker-image/kavita-gds.docker.tar`
- archive tags:
  - `local/kavita-gds:9.0.6-1`
  - `local/kavita-gds:latest`
- archive image id: `sha256:<redacted>`
- package SHA256: repository root `SHA256SUMS` 기준
- inner Docker archive SHA256: repository root `SHA256SUMS` 기준
- GHCR workflow `RELEASE_ASSET_SHA256`: package rebuild 후 root `SHA256SUMS`와 일치하게 갱신

GitHub/GHCR 등록:

- GitHub push: completed on `main`
- release: `https://github.com/suikano1304/Kavita-GDS/releases/tag/v9.0.6-1`
- release assets:
  - `kavita-gds.tar.gz`
  - `SHA256SUMS`
- GHCR publish workflow: completed successfully
- published image digest:
  - `ghcr.io/suikano1304/kavita-gds:9.0.6-1` -> `sha256:<redacted>`
  - `ghcr.io/suikano1304/kavita-gds:latest` -> `sha256:<redacted>`

## 2026-06-01 09:12 KST Missing EPUB3 NAV Hotfix

운영 Web UI 확인 중 `reported cover-only EPUB sample`에서 다음 오류가 확인됐다.

```text
EPUB parsing error: NAV item not found in EPUB manifest.
```

조치:

- `EpubManifestRepairHelper`가 EPUB3 OPF에 `properties="nav"` item이 없을 때 임시 repaired EPUB copy에 최소 `kavita-nav.xhtml`을 합성한다.
- EPUB open fallback 범위를 `EpubPackageException`에서 `EpubReaderException`으로 넓혀 missing NAV 예외도 동일한 임시 repair path를 탄다.
- 적용 경로: `BookService`, `BookController`, `WordCountAnalyzerService`.
- 사용자 제보 파일을 fixture에 추가했다.
  - source: `<redacted-media-path>`
  - fixture: `<redacted-fixture-path>`

테스트 이미지:

```text
local/kavita-gds:9.0.6-1-test
intermediate image sha256:<redacted>
```

`LOCAL-FIXTURES` 재스캔:

```text
Finished library scan of 118 files and 27 series in 11574 milliseconds for LOCAL-FIXTURES
DB summary: 118 chapters, zero pages 0, missing chapter covers 0
```

검증:

```text
test original GDS chapter <redacted> book-info HTTP 200
test original GDS chapter <redacted> book-page?page=0 HTTP 200
test original GDS chapter <redacted> chapters HTTP 200

test fixture chapter sample-chapter-cover-only book-info HTTP 200
test fixture chapter sample-chapter-cover-only book-page?page=0 HTTP 200
test fixture chapter sample-chapter-cover-only chapters HTTP 200
```

운영 반영:

```text
local/kavita-gds:9.0.6-1
final image sha256:<redacted>

production chapter <redacted> book-info HTTP 200
production chapter <redacted> book-page?page=0 HTTP 200
production chapter <redacted> chapters HTTP 200
production Web UI internal HTTP 200
production Web UI external HTTP 200
```

주의:

- `reported cover-only EPUB sample` 원본 EPUB은 ZIP entry가 `cover.jpg`, `cover.xhtml`, `toc.ncx`, `content.opf`뿐이다.
- OPF manifest의 유일한 XHTML은 `cover.xhtml`이고 spine도 `cover` 하나만 참조한다.
- 따라서 이 파일은 repair 이후에도 1페이지가 정상이며, 본문이 있는 원본 EPUB로 교체하지 않는 한 Kavita가 440화 본문을 표시할 수 없다.

rclone:

```text
errors=0 deletes=0 renames=0 serverSideCopies=0 serverSideMoves=0
```

## 2026-06-02 운영 커버 재생성 및 강제 스캔 후속

운영 metadata cover refresh 완료 후, 1권 이후 커버가 없거나 1권과 같은 generated cover hash를 가진 항목을 대상으로 강제 재생성을 수행했다.

DB 백업:

```text
/kavita/config/kavita.db.coverfix-final-20260602-050124.bak
backup_size=243M
backup_method=sqlite backup copy before remediation
```

재생성 대상:

```text
affected_series=1480
affected_chapters=15256
affected_volumes=14799
deleted_unreferenced_cover_files=12791
series_refresh_metadata_ok=1480
series_refresh_metadata_fail=0
```

재생성 후 상태:

```text
remaining Chapter.CoverImage nulls=93
remaining Volume.CoverImage nulls=50
cover_files_after_regeneration=155302
```

잔여 동일 커버 판단:

- DB가 후권대에 1권 cover file reference를 재사용하는 문제는 해소됐다.
- 남은 same-hash 사례는 원본 archive의 첫 이미지가 여러 권에서 동일한 경우다.
- 대표 샘플 `보표`, `파천분뢰수`는 각 권 ZIP의 `001.jpg` hash가 동일했고 2번째 이미지는 달랐다.
- 따라서 이후 개선은 "첫 이미지 대신 다른 후보를 cover로 선택"하는 정책 문제이며, 이번 강제 재생성 범위와는 분리한다.

운영 강제 `scan-all` 시도:

```text
POST /api/library/scan-all?force=true -> HTTP 200, but kavita killed by OOM
16 GiB attempt: exitCode=137, RSS about 11.1 GiB
24 GiB attempt: exitCode=137, RSS about 19.5 GiB
32 GiB attempt: host global OOM, kavita RSS about 27.6 GiB
```

임시 메모리 변경은 원복했다.

```text
pct set 101 -memory 16384 -swap 1024
lxc1 memory verified: 16 GiB
```

결론:

- 현재 `production-library-a` 전체 force scan은 scanner batching 없이는 운영에서 안전하지 않다.
- 최종 강제 스캔은 `scan-all` 대신 라이브러리별 force scan으로 대체 진행한다.
- `production-library-b`, `production-library-b`, `production-library-e`은 정상 완료됐다.
- 이후 `production-library-a`, `production-library-d`도 정상 완료됐다.
- `성인 만화` force scan 중 ARM64 build가 겹치면서 운영 Kavita가 2026-06-02 07:37:32 KST에 OOM 재시작됐다.
- 재시작 후 운영 Web UI와 health는 정상 복구됐다.

개별 스캔 최종 상태:

```text
<redacted> production-library-a     2026-06-02 00:00:26.4261487
2 연재중        2026-06-02 00:00:31.7440464
3 성인 만화     2026-06-02 00:00:34.8557038
<redacted> production-library-a        2026-06-02 07:03:46.5457951
<redacted> production-library-b          2026-06-02 06:52:21.3806613
<redacted> production-library-b 2026-06-02 06:55:46.6257085
<redacted> production-library-e   2026-06-02 06:58:07.8451872
<redacted> production-library-c  2026-06-02 00:42:14.7546525
<redacted> production-library-d        2026-06-02 07:08:53.2575256
```

복구 확인:

```text
kavita health=healthy
kavita restart_count=2
external Web UI=https://kavita.suikano.net/ HTTP 200, 30467 bytes
kavita memory after recovery=about 209 MiB / 16 GiB
```

rclone:

```text
rclone-gds.service=active
deletes=0
renames=0
serverSideCopies=0
serverSideMoves=0
errors=7
lastError=Google Drive rateLimitExceeded / quota exceeded
recent log: to upload 0, uploading 0
```

`errors=7`은 Google Drive API quota/rate-limit 누적이며, 원본 GDS 쓰기/삭제/rename은 발생하지 않았다.

## 2026-06-02 ARM64 GHCR Publish

사용자 요청에 따라 `9.0.6-1` ARM64 image를 추가로 빌드해 GHCR에 push했다.

빌드 메모:

- source: `<local-kavita-source>`
- source 핵심 패치 파일 checksum은 host repo와 lxc1 build source가 일치함을 확인했다.
- 기존 Dockerfile의 `npm ci`는 arm64 optional dependency lock mismatch로 실패했다.
- 성공한 재시도는 이미 생성돼 있던 `UI/Web/dist/browser` production bundle을 사용하고, `linux-arm64` runtime publish만 수행했다.
- 임시 Dockerfile: `<temporary-dockerfile>`

GHCR 결과:

```text
arm64 temporary tag:
ghcr.io/suikano1304/kavita-gds:9.0.6-1-arm64
index digest=sha256:<redacted>
arm64 image manifest=sha256:<redacted>

public tags:
ghcr.io/suikano1304/kavita-gds:9.0.6-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:<redacted>

linux/amd64=sha256:<redacted>
linux/arm64=sha256:<redacted>
```

## 2026-06-02 Low-Memory GDS Scan Rebuild

운영 `scan-all?force=true`와 `성인 만화` 개별 강제 스캔에서 이전 병렬 post-scan 경로가 OOM으로 종료되는 증상을 확인했다. 원인은 GDS 파일 스캔 뒤 DB 갱신, cover generation, word-count analysis가 동시에 많이 쌓이며 RSS가 16-32GiB까지 상승하는 경로로 판단했다.

Source change:

- source: `<local-kavita-source>`
- source commit: `22c119d fix: process GDS scan work sequentially`
- file: `Kavita.Services/Scanner/ScannerService.cs`
- GDS 라이브러리만 `ProcessParserInfoSequential` 경로로 분기한다.
- 일반 라이브러리의 기존 parallel channel 처리 경로는 유지한다.
- GDS 경로에서는 series별로 DB update scope를 끝낸 뒤 cover generation과 word-count를 별도 scope에서 실행한다.

Test image:

- image: `local/kavita-gds:9.0.6-1-lowmem-test`
- Image ID: `sha256:<redacted>`
- `kavita-test` compose image를 이 태그로 교체했다.

Fixture forced scan:

- library: `LOCAL-FIXTURES` (`LibraryId=10`)
- API result: HTTP `200`
- log evidence: `Using low-memory sequential GDS scan path for LOCAL-FIXTURES. Series to process: 27`
- result: `Finished library scan of 118 files and 27 series in 15947 milliseconds for LOCAL-FIXTURES`
- `LastScanned`: `2026-06-02 07:58:23.316648`
- observed memory after completion: about `395MiB`

EPUB repair evidence remained active in the low-memory path:

- duplicate manifest repair logged for existing `epub-problem` samples.
- missing NAV repair logged for `reported cover-only EPUB sample`.

Production rollout:

- `local/kavita-gds:9.0.6-1` and `local/kavita-gds:latest` were retagged to Image ID `sha256:<redacted>`.
- production `kavita` was recreated from `/opt/compose/kavita/docker-compose.yml`.
- health endpoint returned `Ok` after restart.
- production `성인 만화` forced scan was started through the API at `2026-06-02 08:00:56 KST`.
- The scan was still in the file enumeration stage when this note was written; no `Found N Series` progress denominator had appeared yet.

GHCR update:

- pushed `ghcr.io/suikano1304/kavita-gds:9.0.6-1-amd64`
- amd64 digest: `sha256:<redacted>`
- pushed `ghcr.io/suikano1304/kavita-gds:9.0.6-1-arm64`
- arm64 index digest: `sha256:<redacted>`
- arm64 image manifest: `sha256:<redacted>`
- public tags `ghcr.io/suikano1304/kavita-gds:9.0.6-1` and `latest` updated to multi-arch digest `sha256:<redacted>`
- final manifest platforms:
  - `linux/amd64`: `sha256:<redacted>`
  - `linux/arm64`: `sha256:<redacted>`

Release asset update:

- rebuilt local amd64 Docker archive from `local/kavita-gds:9.0.6-1` and `local/kavita-gds:latest`
- `kavita-gds.tar.gz`: `<redacted-hash>`
- `docker-image/kavita-gds.docker.tar`: `<redacted-hash>`
- GitHub Release `v9.0.6-1` assets `kavita-gds.tar.gz` and `SHA256SUMS` were replaced with `--clobber`.

Latest fixture full-reader validation on the low-memory image:

- validation script: `scripts/validate_kavita_fixtures.py`
- container image ID: `sha256:<redacted>`
- target: `LOCAL-FIXTURES` (`LibraryId=10`)
- DB summary before pass: `27` series, `118` files
- format summary: archive `65`, EPUB `31`, TXT `22`
- top-level fixture file counts: CBZ `39`, ZIP `26`, EPUB `27`, EPUB problem `4`, TXT `22`

Each pass forced a scan and then validated every fixture chapter through the API:

- `reader/chapter-info` HTTP 200 and page count match
- `reader/next-chapter` and `reader/prev-chapter` HTTP 200 with integer response
- archive first/middle/last `reader/image` HTTP 200 and non-zero bytes
- EPUB/TXT first/middle/last `book-page` HTTP 200 and non-zero bytes
- EPUB `book-info` and `chapters` HTTP 200
- DB `Pages=0` count and missing chapter cover count remained `0`

Result:

```text
image=sha256:<redacted>
pass=1 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=1 last_scanned=2026-06-02 08:30:52.7179298
pass=2 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 last_scanned=2026-06-02 08:31:15.7979326
pass=3 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 last_scanned=2026-06-02 08:31:37.6696307
```

## 2026-06-02 GDS File Discovery Memory Rebuild

운영 `성인 만화` 강제 스캔이 이전 low-memory post-scan 경로에 도달하기 전 `Beginning file scan` 이후 파일 탐색/파싱 단계에서 다시 OOM으로 재시작되는 것을 확인했다. 따라서 후반 DB/커버/word-count 순차화와 별개로, GDS 파일 discovery 자체의 메모리 사용을 줄이는 추가 빌드를 만들었다.

Source change:

- source: `<local-kavita-source>`
- branch: `codex/gds-0906`
- source commit: `e922205 fix: reduce GDS scan discovery memory`
- file: `Kavita.Services/Scanner/ParseScannedFiles.cs`
- GDS 라이브러리만 recursive directory list materialization 대신 bottom-up streaming directory scan 경로를 사용한다.
- GDS 파일 파싱은 파일 수만큼 `Task`를 생성하지 않고 sequential parse로 제한한다.
- `TrackSeriesAcrossScanResults()`에서 `ParserInfo` 전체 flatten list를 만들지 않고 scan result를 streaming iteration한다.
- changed folder parse가 끝나면 `ScanResult.Files`를 비워 파일 경로 list 보유 시간을 줄인다.

Test image:

- image: `local/kavita-gds:9.0.6-1-streamscan-test`
- Image ID: `sha256:<redacted>`
- `local/kavita-gds:9.0.6-1-lowmem-test`도 같은 이미지 ID로 retag 후 `kavita-test`에 반영했다.

Fixture full-reader validation:

```text
image=sha256:<redacted>
pass=1 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=1 last_scanned=2026-06-02 08:42:59.432797
pass=2 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 last_scanned=2026-06-02 08:43:22.0865337
pass=3 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 last_scanned=2026-06-02 08:43:43.8868232
```

Production rollout status:

- `local/kavita-gds:9.0.6-1` was retagged to Image ID `sha256:<redacted>`.
- production `kavita` was recreated from `/opt/compose/kavita/docker-compose.yml`.
- health became `healthy` after restart.
- production `성인 만화` (`LibraryId=3`) forced scan was started through the API at `2026-06-02 08:45:08 KST`.
- At `2026-06-02 08:51 KST`, the scan was still in file discovery before `Found N Series`; production `kavita` remained `healthy`, restart count was `0`, and memory was below `1GiB / 16GiB`.

Note: This earlier fixture corpus had strong per-file coverage but did not yet prove the literal "10 series per format folder" target. That target was completed later in the `2026-06-02 Local Fixture 10-Series Expansion` section.

## 2026-06-02 Skip-Word Final Build / Production Scan / ARM64 Publish

대형 `production-library-c` 운영 스캔에서 per-series 단계가 EPUB word-count 재분석 때문에 과도하게 느려지는 것을 확인했다. 사용자가 요청한 강제 스캔 목적은 커버 재생성과 누락 커버 회복이므로, GDS 라이브러리 scan path에서는 `WordCountAnalyzerService.ScanSeries(...)`를 호출하지 않도록 분리했다.

Source / package:

- source commit: `17253b3 fix: skip GDS word count during library scans`
- package commit: `5b02950 fix: skip GDS word count in library scans`
- docs commits: `a20e6fd`, `e1e8ada`
- source checksum parity between host repo and lxc1 build source was verified for `ScannerService.cs` and `ProcessSeries.cs`.

Fixture validation on final image:

```text
image=sha256:<redacted>
LOCAL-FIXTURES forced scan: Finished library scan of 118 files and 27 series in 12013 ms
pass=1 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 total=118 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
WordCountAnalyzerService logs in fixture scan range=0
```

Production rollout:

```text
production image ID=sha256:<redacted>
container status=running
health=healthy
restart count=0
OOMKilled=false
observed memory after final scan=about 702MiB / 16GiB
```

Final `production-library-c` forced scan:

```text
2026-06-02 16:34:39 KST Beginning file scan on production-library-c
2026-06-02 18:39:58 KST Found 13675 Series that need processing in 7519036 ms
2026-06-02 18:39:58 KST Using low-memory sequential GDS scan path for production-library-c. Series to process: 13675
2026-06-02 19:46:55 KST Finished library scan of 63039 files and 13675 series in 11536369 ms
Finished series update count=13675
WordCountAnalyzerService logs in final scan range=0
critical/fatal/OOM logs in final scan range=0
```

Library `LastScanned` after final scan:

```text
<redacted>|production-library-a|2026-06-02 12:34:55.5868943
2|연재중|2026-06-02 13:25:30.6796249
3|성인 만화|2026-06-02 09:29:16.4414823
<redacted>|production-library-a|2026-06-02 07:03:46.5457951
<redacted>|production-library-b|2026-06-02 06:52:21.3806613
<redacted>|production-library-b|2026-06-02 06:55:46.6257085
<redacted>|production-library-e|2026-06-02 06:58:07.8451872
<redacted>|production-library-c|2026-06-02 19:46:55.4197661
<redacted>|production-library-d|2026-06-02 07:08:53.2575256
```

rclone read-only:

```text
rclone-gds.service=active/running
ExecStart includes --read-only
NRestarts=0
recent /var/log/rclone.gds.log: to upload 0, uploading 0
```

Final GHCR publish:

```text
ghcr.io/suikano1304/kavita-gds:9.0.6-1-amd64
linux/amd64=sha256:<redacted>

ghcr.io/suikano1304/kavita-gds:9.0.6-1-arm64
arm64 index digest=sha256:<redacted>
linux/arm64=sha256:<redacted>

ghcr.io/suikano1304/kavita-gds:9.0.6-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:<redacted>
linux/amd64=sha256:<redacted>
linux/arm64=sha256:<redacted>
```

Final release assets:

```text
kavita-gds.tar.gz sha256=<redacted-hash>
docker-image/kavita-gds.docker.tar sha256=<redacted-hash>
```

## 2026-06-02 Local Fixture 10-Series Expansion

운영 스캔과 public image/GHCR 등록 완료 후, goal의 literal 요구였던 포맷별 10 series fixture를 맞추기 위해 `LOCAL-FIXTURES`를 추가 확장했다. 이후 사용자 지정 EPUB 문제 샘플인 `reported page-count EPUB sample`과 `reported duplicate-manifest EPUB sample`도 원본 하위 구조를 보존해 추가했다. 모든 추가 파일은 GDS read-only 원본에서 읽어 local fixture root에만 복사했다.

확장 후 filesystem 요약:

```text
cbz series=<redacted>
zip series=<redacted>
epub series=<redacted>
txt series=<redacted>
epub-problem groups=2

cbz media files=44
zip media files=30
epub media files=54
txt media files=27
total validated media files=155
fixture root size=882M
```

추가 source directory:

```text
CBZ <redacted-media-path>
CBZ <redacted-media-path>
CBZ <redacted-media-path>
CBZ <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
TXT <redacted-media-path>
TXT <redacted-media-path>
TXT <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
```

`kavita-test` 상태:

```text
image=sha256:<redacted>
status=running
health=healthy
restart count=0
```

3회 강제 scan / reader 검증:

```text
Finished library scan of 155 files and 44 series in 11196 milliseconds for LOCAL-FIXTURES
Finished library scan of 155 files and 44 series in 12234 milliseconds for LOCAL-FIXTURES
Finished library scan of 155 files and 44 series in 11177 milliseconds for LOCAL-FIXTURES

pass=1 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=1 last_scanned=2026-06-02 20:22:26.7537176
pass=2 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 last_scanned=2026-06-02 20:22:52.0716544
pass=3 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 last_scanned=2026-06-02 20:23:17.7408709
```

DB 요약:

```text
Series=44
MangaFile=155
zero_pages=0
missing_covers=0
Archive rows=74
EPUB rows=54
TXT rows=27
```

사용자 지정 샘플 DB 확인:

```text
reported cover-only EPUB sample: pages=1, cover=<redacted-cover-file>
reported page-count EPUB sample: TXT pages=76, EPUB pages=12-15, cover references present
reported duplicate-manifest EPUB sample: TXT pages=48, EPUB pages=12-13, cover references present
```

rclone read-only 확인:

```text
rclone-gds.service=active/running
ExecStart includes --read-only
NRestarts=0
recent /var/log/rclone.gds.log: to upload 0, uploading 0
```
