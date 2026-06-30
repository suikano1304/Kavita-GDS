# Kavita-GDS 0.9.0.7 검증 기록

최초 작성일: 2026-06-06
최신 갱신일: 2026-06-11

이 문서는 official Kavita `0.9.0.7` nightly 기반 Kavita-GDS `9.0.7` 계열 릴리스 검증 결과를 기록한다. 검증은 별도 테스트 컨테이너와 테스트 DB 사본으로 먼저 수행하고, 운영 반영이 있는 릴리스는 적용 후 health와 Docker 상태를 별도로 확인한다.

## 2026-06-11 `9.0.7-6` metadata-filter release candidate

- GHCR `9.0.7-6`와 `latest`는 같은 multi-arch manifest를 가리킨다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` manifest를 모두 포함한다.
- WebUI unnamed metadata filter default regression은 이름 없는 저장을 현재 route 기본 metadata filter 저장으로 처리하도록 수정했다.
- blank-name 기본 필터 저장에서도 저장 버튼이 비활성화되지 않도록 보정했다.
- OPDS 호환성 실험 패치는 최종 배포 전에 원복했고, 기존 OPDS 기능 외 새 OPDS 동작은 이번 릴리스 검증 범위에서 제외했다.
- 공개 문서에는 샘플명, chapter id, media path를 기록하지 않고 issue class만 기록한다.

```text
multiarch digest=sha256:bbdfcff8d1e6b070af1cad78a82c5515ed0292e8e04cb057f839d70cde73206c

linux/amd64=sha256:e1e2ebb9059257bc24d8756e629d768f722646e253b0dca6805071f173b41e0b
linux/arm64=sha256:57109c8ed67bab282d071d7d498fea3b56516d59b15fdb5fb3f3237ab24f98dd
linux/arm/v7=sha256:254c022caed57acb6bfb59788f2f8d9c5ae07060c0454c4d9027a9fbe91f1f4e
```

검증:

- WebUI production build passed and the built bundle contains the unnamed metadata filter default storage path.
- Runtime WebUI bundle contains the blank-name default-filter save-button path.
- `CacheServiceTests` baseline rerun: `DOTNET_EXIT:0`.
- local release image startup smoke:
  - `linux/amd64`: `/api/health` 200
  - `linux/arm64`: qemu `/api/health` 200
  - `linux/arm/v7`: qemu `/api/health` 200
- GHCR manifest inspection:
  - `9.0.7-6`와 `latest` digest가 동일한지 확인했다.
  - amd64, arm64, arm/v7 manifest가 포함되어 있는지 확인했다.
- GHCR pushed image startup smoke:
  - `linux/amd64`: `/api/health` 200, Docker health `healthy`, restart count `0`
  - `linux/arm64`: qemu `/api/health` 200, Docker health `healthy`, restart count `0`
  - `linux/arm/v7`: qemu `/api/health` 200, Docker health `healthy`, restart count `0`
- Production rollout:
  - `kavita` container image `ghcr.io/suikano1304/kavita-gds:9.0.7-6`
  - `/api/health` 200, Docker health `healthy`, restart count `0`
- `kavita-test` smoke:
  - production DB online backup과 read-only fixture mount로 `/api/health` 200을 확인했다.
  - runtime WebUI bundle에 unnamed metadata filter default storage key가 포함되어 있음을 확인했다.

## 2026-06-10 `9.0.7-5` readable book-file selection release

- GHCR `9.0.7-5`와 `latest`는 같은 multi-arch manifest를 가리킨다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` manifest가 모두 포함되어 있다.
- 같은 chapter에 broken/empty EPUB row와 valid EPUB row가 함께 있는 회귀 issue class는 readable EPUB row를 우선 선택하도록 수정했다.
- 공개 문서에는 샘플명, chapter id, media path를 기록하지 않고 issue class만 기록한다.

```text
multiarch digest=sha256:65c7eaed1dc6a21a39c1819f71276c26f748556303e1af904818817be5dfd780

linux/amd64=sha256:7bc92d3c3aaf63c4e7b9acd23c54215ef8ca4641de5b612fa0f327fec5a2e227
linux/arm64=sha256:f9fcf0d95d81325547b380a6ecb1e24b4b369f01d75f7070421dadef2c4f73e4
linux/arm/v7=sha256:6f2bfe3c5ab6069bcd6af7dc1260ebb927d091989031d963825cea8bb63756ba
```

검증:

- `CacheServiceTests` focused regression suite: 24 passed, 0 failed.
- production DB clone + read-only GDS mount에서 affected regression issue class cold-cache 검증:
  - `book-info`: 200
  - `chapters`: 200
  - `book-page` page 0/1: 200
  - EPUB resource: 200
  - cache file size: non-zero valid EPUB size
  - retest window에서 `Central Directory corrupt`, `InvalidDataException`, SQLite/database-lock/Fatal 로그 없음
- pushed GHCR image startup smoke:
  - `linux/amd64`: `/api/health` 200
  - `linux/arm64`: qemu `/api/health` 200
  - `linux/arm/v7`: qemu `/api/health` 200
- production rollout:
  - SQLite online backup created before replacement.
  - production `kavita` moved from `ghcr.io/suikano1304/kavita-gds:9.0.7-4` to `ghcr.io/suikano1304/kavita-gds:9.0.7-5`.
  - post-rollout `/api/health` 200 and Docker health `healthy`.
  - same affected regression issue class passed cold-cache targeted API validation after rollout.

## 범위

- official Kavita `0.9.0.7` nightly 기반 릴리스 확인.
- GDS 전용 reader 동작과 기존 GDS 회귀 수정 유지 여부 확인.
- Kavita+를 사용하지 않는 운영 가정에서 GDS 외 일반 Book 라이브러리 eligibility가 유지되는지 코드 리뷰.
- 테스트 컨테이너 startup, health, version API, DB integrity, reader API, image API, cover API, post-test log 확인.
- 테스트 컨테이너의 GDS source mount가 read-only인지 확인.
- ARM 계열도 같은 source patch set에서 공식 RID/platform 매핑으로 빌드하고 smoke test까지 확인.

## 결과

확장 검증과 multi-arch release smoke를 통과했다.

- health endpoint가 HTTP 200을 반환했다.
- plugin version endpoint가 `0.9.0.7`을 반환했다.
- reader/API 검증 전후 SQLite integrity check가 `ok`를 반환했다.
- 최종 검증 실행 중 새 MediaError가 생성되지 않았다.
- 최종 검증 실행 중 새 404, 500, Fatal, SQLite, database-lock, disk I/O error 로그가 발생하지 않았다.
- GHCR `9.0.7`와 `latest`가 동일한 multi-arch digest를 가리킨다.
- `linux/amd64`, `linux/arm64`, `linux/arm/v7` manifest가 모두 포함되어 있다.
- `linux/arm64`와 `linux/arm/v7`는 pushed GHCR image를 qemu로 시작해 `/api/health` 200 및 Docker health `healthy`를 확인했다.

## GHCR 릴리스

```text
ghcr.io/suikano1304/kavita-gds:9.0.7
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:da791441659ed602a6fbb86f9bb196c4c71754378004556c04399094ad3437e7

linux/amd64=sha256:59ed9200cc906c8737cf086af98656028e9b1f1f87a986db1fa100ae18c30f32
linux/arm64=sha256:8e41c7b01f0167e3d89f46024dbfea6799ccb4ab0dedcdad71b3e9890f426f5a
linux/arm/v7=sha256:48d4d285e41ba9ffe1c2819aa12f4d91b2e69f69d1312c4e60605e5ca0b78869
```

## 빌드 산출물 확인

- UI production build를 한 번 수행한 뒤 `Kavita.Server/wwwroot`에 반영했다.
- .NET publish package를 RID별로 생성했다.
- `linux/amd64` Docker platform은 `kavita-linux-x64.tar.gz`를 사용했다.
- `linux/arm64` Docker platform은 `kavita-linux-arm64.tar.gz`를 사용했다.
- `linux/arm/v7` Docker platform은 `kavita-linux-arm.tar.gz`를 사용했다.
- Docker buildx는 하나의 Dockerfile에서 `TARGETPLATFORM`에 따라 해당 runtime tarball을 선택했다.

## Reader 검증 범위

대표 GDS 샘플과 로컬 fixture 샘플을 이용해 다음 경로를 확인했다.

- TXT book reader: 긴 텍스트 pagination과 TXT 회귀 fixture.
- ZIP/CBZ 계열 archive image reader: chapter-info, file dimensions, image, thumbnail, negative-page clamp.
- EPUB book reader: 정상 EPUB, 문제 재현 EPUB, navigation 누락 fallback, synthetic single-spine TOC fixture.
- PDF reader: raw PDF serving, PDF extraction metadata, extracted page image rendering.
- TXT, archive, EPUB, PDF에 대한 반복 chapter-info cache 호출.
- cover가 존재하는 대표 샘플의 series cover retrieval.

## 특이사항

- synthetic single-spine EPUB fixture는 실제 콘텐츠가 아니라 TOC page mapping 회귀 검증용 파일이다. cover가 없고 HTML page가 매우 작으므로 cover 생성이나 page size 검증 대상이 아니다.
- navigation 누락 EPUB fixture는 매우 작은 generated wrapper page를 반환할 수 있다. 기대 동작은 book-info, TOC, page API가 정상 응답하는 것이다.
- 테스트 DB 사본의 기존 duplicate path 및 loose image counter는 baseline 데이터로 취급했으며, `0.9.0.7` 병합으로 새로 생긴 항목이 아니다.
- 운영 컨테이너는 검증 중 기존 이미지에 그대로 유지했다.

## ARM 적용 결과

`0.9.0.7` 릴리스는 amd64 전용 결과로 마감하지 않았다.

- `linux/amd64`는 .NET RID `linux-x64` publish output을 사용했다.
- `linux/arm64`는 같은 source patch set에서 .NET RID `linux-arm64` publish output을 사용했다.
- `linux/arm/v7`는 같은 source patch set에서 .NET RID `linux-arm` publish output을 사용했다.
- Docker build는 official Kavita 방식처럼 `TARGETPLATFORM`에 따라 RID별 runtime output을 선택했다.
- GHCR에는 버전 태그와 `latest`를 multi-arch manifest로 publish했다.
- `linux/arm64`는 qemu smoke test에서 `/api/health` 200과 Docker health `healthy`를 확인한 뒤 manifest에 포함했다.
- `linux/arm/v7`은 qemu smoke test에서 `/api/health` 200과 Docker health `healthy`를 확인한 뒤 manifest에 포함했다.
