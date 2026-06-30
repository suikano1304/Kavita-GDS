# Kavita-GDS issue/fix inventory - 2026-06-01

## 목적

`0.9.0.2` GDS 빌드에서 누적된 수정사항과 `0.9.0.6-1` 테스트 빌드에 새로 반영한 수정사항을 운영 반영 전에 구분해 기록한다.

검증 완료 전까지 `git commit`, `git push`, GitHub release/package publish는 하지 않는다.

## 기준

- 이전 배포 기준: `0.9.0.2-8`
- 현재 테스트 기준: official Kavita `0.9.0.6` nightly
- 현재 테스트 이미지: `local/kavita-gds:9.0.6-1-test`
- 최종 image id: `sha256:<redacted>`
- 테스트 컨테이너: `kavita-test`
- 테스트 접근: `tkavita.suikano.net`, local port `5658`
- fixture library: `LOCAL-FIXTURES`, path `/fixtures`

## 0.9.0.2 계열에서 유지해야 하는 핵심 수정

출처:

- `<local-kavita-worktree>/0902-GDS-TEST-STATUS.md`
- `[local-private-docs]/KAVITA_GDS_0902_WORKLOG_20260528.md`
- `[local-private-docs]/KAVITA_GDS_SCANFIX_WORKLOG_20260530.md`
- `<public-docs-repo>/RELEASE_NOTES.md`
- current branch history up to `0.9.0.2-8`

유지 대상:

- `LibraryType.GDS = 6` 유지.
- `MangaFormat.Text = 5` 유지. 0.9.6 upstream에서 `Pdf = 4`가 추가되어 Text는 `5`로 유지한다.
- GDS/TXT enum, backend, UI routing 처리.
- TXT page counting and book-reader page rendering.
- GDS scanner/parser routing.
- `.txt` scanner extension and file type grouping.
- `kavita.yaml`, `kavita.yml`, `cover.*`, bracketed cover images 등 metadata/cover 파일을 media로 오인식하지 않도록 필터링.
- GDS folder cover를 Kavita config cover storage로 복사하고 source GDS mount에는 쓰지 않음.
- GDS folder cover를 series/volume/chapter cover에 적용.
- GDS를 외부 metadata matching에서 제외하거나 안전하게 우회.
- TXT/ZIP 등 혼합 format GDS series가 잘못 분리되지 않도록 normalized series matching 보정.
- GDS mixed series에서 `chapter-info`가 실제 chapter format을 반환해 TXT는 book reader, ZIP/CBZ는 image reader로 라우팅.
- duplicate normalized GDS series cleanup 시 TXT survivor를 우선.
- GDS source cleanup/delete 방어.
- GDS folder path and change detection 보정.
- unchanged GDS files의 page/hash 재계산 회피로 scan 속도 개선.
- `Pages=0` scan debt는 force scan으로 복구 가능하게 보정.
- GDS EPUB/PDF/TXT unchanged fast minimum page count로 unreadable 상태 완화.
- GDS sidecar YAML metadata 반영: summary, tags, people, publisher, release date, age rating 등.
- file name 기반 GDS chapter title cleanup.
- TXT fallback title cover generator.
- TXT title cover는 Kavita config cover storage에만 생성.
- Nanum Gothic font를 image에 포함해 한글 TXT cover rendering 보장.
- mixed-format EPUB word-count analyzer가 PDF/TXT를 EPUB로 열지 않도록 skip.
- GDS archive cover fallback: YAML/base64 cover나 TXT title cover가 없으면 ZIP/CBZ 내부 cover/first-page extraction 사용.
- default series sort fallback hotfix: 빈 정렬 조건일 때 최근 수정 내림차순 유지.
- reader/cache/default-sort and GDS startup cleanup stabilization from scanfix worklog.

## 0.9.0.6-1 테스트 빌드에 새로 확인/추가한 수정

### 1. 권별 커버 누락 및 동일 커버 재사용

증상:

- 운영 scan audit에서 multi-file GDS series의 2권 이후 `Chapter.CoverImage`, `Volume.CoverImage`가 비어 있거나 1권과 같은 cover hash로 생성되는 패턴 확인.

수정:

- GDS cover generation이 첫 cover path 이후 return하지 않고 모든 non-text GDS volume/chapter를 처리.
- `kavita.yaml` file-level cover lookup이 대상 파일명에 해당하는 cover만 사용하도록 보정.
- YAML cover를 찾지 못한 경우에만 archive extraction fallback 사용.
- archive에 cover 후보가 없어도 전체 작업이 실패하지 않도록 빈 cover result로 처리.

검증:

- `LOCAL-FIXTURES` CBZ/ZIP/EPUB multi-file samples에서 cover filename과 hash가 chapter별로 다름.
- 대표 샘플: `cbz-sample-a`, `cbz-sample-b`, `zip-sample-a`, `epub-sample-a`, `epub-problem`.

### 2. TXT cover 한글 네모 glyph

증상:

- TXT title cover의 한글이 tofu square로 렌더링됨.

수정:

- runtime image에 `fontconfig`와 Nanum Gothic TTF 포함.
- SVG `font-family`를 fontconfig/Pango가 해석 가능한 family list로 유지.

검증:

- `fc-match NanumGothic` resolves to `NanumGothic-Regular.ttf`.
- TXT fixture cover sample renders Korean text normally.

### 3. malformed EPUB duplicate manifest

증상:

- 일부 generated EPUB가 OPF manifest에 완전 중복 item을 포함해 `VersOne.Epub.EpubPackageException` 발생.

수정:

- 원본 EPUB를 수정하지 않는다.
- Kavita temp path에 임시 repaired copy를 생성한다.
- `id`, `href`, `media-type`가 모두 같은 exact duplicate `manifest/item`만 제거한다.
- reader/service dispose 시 temp repaired copy를 삭제한다.
- `BookService`, `WordCountAnalyzerService`, `BookController` reader paths에 tolerant open wrapper 적용.

검증:

- problem EPUB 3개 `book-info` HTTP 200.
- `refresh-metadata` and `series/analyze` completed.
- word count populated:

```text
problem-chapter-a 180283
problem-chapter-b 207839
problem-chapter-c 201713
```

### 4. EPUB reader resource path

증상:

- EPUB page HTML은 생성되지만 resource URL이 `OEBPS/Styles/../Fonts/...` 형태이거나 manifest key와 file path가 어긋나 `book-resources` 400 발생.

수정:

- EPUB content key 정규화: `.` and `..` segment normalization.
- CSS import/font references에 normalized content key 사용.
- manifest key direct lookup 실패 시 normalized file path, filename fallback으로 resource key coalesce.

검증:

- problem EPUB 3개 first font resources all HTTP 200.

### 5. EPUB page count 1/1

증상:

- problem EPUB가 열리지만 reader가 `1/1`로 표시됨.
- content table에서 page/chapter를 직접 선택하면 이동하지만 next/previous page navigation이 정상 동작하지 않음.

원인:

- GDS scan 최적화가 EPUB/PDF/TXT page count를 실제로 읽지 않고 `1`로 저장.
- 이 shortcut이 remote `/mnt/gds`뿐 아니라 local `/fixtures`에도 적용됨.

수정:

- `/mnt/gds` mount path에서는 기존 shortcut 유지.
- `/fixtures` 같은 local path는 normal Kavita page-count logic 사용.
- force scan 시 local path의 기존 `Pages=1` 값을 그대로 재사용하지 않고 재계산.

검증:

```text
problem-chapter-a Pages 34
problem-chapter-b Pages 49
problem-chapter-c Pages 42
seriesTotalPages 125
book-page middle/last pages HTTP 200
```

### 6. Reader/cache cleanup 경합 로그

증상:

- scan/cache cleanup과 reader page fetch가 겹칠 때 cache directory가 이미 삭제되어 `DirectoryNotFoundException`이 로그에 남음.
- API 응답은 성공했지만 반복 검증 로그에 cleanup/Hangfire failure가 남아 운영 gate로 보기 어려웠다.

수정:

- `DirectoryService.ClearDirectory`가 이미 사라진 child directory 또는 top-level directory를 concurrent cleanup 결과로 보고 조용히 무시하도록 보정.
- 권한 오류는 기존처럼 error log를 유지한다.

검증:

- 최종 3-pass full reader validation 이후 `[Error]`, `Exception`, `Failed to process`, `DirectoryNotFound` 로그 없음.
- force scan 이후에도 같은 error pattern 없음.

### 7. EPUB duplicate manifest 복구 성공 로그 정리

증상:

- duplicate manifest EPUB은 임시 복구본으로 정상 열리지만, 복구 성공 경로가 원래 `EpubPackageException` stack을 warning으로 반복 출력했다.

수정:

- 복구 성공 경로는 stack 없는 warning으로 기록한다.
- 원본 에러 메시지는 유지해 추적 가능하게 하되, 실패처럼 보이는 exception stack은 남기지 않는다.

검증:

- problem EPUB 3개를 포함한 3-pass full reader validation에서 recent error/exception grep 결과 없음.

## 현재 source changes

현재 작업 tree의 주요 변경 파일:

- `Kavita.Server/Controllers/BookController.cs`
- `Kavita.Services/BookService.cs`
- `Kavita.Services/Helpers/EpubManifestRepairHelper.cs`
- `Kavita.Services/Metadata/WordCountAnalyzerService.cs`
- `Kavita.Services/MetadataService.cs`
- `Dockerfile`

최종 diff 기준 변경 내용은 EPUB manifest repair 범위 확대, GDS folder cover 적용 범위 축소, runtime `sqlite3` 및 Nanum Gothic Regular/Bold/ExtraBold 포함이다.

## 3회 반복 fixture 검증 결과

2026-06-02 20:23 KST 최종 기준:

```text
LOCAL-FIXTURES force scan: 155 files / 44 series
Fixture directories: CBZ 10 series / ZIP 10 series / EPUB 10 series / TXT 10 series / EPUB problem 2 groups
Reported fixture samples: reported cover-only EPUB sample, reported page-count EPUB sample, reported duplicate-manifest EPUB sample
DB: 155 media rows / 44 series / zero pages 0 / missing covers 0
Archive: 74 files / zero pages 0 / missing covers 0
EPUB: 54 files / zero pages 0 / missing covers 0
TXT: 27 files / zero pages 0 / missing covers 0

pass=1 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
recent error/exception logs: none
```

Problem EPUB navigation:

```text
problem-chapter-b next=problem-chapter-a prev=-1
problem-chapter-a next=problem-chapter-c prev=problem-chapter-b
problem-chapter-c next=-1 prev=problem-chapter-a
```

NPM and rclone:

- `tkavita.suikano.net/api/health`: `Ok`
- `rclone-gds.service`: active
- rclone RC: `errors=0`, `deletes=0`, `renames=0`, server-side copy/move `0`
- rclone RC still shows read transfer activity, but no delete/rename/upload/write counters.

## 남은 gate

- 운영 Web UI에서 사용자가 redacted page-count and duplicate-manifest EPUB samples, 다른 EPUB 대표 샘플을 직접 재확인.
- 운영 안정성 확인 후 GitHub commit/push/release를 별도 단계로 수행.

## 운영 반영 후 추가 이슈: GDS EPUB page count와 duplicate href

### 8. 운영 EPUB `1/1` 표시

증상:

- test fixture에서는 EPUB가 정상 페이지 수로 열렸지만, 운영 `/mnt/gds` EPUB는 `1/1`로 표시됨.
- `reported page-count EPUB sample` 1권/2권이 대표 재현 케이스.

원인:

- test fixture는 `/fixtures` local path라 force scan에서 실제 page count를 계산했다.
- 운영 `/mnt/gds`는 scan shortcut으로 EPUB를 읽지 않고 `Pages=1`을 유지했다.
- 운영 scan에서 원격 EPUB를 실제로 읽도록 바꿨을 때 Web UI 응답성이 나빠져 scanner 쪽 보정은 유지할 수 없었다.

수정:

- `/mnt/gds` scanner shortcut은 유지한다.
- EPUB `book-info` 요청에서 실제 reading order count를 계산하고 DB 값이 `1` 이하일 때 즉시 `Chapter.Pages`, `MangaFile.Pages`, `Volume.Pages`, `Series.Pages`를 보정한다.
- `book-info`는 no-store로 바꿔 stale `1/1` 응답을 줄인다.

검증:

```text
sample-chapter-a reported page-count EPUB sample 1권: book-info HTTP 200, pages=15, DB Chapter/MangaFile pages=15
sample-chapter-b reported page-count EPUB sample 2권: book-info HTTP 200, pages=12, DB Chapter/MangaFile pages=12
sample-chapter-a book-page page=2 HTTP 200
```

### 9. `reported duplicate-manifest EPUB sample` duplicate EPUB manifest href

증상:

- 운영 Web UI에서 `Incorrect EPUB manifest: item with href = "image-0001.jpg" is not unique.` 오류 발생.

수정:

- `EpubManifestRepairHelper`가 duplicate exact item, duplicate id, duplicate `href + media-type` manifest item을 임시 EPUB copy에서 제거한다.
- spine itemref가 제거된 item id를 참조하면 retained item id로 rewrite한다.
- `BookService`의 EPUB open 경로 전체에 repair fallback을 적용해 `book-info`, `book-page`, `chapters`, resource fetch, metadata/word-count가 같은 처리를 사용한다.

검증:

```text
sample-chapter-c reported duplicate-manifest EPUB sample 1권: book-info HTTP 200, pages=13
sample-chapter-c book-page page=2 HTTP 200, 3739 bytes
sample-chapter-c chapters HTTP 200, 1017 bytes
sample-chapter-d reported duplicate-manifest EPUB sample 2권: book-info HTTP 200, pages=12
sample-chapter-d book-page page=2 HTTP 200, 44497 bytes
sample-chapter-d chapters HTTP 200, 977 bytes
```

운영 image:

```text
local/kavita-gds:9.0.6-1
intermediate sha256:<redacted>
final sha256:<redacted>
```

rclone:

```text
errors=0 deletes=0 renames=0 serverSideCopies=0 serverSideMoves=0
```

### 10. EPUB3 NAV item 누락

증상:

- `reported cover-only EPUB sample`에서 `EPUB parsing error: NAV item not found in EPUB manifest.` 오류가 발생했다.
- 동일 파일을 test fixture에 복사해 재현했다.

원인:

- 파일은 EPUB3 `version="3.0"`이지만 manifest에 `properties="nav"` item이 없다.
- 파일 내부 entry는 `cover.jpg`, `cover.xhtml`, `toc.ncx`, `content.opf`뿐이며, 본문 XHTML은 없다.
- OPF spine도 `cover.xhtml` 하나만 참조한다.

수정:

- `EpubManifestRepairHelper`가 임시 repaired EPUB copy를 만들 때 EPUB3 nav item이 없으면 최소 `kavita-nav.xhtml`을 합성한다.
- fallback catch 범위를 `EpubReaderException`으로 넓혀 missing NAV도 duplicate manifest와 같은 repair 경로로 처리한다.
- 적용 경로:
  - `BookService`
  - `BookController`
  - `WordCountAnalyzerService`

검증:

```text
test fixture chapter sample-chapter-cover-only book-info HTTP 200
test fixture chapter sample-chapter-cover-only book-page?page=0 HTTP 200
test fixture chapter sample-chapter-cover-only chapters HTTP 200
production chapter <redacted> book-info HTTP 200
production chapter <redacted> book-page?page=0 HTTP 200
production chapter <redacted> chapters HTTP 200
```

제한:

- 이 수정은 reader 500을 막는 복구다.
- 해당 원본 EPUB 자체에 본문 파일이 없어 운영/테스트 모두 `Pages=1`이 맞다.
- 440화 본문을 보려면 GDS 원본 EPUB을 본문 포함 파일로 교체해야 한다.

운영 image:

```text
local/kavita-gds:9.0.6-1
sha256:<redacted>
```
