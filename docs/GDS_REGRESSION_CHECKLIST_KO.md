# Kavita-GDS 배포 전 회귀 검증 체크리스트

작성일: 2026-06-06
정책 갱신: 2026-06-10

이 문서는 새 upstream Kavita 버전으로 포팅하거나 새 GHCR 이미지를 배포하기 전에 반복해야 하는 공개용 회귀 검증 체크리스트다. 실제 작품명, raw id, media path가 필요한 상세 매트릭스는 공개 저장소 밖의 로컬 운영 문서에만 보관한다.

## 기본 배포 gate

배포 전 기본값은 로컬 회귀 매트릭스의 모든 issue class를 판정하는 것이다. "몇 개 샘플만 확인"은 빠른 smoke에만 허용하고, release 또는 production 반영 전에는 로컬 매트릭스의 각 issue class를 `PASS`, `SOURCE/DB DEBT`, `FUTURE POLICY`, `NOT RETESTED WITH REASON`, `FAIL` 중 하나로 분류한다.

- `FAIL`은 release/production 반영을 막는다.
- `NOT RETESTED WITH REASON`은 이유와 재검증 조건을 기록해야 하며, 무기한 PASS처럼 취급하지 않는다.
- source-only 문제도 server가 Fatal/SQLite/database-lock 없이 reader/API/scan 경로를 견디는지 확인한다.
- 새 upstream 버전으로 포팅할 때는 official upstream base 위에 하나의 GDS patch stack을 얹은 형태인지 확인한다. 이전 GDS 릴리스를 장기 base로 삼아 upstream 변경을 부분 복사하는 방식은 release gate를 통과한 것으로 보지 않는다.
- 이번 release 검증 중 새롭게 발견된 code-fixable 회귀나 current-version failure는 기본적으로 차후 릴리즈로 미루지 않는다. 로컬 매트릭스에 기록하고, 필요한 경우 공개 체크리스트에는 익명화된 issue class만 추가한 뒤, 현재 release candidate에 패치하고 재검증한다.
- `FUTURE POLICY`는 명시적인 제품/정책 결정 항목에만 사용한다. correctness, crash, reader/API, scan, cache, SQLite, startup, health, operational stability failure는 현재 candidate에서 수정/검증하거나 `FAIL` release blocker로 남긴다.
- 새 버전마다 공개 저장소 밖의 로컬 감사 문서에 상태표를 남긴다.

상태표 최소 컬럼:

```text
issue class | previous status | current version result | validation method | blocker | follow-up action
```

## 필수 회귀 영역

배포 전에는 최소한 아래 유형을 `kavita-test`에서 확인한다.

- GDS EPUB page-count: `1/1`로 고정되던 EPUB이 `book-info` 진입 후 실제 page count로 보정되는지 확인.
- duplicate EPUB manifest: duplicate item id 또는 duplicate `href + media-type` EPUB이 `book-info`, TOC, page, resource fetch에서 실패하지 않는지 확인.
- missing NAV EPUB: EPUB3 NAV item이 없어도 fallback TOC/page 경로가 정상 응답하는지 확인.
- cover-only EPUB: 원본에 본문 XHTML이 없는 파일은 reader 실패가 아니라 source-file 한계로 분류되는지 확인.
- single-spine TOC EPUB: 하나의 XHTML spine 안에 여러 TOC anchor가 있을 때 backend virtual pages가 유지되는지 확인.
- TXT pagination: 긴 TXT와 사용자 보고 TXT fixture가 첫 page와 마지막 page 모두 정상 응답하는지 확인.
- ZIP/CBZ reader: `chapter-info`, file dimensions, image, thumbnail, negative page clamp가 정상인지 확인.
- WebUI unnamed metadata filter default: metadata filter에서 smart filter 이름을 비운 상태로 정렬/필터를 저장하면 현재 route의 기본 filter로 저장되고, 같은 route 재진입 시 해당 기본값이 적용되는지 확인한다. 이름을 입력한 smart filter 저장 동작은 기존처럼 유지되어야 한다.
- PDF reader: raw PDF serving과 extracted image rendering이 모두 정상인지 확인.
- cover fallback: folder/YAML/TXT/ZIP-CBZ first-page cover fallback이 source media mount에 쓰지 않고 config cover storage만 사용하는지 확인.
- TXT YAML cover precedence/refresh: TXT-only 신규 import에서 `kavita.yaml`의 file-level base64 cover가 title fallback cover보다 우선되는지, 기존 TXT title cover가 있는 상태에서도 강제 refresh로 실제 교체되는지 확인.
- mixed EPUB+TXT representative cover: 같은 normalized series 안에 TXT와 EPUB이 함께 있을 때 TXT title fallback이 EPUB/YAML/media cover를 대표 series/volume cover에서 밀어내지 않는지 확인. chapter cover는 각 source file 기준으로 유지되고, series cover와 volume cover는 folder/YAML/media cover를 TXT title fallback보다 우선해야 한다.
- cover update WebUI cache: cover refresh 또는 series scan 후 backend 대표 cover가 바뀌었을 때 home/list/detail view가 고정 이미지 URL의 stale browser cache를 재사용하지 않는지 확인한다. cover image URL은 갱신 event 이후 cache-buster를 포함해야 하며, cover endpoints는 no-cache headers를 반환해야 한다.
- valid EPUB cover extraction: OPF/spine과 cover asset이 정상인 EPUB chapter에서 chapter cover가 비어 있지 않고 representative cover 후보로 사용되는지 확인.
- malformed-but-readable EPUB tolerance: 0-byte가 아닌 EPUB의 OPF manifest/spine/cover resource 이상이 scan 전체 실패나 server exception으로 번지지 않고 source issue 또는 tolerant fallback으로 분류되는지 확인.
- large book-category scan warnings: 대형 책 카테고리 scan 후 `Unable to determine page count`, EPUB manifest, PDF parse warning을 샘플링해 source defect와 reader/parser regression을 구분하는지 확인.
- mixed-root series scan safety: 여러 library/root/category의 파일이 한 series에 붙은 경우 `/api/series/scan`이 상위 category root를 광범위하게 재귀 스캔하지 않는지 확인한다. 실제 기존 file parent directories만 scan root로 사용하고, library root는 targeted scan root로 쓰지 않아야 한다.
- GDS mixed-format scan batching: 기존 GDS series에 TXT/EPUB/PDF/archive 파일이 함께 붙어 있는 경우 format이 다르다는 이유로 parsed groups 일부가 누락되지 않는지 확인한다.
- series scan completion logging: targeted series scan이 file scan/update 후 `scan work completed`와 post-scan cleanup enqueue 로그를 즉시 남기고, abandoned metadata cleanup이 다음 scan을 막지 않는지 확인한다.
- bulk cover normalization safety: 운영 또는 production-clone에서 stale representative cover를 대량 정규화할 때는 WebUI health latency gate, 작은 batch size, scan completion log 대기를 적용한다. 연속 series scan 후 .NET ThreadPool CPU가 해소되지 않거나 home/list API가 초 단위로 느려지면 즉시 중단하고 원인을 기록한다.
- mixed-format series: 같은 작품 폴더의 ZIP/EPUB/TXT/PDF가 format별 별도 series로 갈라지지 않는지 확인.
- word-count mixed files: EPUB-format series 안의 PDF/TXT 파일이 EPUB word-count 경로로 열리지 않는지 확인.
- loose web-novel images: cover/capture loose `.jpg`가 bogus series로 ingest되지 않는지 확인.
- malformed sidecar YAML: broken `kavita.yaml`이 media import 전체를 막지 않는지 확인.
- GDS scan memory: file discovery와 post-scan DB/cover/word-count 단계에서 OOM/restart가 없는지 확인.
- GDS targeted ScanSeries follow-up work: GDS 시리즈 단위 스캔 후 word-count 분석과 전역 metadata/cache cleanup이 실행되지 않고, series-local cleanup만 남는지 확인.
- release documentation sync: GHCR/GitHub Release publish 후 `README.md`, `docs/USAGE_KO.md`, `docs/BUILD_NOTES_KO.md`, `docs/NEXT_RELEASE_CHECKLIST_KO.md`, compose 예시, `RELEASE_NOTES.md`, `docs/CHANGELOG_KO.md`의 current version, pull 예시, digest, platform list가 모두 같은 릴리스 태그를 가리키는지 확인한다. GitHub Release tag도 최종 문서 커밋을 가리켜야 한다.
- Web UI production bundle: runtime `.js/.html/.css`에 `localhost:5000`, `:5000/api`, Angular development-mode 문자열이 없는지 확인.
- ARM runtime: `linux/arm64`와 `linux/arm/v7`가 pushed GHCR image에서 `/api/health` 200에 도달하는지 확인. qemu startup은 Docker healthcheck보다 느릴 수 있으므로 `/api/health` 200을 우선 판정하고 Docker health는 참고값으로 기록한다.

## 표준 실행 절차

1. 로컬 회귀 매트릭스, 최신 로컬 full-matrix audit, 이 공개 체크리스트를 읽고 시작 로그에 문서 목록을 남긴다.
2. official upstream base ref/commit과 fork patch stack head commit을 기록한다.
3. patch-stack summary/privacy scan을 실행하고, export/apply self-test가 같은 base에서 tree equality `PASS`를 내는지 확인한다.
4. unit/service tests를 먼저 실행한다.
5. 운영 `kavita`는 건드리지 않는다.
6. `kavita-test`만 새 이미지로 재생성한다.
7. production DB clone과 원본 GDS 상대 폴더 구조를 보존한 copied fixture를 사용한다.
8. GDS fixture와 source mount는 read-only bind로 유지한다.
9. extended verifier와 로컬 매트릭스 issue class별 상태표를 실행/작성한다.
10. 운영 targeted validation은 필요한 항목만 수행한다.
11. 운영 broad scan/full verifier는 CPU, health, WebUI, Plex/Jellyfin, host I/O gate를 통과하고 필요성이 있을 때만 수행한다.
12. `linux/arm64`와 `linux/arm/v7` smoke test를 실행한다.
13. GHCR version tag와 `latest` manifest를 확인한다.
14. `kavita-test`, ARM smoke containers, BuildKit을 정지한다.
15. 결과를 release notes와 검증 기록에 남긴다.

## Upstream Base + GDS Patch Stack Gate

새 upstream 버전으로 올릴 때는 다음을 release blocker로 취급한다.

- source branch가 official upstream base에서 시작되었고, GDS 변경이 fork-only patch stack으로만 올라가 있는지 확인한다.
- upstream scanner/auth/migration/reader/UI 변경은 기본적으로 보존하고, GDS 회귀 매트릭스가 요구하는 동작만 override한다.
- patch-stack helper self-test가 export, apply, public-doc privacy scan, same-base tree equality를 통과해야 한다.
- architecture별 차이는 source patch가 아니라 RID package와 Docker `TARGETPLATFORM` 선택으로만 처리한다.
- 어떤 architecture도 `/api/health` runtime smoke가 통과하기 전에는 GHCR version/latest manifest에 포함하지 않는다.

## Extended Verifier

release image 검증 예:

```bash
pct push 101 /path/to/verify-0907-gds-extended.sh /tmp/verify-gds-extended.sh
pct exec 101 -- bash -lc 'chmod +x /tmp/verify-gds-extended.sh && EXPECTED_IMAGE=ghcr.io/suikano1304/kavita-gds:<version> EXPECTED_VERSION=<official-version> /tmp/verify-gds-extended.sh'
```

PASS 기준:

- container health `healthy`
- expected image/version 일치
- SQLite integrity before/after `ok`
- GDS bind가 read-only
- health/version API HTTP 200
- representative cover API HTTP 200
- TXT, archive, EPUB, PDF sections 전부 PASS
- repeated cache hits HTTP 200
- recent MediaError count 0
- 새 404/500/Fatal/SQLite/database-lock/disk I/O 로그 없음

## Cover Regression Fixture

GDS cover refactor 또는 cover fallback을 건드린 릴리스에서는 local fixture 기반 cover regression을 별도로 반복 실행한다.

```bash
pct push 101 /path/to/prepare-cover-regression-test-config.sh /tmp/prepare-cover-regression-test-config.sh
pct push 101 /path/to/validate-cover-regression-fixture.py /tmp/validate-cover-regression-fixture.py
pct push 101 /path/to/run-cover-regression-validation-gated.sh /tmp/run-cover-regression-validation-gated.sh
pct exec 101 -- bash -lc 'IMAGE=ghcr.io/suikano1304/kavita-gds:<version> SQLITE_IMAGE=ghcr.io/suikano1304/kavita-gds:<version> MAX_IO_FULL_AVG10=8 PASSES=2 bash /tmp/run-cover-regression-validation-gated.sh'
```

PASS 기준:

- fixture library가 원본 GDS 상대 폴더 구조와 sidecar 배치를 보존한다.
- local cover regression library가 2회 force scan을 통과한다.
- YAML base64 cover가 적용되어야 하는 샘플은 cover reference와 cover API bytes가 존재한다.
- `cover: TEXT` 또는 invalid base64 샘플은 TXT title fallback으로 분류된다.
- mixed EPUB+TXT 샘플은 media/YAML cover가 TXT title fallback보다 representative cover에서 우선된다.
- duplicate broken/valid EPUB row 샘플은 첫 DB row가 0-byte 또는 `Pages=0`이어도 reader/cache가 non-zero, non-empty EPUB row를 선택한다.
- cover update 후 같은 entity의 image API URL 또는 응답 헤더가 stale browser cache를 막고, home/list view가 refresh 전 cover bytes로 되돌아가지 않는다.
- mixed-root copied fixture 또는 production-clone test는 실제 file directory roots만 스캔하고 broad category/library root로 확장되지 않는다.
- GDS targeted series scan 로그에 word-count skip과 global post-scan cleanup skip이 남고, `WordCountAnalyzerService` 후속 로그가 없어야 한다.
- GDS targeted series scan 완료 후 30초 이상 container CPU와 `/api/health`를 확인해 ThreadPool CPU spin이 없어야 한다.
- GDS filename completion markers such as `(완결)`, `(완)`, and `(完)` set completed publication metadata after scan or controlled backfill, while ordinary title text containing completion-like words does not.
- Default-sorted card-list JumpBar keys scroll the virtualized list; JumpBar remains disabled for actual non-default/custom sort states.
- Korean locale labels must distinguish `Completed` from `Ended` without rendering `Ended` as missing/lost data.
- release publication consistency check: GHCR image push만으로 릴리스를 완료 처리하지 않는다. `README.md`, `RELEASE_NOTES.md`, changelog, usage/build docs의 version/digest/platform 표기가 모두 새 release tag와 일치해야 하며, `git ls-remote --tags origin <release-tag>`, `gh release view <release-tag>`, `gh release view`, `docker buildx imagetools inspect ghcr.io/suikano1304/kavita-gds:<version>`, `docker buildx imagetools inspect ghcr.io/suikano1304/kavita-gds:latest`가 모두 같은 release version과 expected manifest digest를 가리켜야 한다.
- 0-byte EPUB, malformed EPUB, 본문/cover source가 없는 샘플은 expected source-data issue로만 남고 server exception으로 실패하지 않는다.
- SQLite `quick_check`가 `ok`다.

## ARM Smoke

필요 시 binfmt를 등록한다.

```bash
pct exec 101 -- docker run --rm --privileged tonistiigi/binfmt --install arm64,arm
```

`linux/arm64`와 `linux/arm/v7`를 각각 `--platform`으로 기동하고 `/api/health` 200을 확인한다. qemu startup은 수 분 걸릴 수 있고 Docker healthcheck가 먼저 `starting` 또는 `unhealthy`를 찍을 수 있으므로 최종 `/api/health` 200과 container exit 여부를 함께 기록한다.

## Manifest Check

```bash
pct exec 101 -- docker buildx imagetools inspect ghcr.io/suikano1304/kavita-gds:<version>
pct exec 101 -- docker buildx imagetools inspect ghcr.io/suikano1304/kavita-gds:latest
```

PASS 기준:

- version tag와 `latest`가 같은 multi-arch digest를 가리킨다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` manifest가 모두 있다.
- ARM manifest는 ARM smoke test에서 `/api/health` 200을 확인한 뒤에만 publish manifest에 포함한다.

## 로컬 상세 매트릭스

실제 작품명, raw id, media path, fixture path가 필요한 경우 공개 저장소 밖의 로컬 상세 매트릭스를 확인한다. 이 로컬 문서는 공개 GitHub 문서에 복사하지 않는다.

## 0.9.0.12-5 추가 회귀 체크

- 대형 GDS broad scan에서 파일/디렉터리 열거 후에도 RSS가 계속 증가해 OOM에 접근하지 않는지 확인한다.
- 큰 `kavita.yaml`/`kavita.yml` sidecar가 있어도 필요한 metadata/page hint만 읽고 scan이 완료되는지 확인한다.
- metadata가 비어 있거나 일부만 있는 sidecar가 매 scan마다 재처리 대상으로 반복 분류되지 않는지 확인한다.
- mixed-format GDS series가 대표 format 차이 때문에 매 scan마다 fingerprint miss를 일으키지 않는지 확인한다.
- no-change GDS scan 완료 후 post-scan cleanup CPU tail 없이 idle 상태로 돌아오는지 확인한다.
- 한국어 `N부 M권` 형태의 part/volume 구성이 같은 `01권`으로 합쳐지지 않는지 확인한다.
- 외부에서 파일을 다른 library root로 이동한 뒤 source/destination scan을 수행하면 이전 경로를 가리키는 검색 중복 series가 남지 않고, 새 series의 reader API가 HTTP 200을 반환하는지 확인한다.

## 0.9.1.4 포팅 추가 회귀 항목

- 시리즈 설명의 문자 그대로인 `\n`·`\r\n`, 실제 개행, YAML `|`·`>` 및 빈 문단이 올바르게 표시되는지 확인한다. 기존 DB 설명은 재스캔 없이 표시되어야 하고 HTML 정화는 유지되어야 한다.
- 큰 표지 payload가 있는 sidecar도 metadata만 제한된 크기로 해석하고, 잘못된 Base64 표지는 거부해야 한다.
- 시리즈 이름 변경 후 GDS scan cleanup이 원래 이름·현지화 이름으로 같은 시리즈를 유지하는지 확인한다.
- UTC 읽기 기록 집계가 로컬 날짜 경계(+09:00/-07:00)에서 세션을 누락하지 않는지 확인한다.
- 기존 Android 게임패드 snapshot 갱신 및 EPUB 상·하 스크롤 패치를 유지하고 새 UI에서 검증한다.
