# 빌드 노트

## 목적

Kavita official `0.9.0.10` nightly 기반 GDS scanfix 빌드 `9.0.10-1`를 Docker/GHCR 배포용으로 패키징했습니다.

이 배포는 기존 `9.0.7-6`까지의 GDS/rclone 수정과 official Kavita `0.9.0.10` nightly 병합을 포함합니다. upstream `0.9.0.10`의 대량 파일 단일 디렉터리 scan 성능 개선도 유지했습니다. 상세 변경 내역은 [CHANGELOG_KO.md](CHANGELOG_KO.md)를 참고하세요.

## 포함 플랫폼

- `linux/amd64`
- `linux/arm64` (GHCR multi-arch manifest)

`linux/arm/v7`는 이번 `9.0.10-1` 초기 manifest에 포함하지 않았습니다. 별도 runtime smoke를 통과한 뒤 다시 포함할 수 있습니다.

## 산출물

Primary release image:

```text
ghcr.io/suikano1304/kavita-gds:9.0.10-1
ghcr.io/suikano1304/kavita-gds:latest
```

RID package outputs used by Docker buildx:

```text
kavita-linux-x64.tar.gz
kavita-linux-arm64.tar.gz
```

권장 이미지 태그:

```text
ghcr.io/suikano1304/kavita-gds:9.0.10-1
```

현재 GHCR 기준:

```text
ghcr.io/suikano1304/kavita-gds:9.0.10-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:c43c28dc83cf03b4af11b77337d2a54368cb33d850be0474df462d01de3ec8d0
linux/amd64=sha256:a1c7ebf40c2b7205fca62688b5f6ad0757d85d871c5938c55921a1f7c920e2de
linux/arm64=sha256:778f7161d38fa04b4178822ffe62e9c85c21b897bd5be8721a81140bf7b8bdcd
linux/arm/v7=not included in the initial 9.0.10-1 manifest
```

GHCR는 Docker buildx `--push`로 직접 publish했습니다.

## 검증 내용

- official `0.9.0.10` nightly source에 기존 GDS patch set을 포팅했습니다.
- upstream `0.9.0.10`의 scan 성능 개선을 유지했습니다.
- `9.0.10-1`에는 `9.0.7-6`까지의 WebUI, reader/cache, scanner, cover 관련 GDS hotfix를 포함했습니다.
- OPDS 호환성 실험 패치는 최종 배포 전에 원복했으며, 이번 릴리스는 새 OPDS 동작을 claim하지 않습니다.
- `9.0.7-5`에는 duplicate broken/valid EPUB row에서 readable EPUB row를 우선 선택하는 reader/cache hotfix를 포함했습니다.
- `9.0.7-4`의 GDS targeted series scan 후 word-count 분석과 전역 metadata/cache cleanup을 건너뛰는 hotfix를 유지했습니다.
- `9.0.7-3`에는 mixed-root GDS series scan root 축소, mixed-format scan batching, WebUI cover cache-busting을 포함했습니다.
- duplicate manifest EPUB, EPUB `1/1` navigation, TXT fallback cover font, GDS archive per-volume cover regeneration을 포함했습니다.
- GDS EPUB/PDF/TXT scanner shortcut page-count 문제, malformed YAML fallback, single-spine EPUB virtual page regression을 포함했습니다.
- 대형 GDS 강제 스캔의 OOM 완화를 위해 GDS 라이브러리 post-scan 작업을 시리즈 단위 저메모리 직렬 경로로 처리합니다.
- WebUI hotfix 포함으로 Angular production bundle을 다시 빌드했고, runtime image의 WebUI bundle에 unnamed metadata filter default storage key가 포함되어 있음을 확인했습니다.
- Docker Buildx로 `linux/amd64`, `linux/arm64` 이미지를 생성했습니다.
- `linux/amd64` 이미지는 release image startup smoke에서 `/api/health` 200을 확인했습니다.
- `linux/arm64` 이미지는 같은 소스와 prebuilt production UI로 빌드해 GHCR multi-arch manifest에 포함했고, Windows Docker Desktop/WSL qemu smoke test에서 `/api/health` `Ok`를 확인했습니다.
- GHCR `9.0.10-1`와 `latest`는 같은 amd64/arm64 multi-arch manifest를 가리킵니다.
- local amd64 startup smoke, production DB clone 기반 reader/API targeted validation, Windows amd64 runtime smoke, Windows ARM64 runtime smoke, copied original-layout fixture scan을 통과했습니다.
- UI runtime bundle에서 sourcemap을 제외하고 `localhost:5000`, `:5000/api`, Angular development mode 문자열이 없는 것을 확인했습니다.
- 제목 기반 TXT fallback cover 생성을 위해 Docker image에 Nanum Gothic Regular/Bold 폰트를 포함했습니다.
- 중간 테스트 이미지와 webtoon patch tree는 배포 패키지에 넣지 않았습니다.
- 큰 binary 파일은 Git repo에 직접 commit하지 않습니다.

## 제한

- 이 빌드는 공식 Kavita 이미지가 아닙니다.
- `linux/arm64`는 qemu smoke 검증 기준이며, native ARM 실서비스 검증은 별도로 수행해야 합니다.
- `linux/arm/v7`는 이번 manifest에 포함하지 않았습니다.
- 기존 Kavita 데이터베이스에 적용하기 전에는 백업을 권장합니다.
