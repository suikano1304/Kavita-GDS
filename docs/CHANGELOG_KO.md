# 0.9.1.4-1

- 공식 0.9.1.4 기반 이식, 시리즈 설명 줄바꿈 복원, 라이브러리 공급자 검증·기존 GDS DB 수동 마이그레이션·UTC 읽기 기록 집계 보정.
- 상세 검증과 배포 digest: [RELEASE_NOTES.md](../RELEASE_NOTES.md).

# 변경 내역

기준 버전: `kavita-gds-0.9.0.2-scan-20260528`

현재 공개 릴리즈: `0.9.0.12-6`

참고: 운영 컨테이너가 이전 태그를 계속 쓰는 경우, source/release/운영 기준이 다시 달라질 수 있습니다. 운영 검증은 적용 전 baseline과 적용 후 postflight를 같은 진단 스크립트로 비교하세요.

## 2026-07-05: `0.9.0.12-6` GDS scan 처리 메모리 및 커버 생성 보강

- GDS 처리 단계에서 `ParsedSeries` 전체 값 목록을 오래 붙잡지 않도록 삭제 감지에는 normalized series key만 사용합니다.
- 처리 대상 queue는 무거운 series result 객체 대신 index 목록으로 유지하고, skip/처리 완료 후 parser metadata 참조를 즉시 비웁니다.
- scan 종료 시 GDS sidecar metadata cache를 명시적으로 비워 대형 sidecar scan 이후 retained memory를 줄입니다.
- scan 중에는 대표 cover만 빠르게 보강하고, 전체 volume/chapter cover refresh는 metadata refresh 경로에서 수행하도록 분리했습니다.
- sidecar cover base64는 fingerprint/metadata scan 중 중복 decode하지 않고 실제 thumbnail 생성 시점에만 검증합니다.
- `tkavita` fixture 검증에서 변경 없음 재스캔 처리 대상 `0`, 파일 추가 후 처리 대상 `1`, sidecar 변경 후 재처리, 다권 합성 53개 chapter cover 누락 `0`, 재스캔 처리 대상 `0`을 확인했습니다.
- Windows Docker Desktop에서 `linux/amd64`, `linux/arm64`, `linux/arm/v7` pushed GHCR image가 `/api/health=Ok`에 도달함을 확인했습니다.
- 운영 적용은 기존 운영 스캔 완료 후 별도 postflight와 함께 진행합니다.

## 2026-07-04: `0.9.0.12-5` GDS scan phase 메모리 및 대형 sidecar 보강

- 대형 GDS 라이브러리 스캔에서 파일 스캔/파싱 단계가 끝나기 전에 RSS가 급증할 수 있던 구조를 보강했습니다.
- GDS 라이브러리는 디렉터리별 `ScanResult`와 parser list를 전체 root 파싱 완료까지 보관하지 않고, 디렉터리 단위로 파싱 후 즉시 series grouping에 반영합니다.
- 운영 로그에서 새 경로를 확인할 수 있도록 GDS streaming scan 시작/그룹 완료 로그를 Information 레벨로 남깁니다.
- 대형 `kavita.yaml`/`kavita.yml`은 필요한 `meta` scalar와 file page hint만 streaming으로 읽어 full YAML deserialize와 불필요한 cover payload retention을 피합니다.
- GDS YAML page hint와 파일명 `#숫자` page marker를 우선 사용해 broad scan 중 불필요한 archive open을 줄였습니다.
- sidecar metadata가 의도적으로 비어 있거나 일부만 있는 series를 매 scan마다 metadata backfill 대상으로 다시 잡지 않도록 했습니다.
- mixed-format GDS series의 fingerprint lookup을 normalized series identity 기준으로 안정화해 대표 format 차이 때문에 같은 series가 반복 재처리되는 문제를 줄였습니다.
- GDS broad scan 완료 후 synchronous abandoned metadata cleanup을 건너뛰어 no-change scan 이후 긴 CPU 작업으로 이어지는 상황을 줄였습니다.
- 동일 series 아래 한국어 `N부 M권` 형태의 part/volume 파일이 단순 volume number로 합쳐지지 않도록 parser를 보강했습니다.
- 운영 대형 GDS 라이브러리 검증에서 `4668` series parse, 처리 대상 `0`건, 약 `148`초 완료, 완료 후 container memory 약 `350 MiB`, `/api/health=Ok`를 확인했습니다.
- `0.9.0.12-4`의 fingerprint skip, 처리 단계 parser 참조 해제, content freshness 기반 "마지막 수정" 정렬은 유지합니다.

## 2026-07-03: `0.9.0.12-4` GDS 스캔 메모리 및 최신성 정렬 보강

- GDS 전체 스캔 처리 단계에서 `ParserInfo`/`ComicInfo` 목록을 끝까지 보관하지 않도록, 삭제 감지용 `ParsedSeries` 키와 실제 처리 대상 키를 분리했습니다.
- fingerprint가 같아 skip된 series는 즉시 parser metadata 참조를 비우고, 처리 대상 series도 한 series 처리 직후 참조를 비워 대형 라이브러리 스캔 중 유지되는 heap을 줄였습니다.
- GDS 경로는 전체 `toProcess` 묶음을 만들지 않고 series 단위 저메모리 순차 처리 루프를 사용합니다.
- 사용자-facing "마지막 수정" 기준인 `ContentLastModified`가 파일 timestamp뿐 아니라 새 Chapter/MangaFile의 DB 생성 시각도 포함합니다. 파일 timestamp가 과거로 보존된 신규 콘텐츠도 최근 항목으로 정렬될 수 있습니다.
- 홈 "최근 업데이트 시리즈"에서 전체 목록으로 이동할 때 `LastChapterAdded`가 아니라 라이브러리의 "마지막 수정"과 같은 `LastModifiedDate` desc 정렬을 사용합니다.
- `kavita.yaml`/sidecar 변경은 fingerprint mismatch를 일으키지만, sidecar 자체 변경 시각은 사용자-facing "마지막 수정" 날짜에 포함하지 않습니다.

## 2026-07-03: `0.9.0.12-3` GDS 스캔 fingerprint 및 콘텐츠 수정일 분리

- GDS Library Scan은 신규 파일 누락을 막기 위해 폴더 mtime skip을 사용하지 않고 계속 디렉터리/파일을 열거합니다.
- 열거된 series별 파일 경로, 크기, `LastWriteTimeUtc`, `CreationTimeUtc`, format/확장자, `kavita.yaml`/`kavita.yml`/`.special`/cover sidecar 상태로 scan fingerprint를 계산합니다.
- 이전 fingerprint와 같은 GDS series는 `ProcessSeries`를 건너뛰어 실제 변경 없는 기존 시리즈 재처리를 줄입니다.
- `Series.LastModified`는 DB 엔티티 수정일로 유지하고, 실제 콘텐츠 파일 기준 날짜인 `ContentLastModified`를 추가했습니다.
- WebUI의 "마지막 수정" 정렬과 오른쪽 JumpBar 날짜는 `ContentLastModified`를 사용합니다.
- `kavita.yaml` 변경은 fingerprint mismatch를 일으켜 재처리 대상이 되지만, 사용자-facing "마지막 수정" 날짜는 바꾸지 않습니다.
- scan pass 안에서 `kavita.yaml`/`kavita.yml` 파싱을 경로별로 캐시해 remote-backed filesystem의 반복 sidecar 읽기를 줄였습니다.
- Docker fixture 검증에서 동일 fixture 재스캔은 `Found 0 Series that need processing`, `kavita.yaml`만 변경한 경우 콘텐츠 날짜 유지, 신규 archive 추가 시 콘텐츠 날짜 상승을 확인했습니다.
- `tkavita` fixture 검증에서 `/api/health=Ok`, Docker health `healthy`, Series API `contentLastModified`, GDS fingerprint skip 로그를 확인했습니다.
- 프로덕션 `kavita`를 `0.9.0.12-3`로 교체했고 `/api/health=Ok`, Docker health `healthy`, 신규 `Series` migration column 적용을 확인했습니다.

## 2026-07-02: `0.9.0.12-2` 한국어 검색 공백/유니코드 정규화 개선 (GDS 자체 패치)

- `ToNormalized()`에 유니코드 NFC 정규화를 추가했습니다. 일부 입력기/OS에서 생성되는 분해형(NFD) 한글 자모가 완성형(NFC) DB 데이터와 검색 매칭에 실패하던 문제를 해결합니다.
- `Chapter.NormalizedTitleName`, `Library.NormalizedName` 필드를 신규 추가했습니다. 챕터 제목과 라이브러리 이름도 Series와 동일하게 띄어쓰기 무관 검색이 가능해졌습니다.
- 검색 쿼리 정규화 불일치 버그 3건을 수정했습니다: `AppUserCollection` 검색이 정규화 필드를 정규화되지 않은 검색어로 비교하던 문제, `ReadingList` 검색이 `NormalizedTitle`을 전혀 사용하지 않던 문제, `Library` 검색에 정규화 필드가 아예 없던 문제.
- `Series` 검색에서 미사용이던 `NormalizedLocalizedName` 필드를 검색 조건에 포함했습니다.
- 신규 `ManualMigrateKoreanSearchNormalizationBackfill` 마이그레이션으로 기존 Series/Chapter/Library/Tag/Genre/Person/ReadingList/Collection의 정규화 필드를 전수 재계산합니다. 전체 라이브러리 재스캔 없이 서버 시작 시 1회 자동 적용됩니다.
- 초성 검색, 조사(을/를/이/가 등) 제거 등 고급 한국어 검색 기능은 이번 범위에서 제외했습니다.
- `Kavita.Common.Tests`에 `ToNormalized()` 공백 무관/NFC-NFD 동등성 단위 테스트를 추가했습니다.

## 2026-06-28: `0.9.0.12` official `0.9.0.12` nightly 포팅 릴리스

아래 변경은 공개 릴리스 태그 `0.9.0.12`에 포함했습니다.

- official Kavita `0.9.0.12` nightly를 새 base로 사용하고 기존 GDS patch set(41커밋)을 포팅했습니다.
- upstream `0.9.0.12`의 변경사항을 유지했습니다.
- `ImageService.cs`: upstream thumbnailHeight 파라미터 + GDS CreateTitleCover 공존 (6-arg 시그니처).
- GHCR `0.9.0.12`와 `latest`는 같은 multi-arch manifest digest `sha256:e6c12f19a77edb051eeb439c9655ae650133e08baf1c7df70ec4e682b298bd61`로 push했습니다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`입니다.
- Windows .NET 10 SDK 10.0.301 + node v23 + Docker Desktop buildx `gdswin`으로 빌드했습니다.
- 운영 컨테이너를 `9.0.10-3`에서 `0.9.0.12`로 교체했고 `/api/health=Ok`, Docker health `healthy`를 확인했습니다.

## 2026-06-30: `0.9.0.12-1` GDS mtime 우회 핫픽스

- rclone FUSE 마운트(`--dir-cache-time=1000h`)에서 디렉터리 mtime이 마운트 시점에 고정되어 Library Scan이 신규 파일을 감지하지 못하는 문제를 수정했습니다.
- `HasSeriesFolderNotChangedSinceLastScan`: GDS 라이브러리일 경우 mtime 체크를 건너뛰고 항상 디렉터리를 전수 열거합니다.
- 기존 Series Scan(`forceCheck=true`)과 동일한 수준의 파일 발견을 Library Scan에서도 보장합니다.
- Trade-off: GDS 라이브러리 스캔 시간이 증가합니다 (모든 등록 시리즈 폴더 전수 열거).
- GHCR `0.9.0.12-1`과 `latest`는 같은 multi-arch manifest digest `sha256:8ed8199bf1c62b54e629f86e23482d81b43fb2880320b82c9275ea5ea0d66cd8`로 push했습니다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`입니다.
- Windows Docker Desktop buildx로 3개 플랫폼 빌드 및 GHCR push를 완료했습니다.
- 운영 컨테이너를 `0.9.0.12-1`로 교체했고 `/api/health=Ok`, Docker health `healthy`를 확인했습니다.

## 2026-06-23: `9.0.10-3` sort-aware JumpBar density hotfix

아래 변경은 공개 릴리스 태그 `9.0.10-3`에 포함했습니다.

- 최근 수정순처럼 같은 날짜에 많은 시리즈가 몰린 목록에서도 우측 JumpBar가 실제 스크롤 거리 전체를 촘촘하게 대표하도록 보강했습니다.
- WebUI에 series `LastModified`, `ReleaseYear`를 전달해 현재 정렬 기준에 맞는 JumpBar label을 만들 수 있게 했습니다.
- 반복되는 긴 날짜 label은 compact marker로 접되, 각 marker는 고유한 실제 index jump target을 유지합니다.
- Docker image 조립 시 이전 WebUI lazy chunk가 남지 않도록 runtime `wwwroot`를 비운 뒤 새 production bundle을 복사합니다.
- GHCR `9.0.10-3`와 `latest`는 같은 multi-arch manifest digest `sha256:b3f9ed89796cbdfb24e0dee44bbf7a8d5a4bd72cdd3333f333110cc5836da8dd`로 push했습니다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`입니다.
- pushed GHCR image smoke에서 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 모두 `/api/health` `Ok`를 확인했습니다.
- 운영 컨테이너도 GHCR `9.0.10-3` amd64 이미지로 교체했고 `/api/health` `Ok`, Docker health `healthy`를 확인했습니다.

## 2026-06-23: `9.0.10-2` 웹소설 완결 및 JumpBar hotfix

아래 변경은 공개 릴리스 태그 `9.0.10-2`에 포함했습니다.

- 웹소설 파일명 completion marker 인식을 보강했습니다. 괄호/대괄호 marker, 분리된 marker, completion range를 처리하면서 제목 내부 문자열 오탐은 피하도록 했습니다.
- WebUI 우측 JumpBar/list 클릭이 현재 정렬 기준에서도 실제 항목으로 스크롤되도록 보정했습니다.
- 한국어 `Ended` 출판 상태 표시를 누락/실종 의미가 아닌 종료 의미로 수정했습니다.
- GHCR `9.0.10-2`와 `latest`는 같은 multi-arch manifest digest `sha256:0a88eaccb6c1ab400dbb1cefbbbff58e5cc179f260af15cdef34aa7d50750228`로 push했습니다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`입니다.
- pushed GHCR image smoke에서 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 모두 `/api/health` `Ok`를 확인했습니다. ARMv7 smoke는 CT101 qemu 10.2.1의 ARM32 translator assertion을 피해 qemu 8.1.5로 확인했습니다.
- 운영 컨테이너도 GHCR `9.0.10-2` amd64 이미지로 교체했고 `/api/health` `Ok`, Docker health `healthy`를 확인했습니다.

## 2026-06-22: `9.0.10-1` official `0.9.0.10` nightly 포팅 릴리스

아래 변경은 공개 릴리스 태그 `9.0.10-1`에 포함했습니다.

- official Kavita `0.9.0.10` nightly를 새 base로 사용하고 기존 GDS patch set을 다시 얹었습니다.
- upstream `0.9.0.10`의 대량 파일 단일 디렉터리 scan 성능 개선을 유지했습니다.
- GHCR `9.0.10-1`와 `latest`는 같은 multi-arch manifest digest `sha256:fe44c893aa1bc38942d1ab86ff028f0dff340a175cdd260090f01e41e76cf7ff`로 push했습니다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`입니다.
- source build, focused GDS service regression tests, UI production build, RID별 backend package build를 통과했습니다.
- local amd64 Docker startup smoke와 production DB clone 기반 reader/API targeted validation을 통과했습니다.
- Windows Docker Desktop/WSL에서 amd64 runtime smoke, ARM64 qemu runtime smoke, copied original-layout fixture scan을 통과했습니다.
- CT101 qemu에서 pushed GHCR unified tag의 ARMv7 runtime smoke를 통과했습니다.
- production `kavita` 컨테이너는 이 publish에서 교체하지 않았습니다. 운영 반영은 별도 rollout 절차로 진행해야 합니다.

## 2026-06-11: `9.0.7-6` metadata-filter hotfix

아래 변경은 공개 릴리스 태그 `9.0.7-6`에 포함했습니다.

- WebUI metadata filter에서 smart filter 이름 없이 정렬/필터를 저장하면 현재 route의 기본 metadata filter로 저장되도록 수정했습니다.
- smart filter 이름이 비어 있는 기본 필터 저장에서도 저장 버튼이 비활성화되지 않도록 보정했습니다.
- OPDS 호환성 실험 패치는 최종 `9.0.7-6` 배포 전에 원복했습니다. 기존 OPDS 기능은 유지하지만, 새 OPDS acquisition/progress 동작은 이번 릴리스 범위가 아닙니다.
- WebUI production build와 runtime bundle 포함 검증을 통과했습니다.
- GHCR `9.0.7-6`와 `latest`는 같은 multi-arch manifest digest `sha256:bbdfcff8d1e6b070af1cad78a82c5515ed0292e8e04cb057f839d70cde73206c`로 push했습니다.
- GHCR에서 pull한 `linux/amd64`, `linux/arm64`, `linux/arm/v7` release image가 `/api/health` 200과 Docker health `healthy`에 도달하는지 확인했습니다.
- 운영 컨테이너는 `9.0.7-6` 이미지로 교체 후 `/api/health` 200, Docker health `healthy`, restart count `0`을 확인했습니다.

## 2026-06-10: `9.0.7-5` readable book-file selection hotfix

아래 변경은 공개 릴리스 태그 `9.0.7-5`에 포함했습니다.

- 같은 chapter에 broken/empty EPUB row와 valid EPUB row가 함께 있을 때 reader/cache/TOC 경로가 readable EPUB row를 우선 선택하도록 수정했습니다.
- cache copy, cached file lookup, `book-info`, `book-page`, EPUB resource, TOC generation 경로에 같은 file-selection 정책을 적용했습니다.
- 모든 attached file row에 분석 정보가 없을 때는 기존 first-file 동작을 유지합니다.
- readable EPUB 우선순위와 cache copy 회귀 테스트를 추가했습니다.
- `CacheServiceTests` focused regression suite 24개를 통과했습니다.
- production DB clone + read-only GDS mount에서 affected regression sample의 cold-cache `book-info`, `chapters`, `book-page`, EPUB resource API 200을 확인했습니다.
- GHCR `9.0.7-5`와 `latest`를 같은 multi-arch manifest로 push했습니다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` pushed GHCR 이미지가 `/api/health` 200에 도달하는 것을 확인했습니다.
- 운영 컨테이너도 GHCR `9.0.7-5` amd64 이미지로 맞췄고, rollout 후 같은 reader API targeted validation을 통과했습니다.

## 2026-06-10: `9.0.7-4` GDS targeted scan 후속 작업 hotfix

아래 변경은 공개 릴리스 태그 `9.0.7-4`에 포함했습니다.

- GDS 시리즈 단위 스캔 후 word-count 분석을 건너뛰도록 했습니다.
- GDS 시리즈 단위 스캔 후 전역 metadata cleanup과 전체 cache cleanup을 건너뛰도록 했습니다.
- 시리즈 로컬 chapter cache cleanup은 유지했습니다.
- GDS targeted scan 완료 뒤 불필요한 후속 작업으로 CPU가 오래 남는 상황을 막기 위한 회귀 테스트를 추가했습니다.
- production DB clone과 원본 GDS 상대 폴더 구조 read-only mount에서 targeted scan 후 CPU/health를 확인했습니다.
- GHCR `9.0.7-4`와 `latest`를 같은 multi-arch manifest로 push했습니다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` pushed GHCR 이미지가 `/api/health` 200에 도달하는 것을 확인했습니다.
- 운영 컨테이너도 GHCR `9.0.7-4` amd64 이미지로 맞췄습니다.

## 2026-06-10: `9.0.7-3` GDS cover scan hardening 및 WebUI cover cache fix

아래 변경은 공개 릴리스 태그 `9.0.7-3`에 포함했습니다.

- GDS targeted series scan이 저장된 broad category/library root를 재귀 스캔하지 않고 기존 파일의 실제 parent directory만 스캔하도록 했습니다.
- GDS mixed-format series에서 TXT/EPUB/PDF/archive 파일이 format 차이 때문에 누락되지 않도록 batch 병합을 보강했습니다.
- mixed-root series에서 concrete `LowestFolderPath`가 broad root로 되돌아가지 않도록 보존했습니다.
- scan work completion과 post-scan cleanup enqueue 로그를 분리해 targeted scan 완료 지점을 확인하기 쉽게 했습니다.
- cover refresh 또는 series scan 후 WebUI가 stale browser cache의 이전 cover bytes로 되돌아가지 않도록 cover URL cache-buster와 no-cache header를 추가했습니다.
- 대표 cover 정규화는 운영에서 작은 batch와 health latency gate를 걸어 실행해야 하는 절차로 문서화했습니다.

## 2026-06-09: `9.0.7-2` GDS cover refactor 및 TXT YAML cover precedence fix

아래 변경은 공개 릴리스 태그 `9.0.7-2`에 포함했습니다.

- GDS 전용 cover 생성 경로를 `MetadataService`에서 별도 서비스로 분리했습니다.
- upstream 재포팅 시 예상 충돌 지점을 `MetadataService` DI와 GDS hook 호출부로 축소했습니다.
- GDS cover 우선순위를 한 곳에 고정했습니다: folder cover는 series cover로 보존, file-level YAML base64 cover는 exact chapter file 기준 우선, media internal cover fallback, TXT title fallback 순서입니다.
- TXT-only GDS import/refresh에서 `kavita.yaml` file-level base64 cover가 TXT title fallback cover보다 먼저 적용되도록 수정했습니다.
- `cover: TEXT`, URL, invalid base64, empty YAML, NUL-filled YAML은 이미지 cover가 아닌 hint로 처리하고 media import는 계속 진행되도록 했습니다.
- `Kavita.Services.Tests` 전체를 GDS TXT 지원 기준으로 보정하고 통과했습니다: 2246 passed, 0 failed, 6 skipped.
- local cover regression fixture를 2회 반복 통과했고 SQLite `quick_check=ok`를 확인했습니다.
- GHCR `9.0.7-2`와 `latest`를 같은 multi-arch manifest로 push했습니다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` pushed GHCR 이미지가 `/api/health` 200에 도달하는 것을 확인했습니다.
- 원본 EPUB이 0바이트 또는 ZIP/EPUB이 아닌 샘플은 code fix 대상이 아니라 source-data repair 대상으로 분류해 회귀 매트릭스에 남겼습니다.

## 2026-06-09: `9.0.7-1` GDS cover/SQLite hotfix

아래 변경은 공개 릴리스 태그 `9.0.7-1`에 포함했습니다.

- GDS cover metadata가 비었거나 잘못된 경우에도 cover 생성 흐름이 중단되지 않도록 보강했습니다.
- 생성된 GDS chapter cover가 volume/series cover 참조까지 저장되도록 보정했습니다.
- 특정 운영 환경에서 WebUI 초기 접근 중 SQLite disk I/O 오류로 보일 수 있던 upstream write-path 회귀를 되돌렸습니다.

## 2026-06-06: `9.0.7` official `0.9.0.7` nightly 포팅 릴리스

아래 변경은 공개 릴리스 태그 `9.0.7`에 포함했습니다.

- official Kavita `0.9.0.7` nightly 변경을 GDS 포팅 브랜치에 병합했습니다.
- GDS reader metadata refresh 안정화 패치를 유지했습니다.
- upstream `BookController`의 chapter access 보호 변경과 GDS no-store cache policy가 함께 유지되는지 확인했습니다.
- Kavita+를 쓰지 않는 운영 가정에서도 일반 Book 라이브러리 eligibility가 깨지지 않도록 GDS만 Kavita+ metadata 대상에서 제외되는지 확인했습니다.
- 테스트 컨테이너에서 health, version API, DB integrity, GDS read-only mount를 확인했습니다.
- TXT, ZIP/CBZ archive, EPUB, PDF reader/API 경로를 확장 검증했고 새 MediaError, 404/500/Fatal/SQLite/database-lock/disk I/O error 로그가 없음을 확인했습니다.
- synthetic single-spine EPUB fixture는 cover 검증 대상이 아니라 TOC page mapping 회귀 검증 대상으로만 분리했습니다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` 모두 같은 source patch set에서 RID별 publish output으로 빌드했습니다.
- GHCR `9.0.7`와 `latest`를 같은 multi-arch manifest로 push했습니다.
- `linux/amd64`는 pushed GHCR image로 `kavita-test` extended validation을 통과했습니다.
- `linux/arm64`와 `linux/arm/v7`는 qemu smoke test에서 `/api/health` 200 및 Docker health `healthy`를 확인했습니다.
- 상세 검증 기록은 `docs/GDS_0.9.0.7_VALIDATION.md`에 남겼습니다.

## 2026-06-02: `9.0.6-2` 스캔/page-count 안정화

아래 변경은 `9.0.6-2` 배포 후보에 포함했습니다.

- GDS EPUB/PDF/TXT 신규 또는 재빌드 파일이 scanner shortcut 때문에 `Pages=1`로 저장되는 문제를 수정했습니다.
- 잘못된 `kavita.yaml`이 있더라도 미디어 파일 전체를 scan에서 제외하지 않고 파일명 기반 fallback metadata로 계속 import되도록 했습니다.
- `Finished library scan` 이후 post-scan cleanup이 남아 있어 수동 스캔이 지연되어 보이는 혼선을 줄이기 위해 최종 scan-job completion log를 추가했습니다.
- GDS folder cover가 적용된 직후 volume/chapter cover 생성 흐름에서 series cover가 다시 덮어써지는 문제를 보정했습니다.
- 하나의 XHTML spine 안에 여러 TOC anchor가 있는 EPUB은 backend 가상 페이지로 나눠 `book-info`, TOC, `book-page`가 여러 페이지를 반환하도록 했습니다.
- `kavita-test`에서 LOCAL-FIXTURES 155개 항목을 3회 반복 검증했고, reader info/nav/page/cover 실패가 0건임을 확인했습니다.
- 합성 single-spine EPUB fixture에서 DB pages `3/3`, `book-info=3`, TOC `3`, `book-page` 0/1/2 distinct content를 확인했습니다.
- 운영 redacted duplicate-manifest EPUB sample 03-06권 duplicate manifest EPUB은 `book-info` 호출 후 `12/12`, `12/12`, `12/12`, `13/13` 페이지로 DB가 보정되고 마지막 page API가 200을 반환했습니다.
- redacted cover-only EPUB sample fixture는 EPUB ZIP 내부에 `cover.xhtml`, `cover.jpg`, `toc.ncx`만 있고 본문 XHTML이 없어, 해당 파일의 `1/1`은 Kavita page-count 복구 대상이 아니라 원본 EPUB 구조 문제로 기록했습니다.
- GHCR `9.0.6-2` multi-arch manifest를 push했습니다. `linux/amd64`는 운영 반영 검증, `linux/arm64`는 qemu smoke test에서 `/api/health` 200, `linux/arm/v7`는 qemu smoke test에서 `/api/health` 200 및 Docker health `healthy`를 확인했습니다.

## 2026-06-01: `9.0.6-1` official `0.9.0.6` 포팅

아래 변경은 `9.0.6-1` 배포 후보에 포함했습니다.

- official Kavita `0.9.0.6` 코드베이스에 `0.9.0.2-8`까지의 GDS/rclone 수정사항을 포팅했습니다.
- GDS EPUB이 scanner shortcut 때문에 `1/1`로 남는 문제를 reader `book-info` 진입 시 실제 reading order count로 보정하도록 수정했습니다.
- EPUB manifest의 duplicate item/id/href를 임시 copy에서 제거하고 spine 참조를 유지되는 item id로 rewrite하도록 보강했습니다.
- EPUB repair 경로를 `book-info`, `book-page`, TOC, resource, metadata, word-count 경로에 적용했습니다.
- EPUB 내부 resource 상대경로를 정규화해 `../Images/...` 같은 링크를 더 안정적으로 처리합니다.
- GDS scanner는 remote-backed EPUB 전체 읽기를 하지 않도록 유지해 Web UI blocking을 피했습니다.
- GDS archive 커버 재생성 시 2권 이후 chapter/volume cover가 1권 cover로 고정되는 문제를 수정했습니다.
- TXT fallback cover 한글 글꼴 깨짐을 막기 위해 runtime image에 Nanum Gothic Regular/Bold/ExtraBold를 포함했습니다.
- 대형 GDS 강제 스캔에서 DB 갱신, 커버 생성, word-count 분석이 동시에 많이 쌓여 OOM으로 이어질 수 있어, GDS 라이브러리만 시리즈 단위 저메모리 직렬 처리 경로를 사용하도록 보강했습니다.
- 운영 DB/API 확인을 컨테이너 안에서 바로 수행할 수 있도록 runtime image에 `sqlite3`를 포함했습니다.
- cache cleanup과 reader/cache 작업 경합에서 이미 삭제된 directory를 조용히 무시하도록 보강했습니다.
- `kavita-test` fixture를 CBZ/ZIP/EPUB/TXT 각 10 series와 사용자 지정 EPUB 문제 샘플로 확장하고, 155개 media 항목에 대해 reader/API 3회 반복 검증을 통과했습니다.
- 운영 `kavita`에 적용 후 redacted page-count and duplicate-manifest EPUB samples EPUB page count, page render, TOC API, NPM 접근, rclone read-only 상태를 확인했습니다.

## 2026-05-31: `0.9.0.2-8` 기본 시리즈 정렬 hotfix

아래 변경은 `0.9.0.2-8` 배포 후보에 포함했습니다.

- 새 필터/정렬 조건 없음 상태에서 시리즈 기본 정렬이 제목 오름차순으로 돌아가던 문제를 수정했습니다.
- Web UI의 기본 시리즈 정렬을 `최근 수정`으로 바꾸고, 기본 방향을 내림차순으로 지정했습니다.
- 명시적인 내림차순 값(`false`)이 `|| true` 처리로 다시 오름차순이 되던 필터 상태 복원 버그를 수정했습니다.
- 백엔드의 정렬 옵션 null fallback도 `LastModifiedDate desc`로 맞춰 API 호출자가 정렬 옵션을 보내지 않아도 같은 기준을 사용합니다.
- 운영 API에서 정렬 옵션 없이 조회했을 때 DB의 `Series.LastModified desc` 순서와 일치하는 것을 확인했습니다.
- `linux/amd64`, `linux/arm64` self-contained publish, multi-arch OCI build, `linux/amd64` startup smoke를 통과했습니다.

## 2026-05-31: `0.9.0.2-7` GDS archive 커버 fallback hotfix

아래 변경은 `0.9.0.2-7` 배포 후보에 포함했습니다.

- GDS 커버 생성에서 YAML/base64 커버나 TXT 제목 커버가 없는 archive 기반 시리즈가 일반 ZIP/CBZ 첫 페이지 커버 추출 경로로 내려가지 못하던 문제를 수정했습니다.
- 이 문제는 신규 GDS archive 시리즈가 파일과 페이지 수는 정상 등록되지만 `Series`, `Volume`, `Chapter` 커버 참조가 비어 있는 형태로 나타날 수 있었습니다.
- 기존 GDS TXT 제목 기반 커버 동작은 유지했습니다.
- focused regression test, `linux/amd64`/`linux/arm64` self-contained publish, multi-arch OCI build, `linux/amd64` startup smoke를 통과했습니다.

## 2026-05-31: `0.9.0.2-6` 혼합 포맷 단어 수 분석 hotfix

아래 변경은 `0.9.0.2-6` 배포 후보에 포함했습니다.

- 대표 포맷이 EPUB인 시리즈 안에 PDF/TXT 같은 비 EPUB 파일이 섞여 있을 때, 단어 수 분석기가 해당 파일을 EPUB 리더로 열어 오류를 내던 문제를 수정했습니다.
- 비 EPUB 파일은 EPUB word count 대상에서 제외하고, 분석 시각만 갱신해 같은 오류가 반복되지 않도록 했습니다.
- 비 EPUB 파일이 섞인 EPUB-format 시리즈 회귀 테스트를 추가했습니다.
- `linux/amd64`, `linux/arm64` self-contained publish를 통과했습니다.
- `linux/amd64`, `linux/arm64` multi-arch OCI archive를 새로 만들고 manifest를 확인했습니다.
- `linux/amd64` 빈 config startup smoke와 Web UI bundle 문자열 검사를 통과했습니다.

## 2026-05-31: `0.9.0.2-5` 이후 main 브랜치 진단 도구 보강

아래 변경은 `0.9.0.2-6` 배포 전 `main` 브랜치에 먼저 들어간 운영 검증 도구와 문서 보강입니다.

- live DB snapshot preflight가 같은 label을 재사용해도 SQLite sidecar를 정리하고 임시 파일 성공 후 교체하도록 보강했습니다.
- MediaError postflight gate가 상위 40개 출력이 아니라 전체 MediaError count를 기준으로 판정하도록 수정했습니다.
- scan log summary에 before/after 비교와 non-forced scan churn gate를 추가했습니다.
- `collect_gds_preflight.sh`에서 DB gate와 scan churn gate를 한 번에 실행할 수 있도록 `--compare-scan-json`을 추가했습니다.
- `--check-covers`를 DB/config cover reference 중심의 빠른 검사로 바꾸고, rclone 원본 `cover.*`와 `kavita.yaml` cover hint 탐색은 새 `--check-cover-source-files` 옵션으로 분리했습니다.
- cover postflight gate를 config cover 감소 여부와 원본 missing-cover debt 판정으로 나눠, 일반 postflight가 rclone source probe 때문에 멈추지 않도록 했습니다.
- 로그인 화면에서 `localhost:5000/api`로 요청하는 `0.9.0.2-4` 증상 설명을 사용 설명서에 추가했습니다.

## 2026-05-31: `0.9.0.2-5` Web UI production hotfix

아래 변경은 `0.9.0.2-5` 배포 후보에 포함했습니다.

- `0.9.0.2-4` Docker image의 Web UI가 production 번들이 아니라 개발 번들로 포함되어, 외부 브라우저에서 `localhost:5000/api`를 호출하던 문제를 수정했습니다.
- Angular `dist`를 삭제한 뒤 production UI를 다시 빌드했습니다.
- Docker image 빌드 시 기존 `/kavita/wwwroot`를 삭제하고 새 production UI만 복사하도록 했습니다.
- 검증 컨테이너에서 `/kavita/wwwroot` 전체에 `localhost:5000`, `:5000/api`, Angular 개발모드 문자열이 남아 있지 않음을 확인했습니다.
- production 환경 chunk가 document base URL 기반의 same-origin `/api/`, `/hubs/`를 사용함을 확인했습니다.
- `linux/amd64`, `linux/arm64` OCI manifest를 새로 생성했습니다.
- preflight collector에 `--snapshot-db` 옵션을 추가해 live SQLite DB를 직접 오래 열지 않고 backup copy로 진단할 수 있게 했습니다.
- 공개 GHCR image `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`를 운영 DB 사본으로 기동 검증했고, health/API/UI bundle/DB FK 검증을 통과했습니다.

## 2026-05-31: `0.9.0.2-4` source/release 정렬

아래 변경은 `0.9.0.2-4` 배포 후보에 포함했습니다.

- GDS 이어보기/볼륨 화면의 chapter title 처리에서 `LibraryType.GDS`를 chapter 계열 라이브러리로 처리하도록 보강했습니다.
- 오래된 DB가 file type migration을 다시 탈 때 GDS 라이브러리에 `Archive`, `EPub`, `Pdf`, `Images`, `Text` 파일 그룹이 모두 포함되도록 보강했습니다.
- `linux/amd64` 컨테이너 startup smoke test를 통과했습니다.
- `linux/amd64`, `linux/arm64`를 포함한 OCI manifest를 생성하고 내부 platform 항목을 확인했습니다.
- Oracle A1 startup FK 제보는 x86/NAS 공통 재현 문제가 아니라 arm64 서버의 기존 DB/migration/volume 상태를 비교해야 하는 환경별 사례로 분리했습니다.
- 운영 컨테이너는 별도 승인 전까지 기존 태그를 유지하며, 운영 DB postflight는 아직 완료 판정에 포함하지 않았습니다.

## 2026-05-31: startup FK 진단 및 duplicate cleanup

아래 변경은 `0.9.0.2-3` 배포 후보에 포함했습니다.

- 일부 기존 DB에서 startup migration 실패 뒤 BaseUrl 저장 단계가 `SQLite Error 19: FOREIGN KEY constraint failed`로 보이는 문제를 분석했습니다.
- BaseUrl 저장은 별도 EF scope에서 수행하도록 분리해, migration 단계의 실패한 tracked change가 startup 후속 저장에 섞이지 않도록 했습니다.
- startup migration에서 예외가 발생하면 더 이상 삼키고 계속 진행하지 않고, 원래 migration 예외를 그대로 드러내도록 했습니다.
- BaseUrl 저장에서 `DbUpdateException`이 발생하면 `PRAGMA foreign_key_check` 결과 일부를 로그에 남기도록 했습니다.
- 같은 volume 안에서 같은 파일 경로가 여러 chapter에 남은 경우, 이번 스캔에서 선택된 chapter만 보존하도록 cleanup을 보강했습니다.
- 읽기 전용 진단 스크립트가 `PRAGMA foreign_key_check`와 duplicate file path cleanup 후보 분류를 출력하도록 확장했습니다.
- 읽기 전용 진단 스크립트가 EF migration history, manual migration history, 핵심 server setting, 주요 테이블 row count를 출력하도록 확장했습니다. x86/NAS 정상 사례와 Oracle A1 startup FK 사례를 비교할 때 DB/migration 상태 차이를 바로 볼 수 있습니다.
- 읽기 전용 진단 스크립트가 MediaError를 EPUB 구조 문제, PDF metadata/encryption 문제, archive 지원 문제, scanner 미인식 항목으로 분류하도록 확장했습니다.
- preflight 수집 스크립트가 host architecture와 Docker engine 정보를 manifest에 기록해 Oracle A1 같은 환경별 startup 제보를 비교하기 쉽게 했습니다.
- postflight 비교에 `--postflight-gates` 옵션을 추가해 integrity/FK/`Pages=0`/duplicate/MediaError/cover cache/TXT missing-cover 상태를 `PASS`, `WARN`, `FAIL`로 판정할 수 있게 했습니다.
- `--check-archives` 결과를 JSON에도 기록해 직접 이미지가 있는 복구 가능 `Pages=0` archive와 nested archive를 postflight gate에서 분리할 수 있게 했습니다.
- scan log timing 요약 도구를 추가해 library scan 시간, file discovery 시간, series update 시간, 느린 reader HTTP 요청을 기본적으로 library/series 이름 노출 없이 분석할 수 있게 했습니다.
- reader latency 상관분석 도구를 추가해 느린 reader 요청이 DB 파일 크기, format, page 수, cache folder 상태와 어떻게 연결되는지 경로/제목 노출 없이 확인할 수 있게 했습니다.
- C# backend build, UI production build, multi-arch OCI build, `linux/amd64` startup smoke test를 통과했습니다.
- `linux/arm64` 이미지는 build/manifest 경로를 검증했습니다. x86/NAS에서 정상인데 Oracle A1에서만 startup FK 오류가 나면 이미지 아키텍처보다 기존 DB, 컨테이너 전환 상태, compose volume 연결을 먼저 확인하는 쪽으로 정리했습니다.

## 2026-05-31: GDS TXT fallback cover 및 scan debt 회복

아래 변경은 source branch와 `0.9.0.2-2` 배포 후보에 포함했습니다.

- GDS 라이브러리 타입이 UI entity title 계산에서 빠져 일부 화면의 볼륨/회차명이 빈 문자열로 표시될 수 있던 문제를 보정했습니다.
- GDS 원본 `cover.*`가 없을 때 기존 Kavita config cover cache 파일을 삭제하지 않도록 보정했습니다.
- GDS TXT에서 `cover: TEXT`를 이미지 base64로 오인하지 않도록 보정했습니다.
- 원본 커버와 YAML 이미지가 모두 없는 GDS TXT 시리즈는 제목 기반 cover를 Kavita config `covers` 디렉터리에 자동 생성하도록 했습니다.
- 제목 기반 cover는 외부 API나 외부 이미지 다운로드를 사용하지 않습니다.
- 제목 기반 cover의 한글 렌더링을 위해 Docker image에 Nanum Gothic 폰트를 포함했습니다.
- GDS 시리즈에 `Pages=0` 파일이 남아 있으면 폴더 변경 없음 최적화를 건너뛰고 실제 파일 목록을 다시 파싱하도록 했습니다.
- C# backend build, UI production build, `linux/amd64` runtime smoke test, `linux/amd64`/`linux/arm64` OCI manifest 검증을 완료했습니다.

## 2026-05-31: GDS 증분 스캔 안정화 추가

- GDS 라이브러리에서 포맷 하위 폴더가 실제 시리즈 폴더 바로 아래에 있을 때, DB 경로맵에 현재 폴더가 없더라도 부모 시리즈의 변경 상태를 안전하게 재사용하도록 했습니다.
- 변경 없음으로 판단된 폴더를 파싱할 때 현재 폴더 키만 직접 조회하지 않고, 기존 시리즈 경로 또는 GDS 폴더명 fallback으로 안전하게 매칭합니다.
- 같은 시리즈가 정규화명은 같지만 물리 폴더명이 조금 다른 형제 폴더로 나뉜 경우, 폴더명 정규화값을 기존 시리즈명과 비교해 반복 재처리를 줄였습니다.
- 테스트 컨테이너 검증 기준, 문제 라이브러리의 반복 일반 재스캔이 `5 Series / 108 files / 약 7-10초`에서 `0 Series / 0 files / 약 0.8초`로 안정화됐습니다.
- EPUB 단어 수 계산 단계에서 손상되었거나 EPUB 구조가 아닌 파일은 기존처럼 오류로 기록되지만, 스캔 자체는 정상 완료됩니다.

## 2026-05-31: 혼합 폴더/읽기 불가 보정

- GDS 라이브러리의 `chapter-info` 처리에서 `LibraryType.GDS`가 누락되어 일부 PDF/EPUB 라우팅이 예외로 이어질 수 있던 문제를 보정했습니다.
- GDS 빠른 스캔에서 EPUB/PDF/TXT의 페이지 수 계산을 생략하더라도 최소 `Pages=1`을 유지해 “읽을 수 없음”처럼 보이지 않도록 했습니다.
- 같은 작품이 `작품명/`과 `작품명 -/`처럼 두 폴더로 나뉜 경우, 증분 스캔 입력에 한쪽 폴더만 들어와도 실제 파일이 존재하는 기존 GDS 볼륨은 제거하지 않도록 했습니다.
- `force=true` GDS 스캔은 누락 파일 복구를 위해 실제 파일시스템을 다시 읽도록 했습니다. 이 모드는 느리지만, 증분 스캔에서 누락된 EPUB/PDF/TXT 복구에 필요합니다.
- 운영 검증 기준 분리 폴더 샘플은 ZIP 3개와 EPUB 5개, 총 8개 파일이 유지되고 EPUB 1권이 정상 열리는 것을 확인했습니다.
- 이후 일반 재스캔은 `171 files / 297 series`를 약 12초에 완료했고, EPUB 5개가 다시 제거되지 않는 것을 확인했습니다.

## 2026-05-31: GDS 재스캔 속도 개선

- GDS 강제 스캔에서 변경 없는 파일의 page count와 KOReader hash를 다시 계산하지 않도록 조정했습니다.
- 일반 GDS/rclone 재스캔에서 변경 없는 파일의 불필요한 재계산을 줄였습니다.
- `[Cover].jpg`처럼 대괄호가 붙은 커버 파일이 GDS 이미지 미디어로 오인식되어 스캔 오류와 지연을 만드는 문제를 막았습니다.
- 폴더 커버가 이미 Kavita config cover 디렉터리에 있고 색상 정보도 있는 경우, 반복 스캔에서 커버 복사/색상 분석을 건너뜁니다.
- 실제 운영 검증 기준 한 GDS 라이브러리의 강제 스캔은 3분 이상 진행되던 상태에서 `11 files / 187 series`를 약 2.8초에 완료했습니다.
- 다른 대형 GDS 라이브러리의 강제 스캔도 `2 files / 2061 series`를 약 4.5초에 완료했습니다.
- loose image 폴더를 쓰지 않는 기존 GDS 라이브러리는 `Images` 파일 그룹을 꺼서 불필요한 커버 이미지 열거를 줄였습니다. 실제 이미지 파일이 등록된 라이브러리는 유지했습니다.

## 2026-05-31: 운영 검증 및 YAML metadata fix

- 운영 Kavita config를 일반 경로(`/your/kavita/config`)로 정리하고 compose mount를 확인했습니다.
- 남아 있던 config/test config의 cover 파일을 운영 config로 회수하고, 스캔을 통해 cover cache가 다시 생성되는 것을 확인했습니다.
- GDS 라이브러리에서 `kavita.yaml`/`kavita.yml` sidecar metadata를 읽도록 보강했습니다.
- `Summary`, 장르, 태그, 언어, 웹 링크, 작가/번역자/출판사/작화가, 발매일, 연령등급 등 안전한 YAML 필드를 반영합니다.
- YAML `meta.Name`이 시리즈명 또는 회차 제목을 덮어써 회차 정보가 사라지는 문제를 막았습니다.
- GDS 회차 제목은 파일명에서 만들고 `#138`, `[1440px]`, `[직스샷]`, trailing `(리디)` 같은 배포/품질 태그를 제거합니다.
- 출판사/분류 접두가 붙은 폴더에서도 중복 시리즈가 새로 생기지 않는 것을 확인했습니다.
- 상세 운영 기록은 `docs/OPERATIONS_20260531_KO.md`에 남겼습니다.

## 2026-05-30: universal packaging

- `linux/amd64`, `linux/arm64`를 하나의 OCI archive로 패키징했습니다.
- x86 서버와 Oracle Cloud A1 같은 arm64 서버에서 같은 release asset을 사용할 수 있습니다.
- 중간 테스트 이미지와 webtoon patch tree는 제외하고 scanfix 기준으로만 배포했습니다.
- GitHub Release asset을 GHCR 이미지로 publish하는 workflow를 추가해 `docker pull` 기반 배포가 가능하도록 했습니다.

## 2026-05-30: GDS scanfix

- `LibraryType.GDS` reader/runtime 오류를 수정했습니다.
- GDS 스캔 시 같은 작품 폴더 안의 서로 다른 포맷이 별도 시리즈로 갈라지는 문제를 줄였습니다.
- `kavita.yaml`, `kavita.yml`, `cover.*` 같은 메타데이터 파일이 미디어 파일로 등록되지 않도록 했습니다.
- `웹소설` 경로의 loose `.jpg` 이미지가 권/시리즈로 잘못 등록되는 문제를 방지했습니다.
- GDS 스캔 중 누락 파일 정리 로직이 원본 파일 삭제/정리로 이어지지 않도록 DB 보존 방어를 추가했습니다.
- GDS 폴더/sidecar 커버는 Kavita config cover 디렉터리로만 복사하고 원본 media 경로에는 쓰지 않도록 했습니다.
- GDS 시리즈 `FolderPath`가 가능한 경우 실제 작품 폴더를 가리키도록 조정했습니다.
- GDS 변경 감지가 대표 `FolderPath` 하나에만 의존하지 않고 실제 DB 파일 parent directory도 보도록 했습니다.
- 반복 스캔 시 불필요한 신규/삭제 변화가 줄도록 안정화했습니다.
- 이미지 빌드 시 기존 `/kavita/wwwroot`를 제거한 뒤 새 UI를 복사해 stale Angular chunk 문제를 방지했습니다.
- 정적 파일 캐시 정책을 `no-cache/no-store`로 바꿔 UI 갱신 후 오래된 chunk 참조를 줄였습니다.
- 기본 시리즈 정렬을 마지막 수정 내림차순으로 복구했습니다.

## 2026-05-29: fix build

- EPUB OPF manifest에 `Section0001.xhtml` 같은 중복 ID가 있을 때 자동 복구 후 다시 열도록 했습니다.
- TXT 변환 도구로 생성된 일부 EPUB에서 발생하던 파싱 오류를 완화했습니다.
- 손상된 PDF의 `/Prev` 순환 참조로 인한 XRef 무한 재귀를 막기 위해 최대 깊이 제한을 추가했습니다.
- rclone FUSE 대형 라이브러리에서 디렉터리 재귀 열거가 hang 되는 문제를 줄이기 위해 stack 기반 반복 열거로 바꿨습니다.

## 2026-05-28: scan build 기준 기능

이 버전은 기존 배포 기준입니다. 주요 기능은 다음과 같습니다.

- `LibraryType.GDS = 6`
- `MangaFormat.Text = 5`
- `FileTypeGroup.Text = 5`
- TXT 확장자/parser/reader/controller 지원
- GDS scanner의 folder-based TXT series 지원
- `cover.*`를 series, volume, chapter에 반영
- TXT/ZIP 혼합 GDS 시리즈가 같은 정규화 제목이면 갈라지지 않도록 그룹핑
- mixed GDS series에서 `chapter-info`가 실제 chapter format을 반환
