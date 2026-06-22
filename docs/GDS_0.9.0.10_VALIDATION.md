# Kavita-GDS 0.9.0.10 검증 기록

최초 작성일: 2026-06-22

이 문서는 official Kavita `0.9.0.10` nightly 기반 Kavita-GDS `9.0.10-1` 릴리스 검증 결과를 기록한다. 공개 문서에는 실제 작품명, library/series/chapter id, 전체 media path를 기록하지 않는다.

## 2026-06-22 `9.0.10-1` release

- GHCR `9.0.10-1`와 `latest`는 같은 multi-arch manifest를 가리킨다.
- 포함 플랫폼은 `linux/amd64`, `linux/arm64`, `linux/arm/v7`이다.
- official Kavita `0.9.0.10` nightly를 새 base로 사용하고 기존 GDS patch set을 다시 적용했다.
- upstream `0.9.0.10`의 대량 파일 단일 디렉터리 scanner 성능 개선을 유지했다.

```text
multiarch digest=sha256:fe44c893aa1bc38942d1ab86ff028f0dff340a175cdd260090f01e41e76cf7ff

linux/amd64=sha256:a1c7ebf40c2b7205fca62688b5f6ad0757d85d871c5938c55921a1f7c920e2de
linux/arm64=sha256:778f7161d38fa04b4178822ffe62e9c85c21b897bd5be8721a81140bf7b8bdcd
linux/arm/v7=sha256:1f238eb2148d428bdc1d61f9418a269d81b870cc2ef752af144c0a250fa005db
```

검증:

- Source build passed.
- Focused GDS service regression tests passed.
- Full service-test failures observed during this validation window reproduced on clean upstream `0.9.0.10`, so they were classified as upstream-baseline for this port.
- UI production bundle build passed.
- RID-specific backend packages for `linux-x64` and `linux-arm64` were built.
- Local `linux/amd64` Docker startup smoke reached `/api/health` `Ok`.
- Production-targeted validation used a production DB online-backup clone and read-only media mount. TXT, archive, EPUB, and PDF reader/API smoke passed; SQLite integrity stayed `ok`; no new MediaErrors, SQLite, disk I/O, database-lock, 500, 404, or fatal log patterns were observed.
- Windows Docker Desktop/WSL off-host amd64 runtime smoke reached `/api/health` `Ok`.
- Windows Docker Desktop/WSL off-host ARM64 qemu runtime smoke reached `/api/health` `Ok`.
- CT101 qemu ARMv7 runtime smoke reached `/api/health` `Ok` with the pushed GHCR unified tag. The current qemu 10.2.1 ARM32 emulator hit a translator assertion before Kavita startup; rerunning with qemu 8.1.5 completed successfully.
- Windows copied original-layout fixture scan passed with expected source-only MediaErrors and no SQLite/disk/database-lock failures.
- Production `kavita` was not rolled to `9.0.10-1` as part of the publish. Production rollout is a separate operation.

## 빌드 산출물 확인

- UI production build를 한 번 수행한 뒤 Docker build context에 반영했다.
- `linux/amd64` Docker platform은 .NET RID `linux-x64` publish output을 사용했다.
- `linux/arm64` Docker platform은 .NET RID `linux-arm64` publish output을 사용했다.
- `linux/arm/v7` Docker platform은 .NET RID `linux-arm` publish output을 사용했다.
- Docker buildx는 하나의 Dockerfile에서 `TARGETPLATFORM`에 따라 해당 runtime tarball을 선택했다.
- GHCR에는 버전 태그와 `latest`를 같은 multi-arch manifest로 publish했다.

## 특이사항

- 운영 컨테이너는 publish 과정에서 교체하지 않았다.
- Broad real-media scan/full verifier는 고부하 I/O 위험 때문에 이 릴리스 publish gate에 포함하지 않았다.
- 기존 production DB/source baseline의 일부 duplicate path 및 source-only media error debt는 code regression으로 분류하지 않았다. Reader/API smoke는 해당 baseline을 새 SQLite/log failure 없이 통과했다.
