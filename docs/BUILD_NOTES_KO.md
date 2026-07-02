# 빌드 노트

## 목적

Kavita official `0.9.0.12` nightly 기반 GDS 빌드 `0.9.0.12-2`를 Docker/GHCR 배포용으로 패키징했습니다.

이번 배포는 한국어 검색 공백/유니코드(NFC/NFD) 정규화 개선을 포함하는 GDS 자체 패치입니다. 이전 `0.9.0.12-1`까지의 GDS/rclone 수정과 official Kavita `0.9.0.12` nightly 병합 내용도 그대로 포함합니다. 상세 변경 내역은 [CHANGELOG_KO.md](CHANGELOG_KO.md)를 참고하세요.

## 포함 플랫폼

- `linux/amd64`
- `linux/arm64` (GHCR multi-arch manifest)
- `linux/arm/v7` (GHCR multi-arch manifest)

## 산출물

Primary release image:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-2
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
ghcr.io/suikano1304/kavita-gds:0.9.0.12-2
```

현재 GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-2
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:3ea108084fa6b71a7939d742aa55ff3c9a16cfe608261b90f75e520045b2c3d0
linux/amd64=sha256:e3ab2aaf34df671c7bcffe8c1d1002ce50b6812a3de99f73028d01854314dda6
linux/arm64=sha256:fef06bcae28254882c8007340c46be77b574f2d90825cb32f79415bbb0d83ffd
linux/arm/v7=sha256:c570a08f06df8fb653e1f99827a4cc8475e545795e8e575060b44d281f430dc6
```

GHCR는 Docker buildx `--push`로 직접 publish했습니다 (`--no-cache` 전체 재빌드).

이전 `0.9.0.12-1` GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-1
multiarch digest=sha256:8ed8199bf1c62b54e629f86e23482d81b43fb2880320b82c9275ea5ea0d66cd8
```

## 검증 내용

- **`0.9.0.12-2` 한국어 검색 정규화 개선**: `ToNormalized()`에 유니코드 NFC 정규화를 추가해 분해형(NFD) 한글 입력도 완성형(NFC) DB 데이터와 매칭되도록 했습니다. `Chapter.NormalizedTitleName`, `Library.NormalizedName` 필드를 추가해 챕터/라이브러리도 Series와 동일하게 띄어쓰기 무관 검색이 가능해졌습니다. `AppUserCollection`/`ReadingList`/`Library` 검색의 정규화 필드 누락·불일치 버그 3건을 수정했습니다. 신규 `ManualMigrateKoreanSearchNormalizationBackfill` 마이그레이션이 서버 시작 시 1회 자동으로 기존 레코드의 정규화 필드를 재계산합니다. `Kavita.Common.Tests`에 `ToNormalized()` 단위 테스트를 추가했습니다.
- Docker buildx로 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 이미지를 `--no-cache`로 재빌드하여 GHCR에 push했습니다.
- 로컬 tarball 패키징 시 실행 파일 rename(`Kavita.Server`→`Kavita`)과 `config/appsettings-init.json` 포함을 공식 `build.sh` 규칙과 동일하게 맞췄습니다.
- push한 이미지를 검증할 때는 로컬 stale 태그를 피하기 위해 `docker rmi <tag> -f` 후 명시적으로 재pull하여 digest 일치를 확인했습니다.
- `linux/amd64`, `linux/arm64`(qemu), `linux/arm/v7`(qemu, `tonistiigi/binfmt:qemu-v8.1.5` arm 핸들러로 uninstall 후 재install) 각각 pushed GHCR unified tag의 `/api/health` `Ok`, Docker health `healthy`를 확인했습니다.
- `docker buildx imagetools inspect`로 `linux/amd64`, `linux/arm64`, `linux/arm/v7` 3개 플랫폼 매니페스트가 모두 존재함을 확인했습니다.
- 프로덕션 롤아웃은 사용자 확인 전까지 보류합니다.
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
