# Kavita-GDS 검증 매트릭스

작성일: 2026-05-31

이 문서는 “스캐너와 Kavita 문제를 근본적으로 분석했다”고 판단하기 위한 증거 기준을 정리한다. 원인 설명만으로는 완료가 아니며, 운영 DB와 운영 이미지에서 재현/개선 여부가 확인되어야 한다.

## 현재 판정

현재 상태는 `분석 진행 중`이다.

- 원인 후보는 대부분 분리됐다.
- 진단 도구와 postflight gate는 준비됐다.
- `0.9.0.2-5` 후보는 `0.9.0.2-3` 이후 GDS 타입 처리 보강 커밋까지 포함해 빌드됐다.
- `0.9.0.2-5` 공개 GHCR image의 `linux/amd64` startup smoke test와 `linux/arm64` manifest 확인은 완료됐다.
- release tag `v0.9.0.2-5`는 `fda1eab`이고, 현재 `main`의 차이는 진단 스크립트와 문서 보강이며 Docker runtime image 변경은 아니다.
- 운영 컨테이너는 아직 `local/kavita-gds:0.9.0.2-1`이므로, 운영 DB에서 최신 release/source의 회복/cleanup 효과는 아직 검증되지 않았다.

## 2026-05-31 17:55 공개 GHCR image smoke

운영 컨테이너는 변경하지 않고, 공개 이미지 `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`를 운영 DB 사본으로 별도 포트에서 검증했다.

- Pull digest: `sha256:<redacted>`
- Runtime image id: `sha256:<redacted>`
- Test container: `kavita-gds-ghcr-0902-5-smoke`, port `5016:5000`
- `/api/health`: `Ok`
- `/api/admin/exists`: `true`
- Container state: `running healthy`, restart count `0`
- Startup logs: manual migrations and migrations completed, `BaseUrl=/`, `Kavita - v0.9.0.2`
- Startup 후 DB copy: `integrity_check ok`, `foreign_key_check` 위반 없음
- Web UI bundle: `/kavita/wwwroot`에서 `localhost:5000`, `:5000/api`, Angular development mode 문자열 없음
- Production env chunk: document base URL 기반 `apiUrl: ${base}api/`, `hubUrl: ${base}hubs/`

테스트 컨테이너와 임시 DB 사본은 검증 후 삭제했다.

## 2026-05-31 18:36 운영 baseline

운영 컨테이너를 변경하지 않고 최신 postflight 도구 기준으로 read-only preflight를 다시 수집했다. 기존 17:45 baseline은 MediaError 출력 상위 40개 제한이 JSON에도 들어가 있었으므로 after 비교 기준으로 쓰지 않는다.

- Diagnostics: `/tmp/kavita-gds-preflight/before-diagnostics.json`
- DB snapshot: `/tmp/kavita-gds-preflight/before-kavita.db`
- Scan log summary: `/tmp/kavita-gds-preflight/before-scan-log-summary.json`
- Reader latency summary: `/tmp/kavita-gds-preflight/before-reader-latency-summary.json`
- Manifest: `/tmp/kavita-gds-preflight/before-manifest.txt`

확인 결과:

- `integrity_check`: `ok`
- `foreign_key_check`: 위반 없음
- 운영 DB row count: `Series 23856`, `MangaFile 136427`, `MediaError 637`
- `media_errors_by_ext_comment` JSON sum: `637`
- `media_error_classification` JSON sum: `637`
- `Pages=0`: 49개, 세부는 CBZ 10개와 ZIP 39개
- Archive validation: CBZ 10개는 nested archive 84개 구조, ZIP 39개는 내부 이미지 13,301개가 있어 회복 가능 debt
- same-series/same-volume duplicate cleanup 후보: 26개 group
- cross-series duplicate: 153개 group, 자동 삭제 제외 대상
- scan log baseline: 52 scan rows, finished 34, non-forced processed series 272
- slow reader request: 8개 중 7개가 ZIP이고, 가장 느린 `/api/reader/image`는 약 24.3초

self-check:

- DB postflight self-check: `FAIL` 없음. `Pages=0`, recoverable archive, same-series duplicate는 같은 baseline 비교이므로 `WARN`으로 남음
- scan postflight self-check: `FAIL` 없음. non-forced processed series와 churn scan count는 같은 baseline 비교이므로 `WARN`으로 남음

`--check-covers`는 현재 DB/config cover reference만 확인하는 빠른 검사로 분리했다. rclone source `cover.*`와 `kavita.yaml` cover hint를 직접 확인하는 느린 검사는 `--check-cover-source-files`를 추가한 별도 단계로만 실행한다.

## 2026-05-31 18:57 cover gate 기준

운영 컨테이너를 변경하지 않고 `before-kavita.db` snapshot 기준으로 빠른 cover baseline을 별도 수집했다.

- Diagnostics: `/tmp/kavita-gds-preflight/before-covers-fast-diagnostics.json`
- Self-check: `/tmp/kavita-gds-preflight/before-covers-fast-selfcheck-diagnostics.json`
- `source_cover_probe`: `False`
- GDS config cover reference: `4,423`
- TXT config cover series: `3,650`
- TXT config cover 누락: production-library-e `2,061`, 웹소설 단행 `4`
- postflight self-check: `FAIL` 없음
- source cover/YAML hint 기반 missing-cover debt gate는 `--check-cover-source-files` 없이 실행했으므로 `WARN`으로 skip됨

해석:

- 일반 postflight에는 `--check-covers`를 넣어도 rclone source cover probe를 수행하지 않는다.
- TXT title-cover fallback의 운영 효과는 운영 컨테이너를 `0.9.0.2-5`로 전환하고 대상 라이브러리를 재스캔한 뒤 같은 fast cover gate로 config cover 증가/감소를 확인한다.
- 원본 `cover.*` 또는 YAML cover hint까지 확인해야 할 때만 `--check-covers --check-cover-source-files`를 별도 실행한다.

## 2026-05-31 19:03 운영 read-only 재확인

운영 컨테이너와 DB를 변경하지 않고 최신 `main` 진단 도구로 현재 상태를 다시 확인했다.

- Runtime image: `local/kavita-gds:0.9.0.2-1`
- Compose image: `local/kavita-gds:0.9.0.2-1`
- Container state: `healthy`
- Current diagnostics: `/tmp/kavita-gds-preflight/current-readonly-diagnostics.json`
- Current cover diagnostics: `/tmp/kavita-gds-preflight/current-covers-fast-diagnostics.json`
- DB snapshot: `/tmp/kavita-gds-preflight/current-readonly-kavita.db`
- `integrity_check`: `ok`
- `foreign_key_check`: 위반 없음
- `Pages=0`: 49개로 baseline과 동일
- 직접 이미지가 있는 복구 가능 ZIP `Pages=0`: 39개로 baseline과 동일
- same-series/same-volume duplicate cleanup 후보: 26개 group으로 baseline과 동일
- MediaError: 637개로 baseline과 동일
- GDS config cover reference: 4,423개로 baseline과 동일
- TXT config cover series: 3,650개로 baseline과 동일

postflight gate 결과:

- `PASS`: SQLite integrity, FK, cross-series duplicate 증가 없음, MediaError 증가 없음, config cover reference 감소 없음, TXT config cover 감소 없음
- `WARN`: `Pages=0`, 복구 가능 `Pages=0` archive, same-series duplicate가 아직 줄지 않음
- `WARN`: source cover/YAML hint 기반 TXT missing-cover debt는 느린 source probe를 실행하지 않아 skip

로그 분석 결과:

- `2026-05-31` 로그에서 library scan row는 52개다.
- 느린 library scan의 원인은 두 종류로 분리된다. 강제 스캔은 file discovery/rclone listing 시간이 주된 비용이고, 일부 일반 스캔은 old runtime에서 series update 시간이 주된 비용이다.
- slow reader request는 3초 기준 21개이며, reader latency 상관분석에서는 확인 가능한 18개 중 17개가 ZIP reader 요청이다. 대부분 cache가 있어도 100MB 이상 ZIP chapter에서 지연이 남았다.
- report 폴더의 외부 제보는 `0.9.0.2-4` 이미지에서 `localhost:5000`을 호출한 Web UI dev bundle 증상으로 확인했다. 들여쓰기 깨짐만으로 설명되는 문제는 아니다.

해석:

- 현재 운영 DB는 정합성 측면에서 안정적이고, read-only 기준 악화는 없다.
- 그러나 운영 runtime이 아직 `0.9.0.2-1`이므로 `0.9.0.2-5`의 startup/FK 진단, duplicate cleanup, Pages=0 회복, TXT fallback cover 효과는 운영 DB에서 완료 증거가 아니다.
- 목표 완료에는 운영 전환 후 같은 postflight gate를 통과하고, 작은 라이브러리부터 재스캔한 실제 개선 결과가 필요하다.

## 2026-05-31 19:07 최신 이미지 스캔 smoke 반례

운영 컨테이너를 변경하지 않고 `0.9.0.2-5` 이미지를 운영 DB snapshot과 임시 config로 별도 기동했다.

- Test container: `kavita-gds-scan-smoke-0902-5`
- Image: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`
- Port: `5017:5000`
- Config: LXC `/tmp/kavita-scan-smoke-0902-5-config`
- Media mount: `/your/gds/mount:/mnt/gds:ro`
- Startup: `/api/health` returned `Ok`
- Startup DB state: migrations completed, no restart, no startup FK failure

그 다음 운영 DB 사본에서 텍스트 중심 라이브러리 `libraryId=<redacted>` force scan을 API로 요청했다. 요청 자체는 `200`으로 enqueue됐지만, 로그는 다음 상태에서 진행되지 않았다.

```text
Beginning file scan on production-library-e
Warning! production-library-e has metadata turned off
```

관찰:

- 컨테이너는 `healthy`, restart count `0`이었다.
- worker thread 중 하나가 FUSE request 대기 상태로 보였다.
- rclone service는 active였고 업로드/삭제/rename 징후는 없었다.
- 불필요한 rclone 부하를 남기지 않기 위해 테스트 컨테이너와 임시 config/DB는 제거했다.

해석:

- `0.9.0.2-5`는 현재 운영 DB snapshot으로 startup FK 문제를 재현하지 않았다.
- 그러나 대형 텍스트 라이브러리 force scan은 최신 이미지에서도 cold rclone/listing 조건에서 file discovery 단계가 길게 멈출 수 있다.
- 따라서 운영 전환 후 첫 검증은 대형/전체 force scan이 아니라 작은 라이브러리, 특정 series scan, 또는 이미 file discovery가 안정된 범위부터 진행해야 한다.
- scanner 개선 검증은 series update 비용 감소와 file discovery/rclone 비용을 계속 분리해서 봐야 한다.

## 완료 조건

| Requirement | 완료 증거 | 현재 증거 | 판정 |
| --- | --- | --- | --- |
| 스캔 변경 감지 과전파 원인 규명 | 변경 후보가 없는 라이브러리 일반 재스캔에서 `Processing series`가 대량 발생하지 않음 | 테스트 재스캔에서 `0 Series / 0 files / 770 ms` 확인. 운영은 아직 `0.9.0.2-1` | 부분 완료 |
| archive `Pages=0` 회복 | 직접 이미지가 있는 `Pages=0` ZIP이 운영 재스캔 후 감소 | 현재 39개 ZIP은 내부 이미지 13,301개가 있어 복구 가능 debt로 분류됨 | 미검증 |
| nested archive 한계 분리 | nested archive CBZ를 자동 회복 대상에서 제외하고 자료 구조 문제로 분류 | 10개 CBZ가 내부 ZIP 84개 구조로 분류됨 | 완료 |
| `kavita.yaml` 우선 메타데이터 | `EnableMetadata=false`에서도 YAML 안전 필드가 반영되고 `meta.Name`이 회차명을 덮지 않음 | 표본 검증과 source 보정 완료 | 부분 완료 |
| GDS reader/title routing | GDS mixed-format에서 chapter-info/title rendering 예외가 사라짐 | `0.9.0.2-5` 후보에 이어보기/볼륨 표시와 오래된 file type migration 보강 포함. 운영 적용 전 | 부분 완료 |
| cover cache 보존 | source `cover.*`가 없어도 기존 config cover cache가 불필요하게 삭제되지 않음 | fast cover gate 준비, 현재 GDS config cover reference 4,423개 baseline 확보 | 미검증 |
| TXT fallback cover | 원본 cover 없는 TXT가 오류가 아니라 config cover fallback으로 처리됨 | fast TXT config cover gate 준비, 현재 TXT config cover 3,650개 baseline 확보 | 미검증 |
| duplicate cleanup | same-series/same-volume duplicate group이 운영 재스캔 후 감소 | 현재 same-series/same-volume group이 남아 있음. cleanup patch는 `0.9.0.2-5` 포함 | 미검증 |
| cross-series duplicate 정책 | 자동 삭제하지 않고 수동 판단 대상으로 분리 | cross-series group 153개가 자동 cleanup 제외 대상으로 문서화됨 | 완료 |
| startup FK 제보 분리 | x86/NAS 정상, Oracle A1 사례는 DB/migration/volume 상태 확인 대상으로 분리 | `0.9.0.2-5` 빈 config startup 통과. 제보는 Oracle A1 환경별 DB/migration/volume 비교 대상으로 분리 | 부분 완료 |
| scan timing 병목 분리 | file discovery, series update, total time을 별도 지표로 수집 | scan log summary 도구와 실제 로그 분석, 별도 `0.9.0.2-5` smoke에서 force scan file discovery 대기 재현 | 완료 |
| reader latency 분리 | scanner 병목과 reader cache/rclone 지연을 별도 지표로 수집 | slow reader request 8개 중 7개 ZIP, cache/file size 상관분석 완료 | 완료 |
| MediaError 원인 분류 | scanner bug, EPUB 구조 문제, PDF 문제, archive 문제를 분리 | MediaError classification 도구와 운영 DB 분류 완료 | 완료 |
| source/release/운영 일치 | 운영 이미지가 공개 release와 같은 source 기준으로 실행됨 | `0.9.0.2-5` 후보 빌드 완료. 운영은 아직 `0.9.0.2-1` | 미검증 |

## Postflight 기준

운영에 최신 source가 반영된 release를 적용한 뒤 아래가 확인되어야 최종 완료로 볼 수 있다. 현재 운영 검증 대상은 `0.9.0.2-5` 후보이다.

1. `PRAGMA integrity_check`가 `ok`다.
2. `PRAGMA foreign_key_check` 결과가 비어 있다.
3. `Pages=0` 총량이 증가하지 않는다.
4. 직접 이미지가 있는 복구 가능 `Pages=0` archive가 감소한다.
5. same-series/same-volume duplicate group이 감소하거나 최소한 증가하지 않는다.
6. cross-series duplicate는 자동 삭제되지 않는다.
7. MediaError 총량과 원인 분류별 count가 증가하지 않는다.
8. scan log에서 변경 후보가 없는 일반 재스캔이 대량 `Processing series`를 만들지 않는다.
9. reader latency 지표는 scanner timing과 별도 파일로 남는다.
10. cover cache 수가 불필요하게 감소하지 않는다.
11. TXT fallback cover는 Kavita config 경로에만 생성되고 GDS/rclone 원본에는 쓰지 않는다.

## 권장 실행 순서

운영 적용 전 baseline:

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --scan-log /your/kavita/config/logs/kavita20260531.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label before \
  --snapshot-db \
  --check-archives \
  --check-covers
```

운영 적용 후 postflight:

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --scan-log /your/kavita/config/logs/kavita20260531.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label after \
  --snapshot-db \
  --check-archives \
  --check-covers \
  --compare-json /tmp/kavita-gds-preflight/before-diagnostics.json \
  --compare-scan-json /tmp/kavita-gds-preflight/before-scan-log-summary.json \
  --postflight-gates
```

postflight 결과에 `FAIL`이 없어야 한다. DB gate의 `WARN`은 남은 debt가 줄지 않았다는 뜻이고, scan gate의 `WARN`은 비강제 재스캔 churn이 줄지 않았다는 뜻이므로 목표 완료가 아니라 추가 분석 대상으로 남긴다.

live DB는 `--snapshot-db`로 `/tmp` 사본을 만든 뒤 분석한다. 이 방식은 운영 DB를 수정하지 않으면서 WAL/SHM 대기로 preflight가 멈추는 문제를 피한다.

`--check-covers`를 before/after 양쪽에 넣으면 DB/config 기준 cover gate도 함께 출력된다. `GDS config cover references decreased`와 `TXT config covers decreased`는 실패로 본다. source `cover.*`와 YAML hint까지 확인하는 `TXT missing-cover debt` gate는 `--check-covers --check-cover-source-files`를 before/after 양쪽에 넣은 경우에만 의미가 있으며, rclone mount에서는 오래 걸릴 수 있어 별도 단계로 실행한다.

`--compare-scan-json`을 넣으면 scan log summary에도 postflight gate가 붙는다. `non-forced processed series increased` 또는 `non-forced churn scan count increased`는 scanner churn이 악화된 것으로 보고 실패 처리한다. 두 항목이 `WARN`이면 scanner가 더 나빠지지는 않았지만 목표한 재스캔 churn 감소가 아직 증명되지 않은 상태다.
