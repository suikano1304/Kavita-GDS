# 빌드 노트

## 목적

Kavita official `0.9.0.12` nightly 기반 GDS scanfix 빌드 `0.9.0.12-1`을 Docker/GHCR 배포용으로 패키징했습니다.

이 배포는 기존 `9.0.10-3`까지의 GDS/rclone 수정과 official Kavita `0.9.0.12` nightly 병합을 포함합니다. upstream `0.9.0.12`의 성능 개선도 유지했습니다. 상세 변경 내역은 [CHANGELOG_KO.md](CHANGELOG_KO.md)를 참고하세요.

## 포함 플랫폼

- `linux/amd64`
- `linux/arm64` (GHCR multi-arch manifest)
- `linux/arm/v7` (GHCR multi-arch manifest)

## 산출물

Primary release image:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-1
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
ghcr.io/suikano1304/kavita-gds:0.9.0.12-1
```

현재 GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:0.9.0.12-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:8ed8199bf1c62b54e629f86e23482d81b43fb2880320b82c9275ea5ea0d66cd8
linux/amd64=sha256:202b4bf266fdd5b6b5176967a794343345ca9327f484f8b40176b1187a58fff8
linux/arm64=sha256:099c07ddcfa68973ef4fc039d8d277180a48f2c6d503bc328b7883dd436b1f8e
linux/arm/v7=sha256:86fdbf199cde125abb57ace0c64188c74925d0bc76119b0177ac4377d69fc8fe
```

GHCR는 Docker buildx `--push`로 직접 publish했습니다.

## 검증 내용

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
