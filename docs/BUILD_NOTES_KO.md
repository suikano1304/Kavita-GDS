# 빌드 노트

## 목적

Kavita official `0.9.0.12` nightly 기반 GDS 빌드 `0.9.0.12-6`를 Docker/GHCR 배포용으로 패키징했습니다.

이번 배포는 GDS scan 처리 queue/index 경량화, scan 종료 sidecar cache 해제, scan 중 대표 cover 경량 생성, metadata refresh 경로의 volume/chapter cover 보강을 포함합니다. 이전 `0.9.0.12-5` scan phase streaming 메모리 보강, 대형 sidecar YAML streaming parser, 반복 재처리 방지, mixed-format fingerprint 안정화, post-scan CPU 작업 축소도 그대로 포함합니다. 상세 변경 내역은 [CHANGELOG_KO.md](CHANGELOG_KO.md)를 참고하세요.

## 포함 플랫폼

- `linux/amd64`
- `linux/arm64` (GHCR multi-arch manifest)
- `linux/arm/v7` (GHCR multi-arch manifest)

## 산출물

Primary release image:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-6
ghcr.io/suikano1304/kavita-gds:latest
```

RID package outputs used by Docker buildx:

```text
kavita-linux-x64.tar.gz
kavita-linux-arm64.tar.gz
kavita-linux-arm.tar.gz
```

권장 이미지 태그:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-6
```

현재 GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-6
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:37393d6f42e9f09a6a5b2f0f49e07f92a706aec86dd0a87b2f09d8c1007091cf
linux/amd64=sha256:067c2671b6c210986855befc8753e6e58ea9a0c1ec67da2a3e33de62a26f86a1
linux/arm64=sha256:4669931ff342652fac8e32ada2d29e235a2e301acd4f84b7e38effedc75d351f
linux/arm/v7=sha256:7faf8457b1feb9a52a75bbb1913f3c954bd4c08fa350cbf335ffbafd24a9dd03
```

GHCR는 Docker buildx `--push`로 직접 publish했습니다.

이전 `0.9.0.12-2` GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-2
multiarch digest=sha256:3ea108084fa6b71a7939d742aa55ff3c9a16cfe608261b90f75e520045b2c3d0
```

이전 `0.9.0.12-1` GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-1
multiarch digest=sha256:8ed8199bf1c62b54e629f86e23482d81b43fb2880320b82c9275ea5ea0d66cd8
```

## 검증 내용

- **`0.9.0.12-5` GDS scan phase memory 검증**: Windows PC에서 Release build와 focused GDS service tests를 통과했습니다. `kavita-test` fixture에서 변경 없음 재스캔은 처리 대상 0건, archive 1개 추가 후 재스캔은 처리 대상 1건, 이후 재스캔은 다시 0건으로 돌아감을 확인했습니다. 테스트 컨테이너 메모리는 scan 후 낮은 수준으로 유지되었습니다.
- **`0.9.0.12-6` GDS scan/cover memory 검증**: Windows PC에서 `linux/amd64`, `linux/arm64`, `linux/arm/v7` RID 패키지를 재생성하고 GHCR multi-arch image를 push했습니다. `tkavita` fixture에서 변경 없음 재스캔 처리 대상 0건, archive 추가 후 처리 대상 1건, sidecar 변경 fingerprint mismatch, 다권 합성 53개 chapter/volume cover 누락 0건, 이후 재스캔 처리 대상 0건을 확인했습니다. pushed GHCR image는 amd64/arm64/armv7 모두 `/api/health=Ok`를 반환했습니다.
- **대형 GDS 운영 스캔 검증**: 운영 대형 GDS 라이브러리에서 broad scan이 `4668` series를 parse하고 처리 대상 `0`건으로 종료했습니다. 완료 시간은 약 `148`초였고, 완료 후 container memory는 약 `350 MiB`, `/api/health=Ok`였습니다.
- **대형 sidecar YAML 검증**: GDS sidecar parser가 필요한 `meta` scalar와 file page hint만 streaming으로 읽도록 변경되어, 큰 `kavita.yaml`에서도 full YAML deserialize와 cover payload retention을 피합니다.
- **반복 재처리 방지 검증**: sidecar metadata가 의도적으로 비어 있거나 일부만 있는 series를 매 scan마다 metadata backfill 대상으로 다시 잡지 않도록 했고, mixed-format GDS series fingerprint lookup을 normalized series identity 기준으로 안정화했습니다.
- **`0.9.0.12-4` GDS scan memory/latest-sort 검증**: Windows PC에서 `dotnet build Kavita.sln -c Release -maxcpucount:1 /p:UseSharedCompilation=false`와 WebUI `npx ng build --configuration production`를 통과했습니다. GDS 처리 경로는 series key만 보관하고 skip/처리 직후 parser metadata 참조를 비웁니다. 또한 scan 시작, 25개 처리마다, 완료 시점에 강제 compacting GC 후 managed/working set/private memory checkpoint를 로그로 남겨 실제 회수 여부를 확인할 수 있게 했습니다. "마지막 수정" 정렬 기준은 파일 timestamp와 새 Chapter/MangaFile DB 생성 시각을 함께 반영합니다.
- **`0.9.0.12-3` GDS scan/content-date 검증**: Windows Docker fixture에서 첫 스캔은 처리 대상 1건, 동일 fixture 재스캔은 처리 대상 0건으로 감소함을 확인했습니다. `kavita.yaml`만 변경하면 재처리 대상이 되지만 `ContentLastModified`는 유지되고, 신규 archive 파일 추가 시 `ContentLastModified`가 새 파일 timestamp로 상승함을 확인했습니다. `tkavita` fixture에서도 `/api/health=Ok`, Docker health `healthy`, `contentLastModified` API 응답, fingerprint skip 로그를 확인했습니다.
- **`0.9.0.12-2` 한국어 검색 정규화 개선**: `ToNormalized()`에 유니코드 NFC 정규화를 추가해 분해형(NFD) 한글 입력도 완성형(NFC) DB 데이터와 매칭되도록 했습니다. `Chapter.NormalizedTitleName`, `Library.NormalizedName` 필드를 추가해 챕터/라이브러리도 Series와 동일하게 띄어쓰기 무관 검색이 가능해졌습니다. `AppUserCollection`/`ReadingList`/`Library` 검색의 정규화 필드 누락·불일치 버그 3건을 수정했습니다. 신규 `ManualMigrateKoreanSearchNormalizationBackfill` 마이그레이션이 서버 시작 시 1회 자동으로 기존 레코드의 정규화 필드를 재계산합니다. `Kavita.Common.Tests`에 `ToNormalized()` 단위 테스트를 추가했습니다.
- Docker buildx로 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 이미지를 GHCR에 push했습니다.
- 로컬 tarball 패키징 시 실행 파일 rename(`Kavita.Server`→`Kavita`)과 `config/appsettings-init.json` 포함을 공식 `build.sh` 규칙과 동일하게 맞췄습니다.
- push한 이미지를 검증할 때는 로컬 stale 태그를 피하기 위해 `docker rmi <tag> -f` 후 명시적으로 재pull하여 digest 일치를 확인했습니다.
- pushed GHCR unified tag smoke에서 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 모두 Windows Docker Desktop/qemu 또는 운영 deployment로 `/api/health` `Ok`를 확인했습니다.
- `docker buildx imagetools inspect`로 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 3개 플랫폼 매니페스트가 모두 존재함을 확인했습니다.
- 프로덕션 `kavita`를 공식 `0.9.0.12-5` GHCR multi-arch 태그로 교체했고 `/api/health=Ok`, Docker health `healthy`, restart count `0`, 대형 GDS no-change scan 안정성을 확인했습니다.
- official `0.9.0.12` nightly source에 기존 GDS patch set(41커밋)을 포팅했습니다.
- upstream `0.9.0.12`의 변경사항을 유지했습니다.
- `0.9.0.12-1`에는 `9.0.10-3`까지의 WebUI, reader/cache, scanner, cover 관련 GDS hotfix와 `0.9.0.12` official base port를 포함했습니다.
- 웹소설 파일명 completion marker와 range marker 인식을 보강했고, WebUI 우측 JumpBar/list 클릭 스크롤과 한국어 `Ended` 표시를 보정했습니다.
- **GDS mtime 우회**: rclone FUSE 마운트의 stale directory mtime으로 인한 Library Scan 누락을 해결했습니다. GDS 라이브러리는 항상 디렉터리를 전수 열거합니다.
- OPDS: upstream #4759에서 정식 해결되어(단일엔트리+병합cbz) GDS 패치스택에 별도 OPDS 커스텀 없이 upstream 동작을 그대로 상속합니다.
- `9.0.7-5`에는 duplicate broken/valid EPUB row에서 readable EPUB row를 우선 선택하는 reader/cache hotfix를 포함했습니다.
- `9.0.7-4`의 GDS targeted series scan 후 word-count 분석과 전역 metadata/cache cleanup을 건너뛰는 hotfix를 유지했습니다.
- `9.0.7-3`에는 mixed-root GDS series scan root 축소, mixed-format scan batching, WebUI cover cache-busting을 포함했습니다.
- duplicate manifest EPUB, EPUB `1/1` navigation, TXT fallback cover font, GDS archive per-volume cover regeneration을 포함했습니다.
- GDS EPUB/PDF/TXT scanner shortcut page-count 문제, malformed YAML fallback, single-spine EPUB virtual page regression을 포함했습니다.
- 대형 GDS 강제 스캔의 OOM 완화를 위해 GDS 라이브러리 post-scan 작업을 시리즈 단위 저메모리 직렬 경로로 처리합니다.
- WebUI hotfix 포함으로 Angular production bundle을 다시 빌드했고, runtime image의 WebUI bundle에 unnamed metadata filter default storage key가 포함되어 있음을 확인했습니다.
- Docker Buildx로 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 이미지를 생성했습니다.
- `linux/amd64` 이미지는 release image startup smoke에서 `/api/health` 200을 확인했습니다.
- `linux/arm64` 이미지는 같은 소스와 prebuilt production UI로 빌드해 GHCR multi-arch manifest에 포함했고, CT101 qemu smoke test에서 pushed GHCR unified tag의 `/api/health` `Ok`를 확인했습니다.
- `linux/arm/v7` 이미지는 같은 소스와 prebuilt production UI로 빌드해 GHCR multi-arch manifest에 포함했고, CT101 qemu 8.1.5 smoke test에서 pushed GHCR unified tag의 `/api/health` `Ok`를 확인했습니다.
- GHCR `0.9.0.12-1`와 `latest`는 같은 amd64/arm64/armv7 multi-arch manifest를 가리킵니다.
- local amd64 startup smoke, production DB clone 기반 reader/API targeted validation, Windows amd64 runtime smoke, Windows ARM64 runtime smoke, copied original-layout fixture scan을 통과했습니다.
- UI runtime bundle에서 sourcemap을 제외하고 `localhost:5000`, `:5000/api`, Angular development mode 문자열이 없는 것을 확인했습니다.
- 제목 기반 TXT fallback cover 생성을 위해 Docker image에 Nanum Gothic Regular/Bold 폰트를 포함했습니다.
- 중간 테스트 이미지와 webtoon patch tree는 배포 패키지에 넣지 않았습니다.
- 큰 binary 파일은 Git repo에 직접 commit하지 않습니다.

## 제한

- 이 빌드는 공식 Kavita 이미지가 아닙니다.
- `linux/arm64`와 `linux/arm/v7`는 qemu smoke 검증 기준이며, native ARM 실서비스 검증은 별도로 수행해야 합니다.
- 기존 Kavita 데이터베이스에 적용하기 전에는 백업을 권장합니다.
