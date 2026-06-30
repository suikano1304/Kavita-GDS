# 2026-05-31 운영 기록

이 문서는 운영 서버에서 수행한 Kavita config 정리, 커버 복구, `kavita.yaml` 메타데이터 적용 검증, GitHub 배포 상태를 남긴 기록입니다.

## 기준 환경

- PVE host에서 LXC `101` (`lxc1-media`)의 Docker Kavita를 운영합니다.
- 운영 compose: `/opt/compose/kavita/docker-compose.yml`
- 운영 config: `/your/kavita/config`
- 운영 컨테이너: `kavita`
- 운영 이미지: `local/kavita-gds:0.9.0.2-1`
- GDS 원본은 LXC에서 `/your/gds/mount`, 컨테이너에서 `/mnt/gds`로 읽기 전용 마운트합니다.

## 커버 복구

기존 active config 정리 과정에서 cover cache 일부가 사라졌고, 남아 있던 config/test config 쪽 cover 파일을 운영 config로 다시 모았습니다.

처리 방향:

- DB만 복구하지 않고, 남아 있는 cover 파일을 `/your/kavita/config/covers`로 최대한 회수했습니다.
- ZFS snapshot이나 PVE backup에 `/your/docker-data` bind mount가 포함되어 있지 않아 사라진 일부 old cover cache는 원본 그대로 복구할 수 없었습니다.
- 이후 Kavita 스캔/메타데이터 작업으로 cover cache가 다시 생성되도록 운영을 유지했습니다.

확인 명령:

```bash
pct exec 101 -- docker ps --filter name=kavita --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}'
pct exec 101 -- python3 - <<'PY'
import os
print(sum(1 for _ in os.scandir('/your/kavita/config/covers')))
PY
```

확인 결과:

- `kavita` 컨테이너는 healthy 상태였습니다.
- 2026-05-31 기준 cover 파일은 약 `5,800+`개까지 회복/재생성되었습니다.

## `kavita.yaml` 메타데이터 적용

기존 GDS 스캔은 `kavita.yaml`의 rich metadata를 제대로 반영하지 못했습니다. 특히 `Summary`, 작가, 출판사, 번역자, 날짜 같은 값이 DB에 들어가지 않거나, 반대로 `meta.Name`이 회차 제목을 덮어써서 회차 정보가 사라지는 문제가 있었습니다.

수정한 동작:

- GDS 라이브러리는 `EnableMetadata=false`여도 같은 폴더의 `kavita.yaml`/`kavita.yml`을 보조 메타데이터로 읽습니다.
- `Summary`, `Genres`, `Tags`, `Language`, `Web Links`, 작가/번역자/출판사/작화가, 발매일, 연령등급 등 안전한 필드를 `ComicInfo`에 반영합니다.
- `meta.Name`은 시리즈명이나 회차 제목을 덮어쓰는 데 사용하지 않습니다.
- 회차 제목은 파일명에서 생성합니다.
- 파일명 제목 생성 시 `#138`, `[1440px]`, `[직스샷]`, trailing `(리디)` 같은 배포/품질 태그를 제거합니다.

관련 소스 위치:

```text
Kavita.Services/Helpers/GdsMetadataParser.cs
Kavita.Services/Reading/ReadingItemService.cs
```

검증한 대표 케이스:

```text
<redacted-media-path>
```

확인 내용:

- 시리즈명은 접두/분류명이 아니라 실제 시리즈명으로 유지됩니다.
- YAML `Summary`가 DB chapter summary에 들어왔습니다.
- 접두가 붙은 중복 시리즈는 생성되지 않았습니다.
- 다른 재스캔 표본에서 회차 제목이 파일명 기반으로 정리되어 들어오는 것을 확인했습니다.

예시:

```text
파일: [분류] <시리즈명> Vol. 6 (완결) (리디)#186.zip
회차 제목: <시리즈명> Vol. 6 (완결)
```

검증 명령:

```bash
pct exec 101 -- python3 - <<'PY'
import sqlite3
con=sqlite3.connect('/your/kavita/config/kavita.db')
print('prefix_dups', con.execute("select count(*) from Series where Name like '[%] %'").fetchone()[0])
for row in con.execute("""
select s.Name,c.TitleName,c.Range
from Chapter c join Volume v on c.VolumeId=v.Id join Series s on v.SeriesId=s.Id
where s.LibraryId=5 and c.TitleName not like '[%] %' and c.TitleName != ''
order by c.Id desc limit 10
"""):
    print(row)
PY
```

주의:

- 이미 스캔된 시리즈는 해당 시리즈가 다시 처리되어야 DB의 `TitleName`이 갱신됩니다.
- 전체 라이브러리 강제 스캔은 ZIP을 다시 열기 때문에 rclone/GDS 환경에서는 오래 걸릴 수 있습니다.
- 빠른 확인이 필요하면 전체 스캔을 기다리지 말고 대상 시리즈만 재스캔하는 편이 낫습니다.

## 배포 상태

공개 GitHub repo:

```text
https://github.com/suikano1304/Kavita-GDS
```

현재 repo의 공개 배포 기준:

- 이미지 이름: `ghcr.io/suikano1304/kavita-gds`
- 권장 태그: `0.9.0.2-5`
- latest 태그도 publish workflow에서 같이 갱신합니다.
- release asset 이름은 `kavita-gds.tar.gz`로 정리했습니다.

현재 문서와 배포 후보는 `0.9.0.2-5` 기준입니다. 운영 컨테이너 적용 여부는 compose의 `image:`와 실행 중인 컨테이너 이미지를 별도로 확인해야 합니다.

배포 확인:

- Release asset: `kavita-gds.tar.gz`
- Release asset SHA256: 저장소 루트 `SHA256SUMS` 기준
- GHCR package visibility: `public`
- GHCR image index는 `linux/amd64`, `linux/arm64`를 포함합니다.

이후 새 버전을 공개 이미지로 올릴 때는 다음 순서로 진행합니다.

1. amd64/arm64 산출물을 새로 build/publish합니다.
2. release asset `kavita-gds.tar.gz`와 `SHA256SUMS`를 갱신합니다.
3. `.github/workflows/publish-ghcr.yml`의 `RELEASE_ASSET_SHA256` 값을 갱신합니다.
4. GitHub Release asset을 올립니다.
5. GitHub Actions `Publish GHCR image` workflow를 실행해 GHCR의 새 버전 태그와 `latest`를 갱신합니다.

## `0.9.0.2-3` 사본 DB startup 검증

운영 컨테이너를 재시작하지 않고, 운영 DB와 appsettings 사본만 사용해 `0.9.0.2-3` 이미지의 startup/migration 동작을 검증했습니다.

검증 방식:

- 운영 config 원본은 그대로 두고 `/tmp/kavita-db-smoke-090203-config`에 `kavita.db`와 `appsettings.json`을 복사했습니다.
- 테스트 컨테이너는 `local/kavita-gds:0.9.0.2-3` 이미지로 별도 포트 `5013`에서 실행했습니다.
- GDS media mount는 읽기 전용으로 붙였습니다.
- 검증 후 테스트 컨테이너는 중지했습니다.

결과:

- startup manual migration 완료
- startup migration 완료
- `/api/health` 응답: `Ok`
- 사본 DB `PRAGMA integrity_check`: `ok`
- 사본 DB `PRAGMA foreign_key_check`: 위반 없음
- 운영 컨테이너는 계속 `local/kavita-gds:0.9.0.2-1`로 실행 중이며 healthy 상태를 유지했습니다.

해석:

- 현재 운영 DB 사본에서는 Oracle 제보와 같은 startup FK 오류가 재현되지 않았습니다.
- 따라서 Oracle 쪽 `SQLite Error 19: FOREIGN KEY constraint failed` 사례는 이미지 아키텍처 자체보다 해당 서버의 기존 DB 상태, 이전 컨테이너와의 전환 상태, 또는 migration history 차이를 우선 확인해야 합니다.
- `0.9.0.2-3`은 이 경우 실제 FK 위반 여부를 로그와 진단 스크립트로 더 직접 확인할 수 있게 만든 진단 릴리즈입니다.

운영 적용 전 남은 확인:

- `0.9.0.2-5`를 운영 컨테이너에 적용한 뒤 작은 라이브러리부터 재스캔합니다.
- same-series/same-volume duplicate file path group이 감소하는지 확인합니다.
- `Pages=0` 잔여 ZIP/CBZ 중 실제로 복구 가능한 파일과 nested archive 구조를 분리합니다.

## 운영 적용 전 baseline

`0.9.0.2-5` 운영 적용 전, 현재 운영 컨테이너와 DB 상태를 다시 확인했습니다.

운영 상태:

- 실행 이미지: `local/kavita-gds:0.9.0.2-1`
- 컨테이너 상태: healthy
- compose 이미지: `local/kavita-gds:0.9.0.2-1`
- DB `PRAGMA integrity_check`: `ok`
- DB `PRAGMA foreign_key_check`: 위반 없음

남은 `Pages=0`:

| LibraryId | Library | Ext | Count |
| --- | --- | --- | ---: |
| <redacted> | production-library-a | `.cbz` | 10 |
| <redacted> | production-library-d | `.zip` | 39 |

남은 duplicate file path:

| LibraryId | Library | Ext | Groups | RowRefs | Cleanup kind |
| --- | --- | --- | ---: | ---: | --- |
| <redacted> | production-library-a | `.cbz` | 10 | 30 | same series / same volume |
| <redacted> | production-library-b | `.zip` | 13 | 45 | same series / same volume |
| <redacted> | production-library-c | `.epub` | 97 | 194 | cross series |
| <redacted> | production-library-c | `.txt` | 56 | 112 | cross series |
| <redacted> | production-library-d | `.zip` | 3 | 6 | same series / same volume |

해석:

- `0.9.0.2-5` 적용 후 우선 확인할 자동 cleanup 대상은 same-series/same-volume duplicate입니다.
- cross-series duplicate는 자료 구조나 분류 의도일 수 있으므로 자동 삭제 검증 대상으로 보지 않습니다.
- `Pages=0` 중 nested archive 중심 CBZ는 파일 구조 문제일 가능성이 크고, 직접 이미지가 있는 ZIP 잔여는 재스캔으로 회복 가능한지 확인해야 합니다.

전후 비교용 JSON baseline:

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --compose-file compose/docker-compose.production.yml \
  --scan-log /your/kavita/config/logs/kavita20260531.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label before \
  --snapshot-db \
  --check-archives
```

preflight 결과에는 사람이 읽는 진단 텍스트, JSON baseline, DB 크기/mtime/host architecture/Docker engine manifest, compose 사본이 포함됩니다. `--snapshot-db`를 넣으면 live DB를 직접 오래 열지 않고 output directory의 SQLite backup copy를 분석합니다. 같은 label을 다시 써도 이전 snapshot sidecar를 정리하고 임시 파일에 백업한 뒤 성공 시 교체합니다. 기본 snapshot timeout은 120초이며 느린 스토리지에서는 `KAVITA_PREFLIGHT_SNAPSHOT_TIMEOUT_SECONDS=300`처럼 조절합니다. JSON에는 `integrity_check`, `foreign_key_check`, `core_table_counts`, `ef_migration_summary`, `manual_migration_summary`, `server_settings`, `pages0_by_library_ext`, `duplicate_file_paths_by_library_ext`, `duplicate_cleanup_candidates`가 들어갑니다. `--check-archives`를 넣으면 `pages0_archive_validation`도 JSON에 포함되어 직접 이미지가 있는 복구 가능 archive와 nested archive를 분리할 수 있습니다. `--check-covers`를 넣으면 DB/config 기준 `cover_source_cache_risk`와 `txt_cover_state`도 JSON에 포함되어 cover cache 보존과 TXT fallback cover 효과를 비교할 수 있습니다. source `cover.*`와 `kavita.yaml` cover hint까지 직접 확인하려면 `--check-covers --check-cover-source-files`를 별도 실행합니다. 이 원본 probe는 rclone mount에서 오래 걸릴 수 있으므로 일반 postflight에는 기본으로 넣지 않습니다. `--scan-log`를 넣으면 library scan 시간, file discovery 시간, series update 시간, slow reader request, reader latency와 DB/cache 상태의 상관분석을 별도 summary로 남깁니다.

운영 적용 후에는 같은 DB를 현재값으로 읽고 before JSON과 비교합니다.

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
  --compare-json /tmp/kavita-gds-preflight/before-diagnostics.json \
  --compare-scan-json /tmp/kavita-gds-preflight/before-scan-log-summary.json \
  --postflight-gates
```

postflight gate는 다음 기준으로 봅니다.

- `FAIL`: SQLite integrity/FK 위반, `Pages=0` 증가, 복구 가능 `Pages=0` archive 증가, same-series duplicate 증가, cross-series duplicate 증가, MediaError 증가
- `WARN`: `Pages=0`, 복구 가능 `Pages=0` archive, same-series duplicate가 줄지 않고 그대로 남음
- `PASS`: 정합성 위반이 없고, 회복 대상이 줄었거나 최소한 증가하지 않음
- cover gate는 `--check-covers`가 before/after 양쪽에 있을 때 DB/config 기준으로 판정합니다. 원본 cover/YAML hint까지 포함한 TXT missing-cover debt는 `--check-cover-source-files`를 같이 넣은 별도 느린 검사에서만 판정합니다.

## 빠른 cover baseline

2026-05-31 18:57 기준, 운영 컨테이너를 변경하지 않고 `before-kavita.db` snapshot으로 빠른 cover gate를 확인했습니다.

명령 요지:

```bash
scripts/collect_gds_preflight.sh \
  --db /tmp/kavita-gds-preflight/before-kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --cache-dir /your/kavita/config/cache \
  --output-dir /tmp/kavita-gds-preflight \
  --label before-covers-fast \
  --check-covers
```

결과:

- `source_cover_probe`: `False`
- GDS config cover reference: `4,423`
- TXT config cover series: `3,650`
- production-library-e TXT series는 `2,061`개가 config cover 없이 남아 있습니다.
- 웹소설 단행 TXT series는 `4`개가 config cover 없이 남아 있습니다.
- 같은 baseline self-check에서 cover 관련 `FAIL`은 없었습니다.

이 값은 운영 전환 후 TXT fallback cover와 cover cache 보존을 비교할 기준입니다.

## 19:03 read-only 재확인

운영 컨테이너를 변경하지 않고 현재 runtime/DB/log 상태를 다시 확인했습니다.

현재 운영 상태:

- 실행 이미지: `local/kavita-gds:0.9.0.2-1`
- compose 이미지: `local/kavita-gds:0.9.0.2-1`
- 컨테이너 상태: healthy
- DB `PRAGMA integrity_check`: `ok`
- DB `PRAGMA foreign_key_check`: 위반 없음

현재 snapshot/postflight:

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --scan-log /your/kavita/config/logs/kavita20260531.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label current-readonly \
  --snapshot-db \
  --check-archives \
  --compare-json /tmp/kavita-gds-preflight/before-diagnostics.json \
  --compare-scan-json /tmp/kavita-gds-preflight/before-scan-log-summary.json \
  --postflight-gates
```

결과:

- `current-readonly-diagnostics.json` 생성
- `Pages=0`: 49개로 baseline과 동일
- 복구 가능 ZIP `Pages=0`: 39개로 baseline과 동일
- same-series duplicate cleanup 후보: 26개 group으로 baseline과 동일
- cross-series duplicate: 증가 없음
- MediaError: 637개로 baseline과 동일

빠른 cover gate:

```bash
scripts/collect_gds_preflight.sh \
  --db /tmp/kavita-gds-preflight/current-readonly-kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --cache-dir /your/kavita/config/cache \
  --output-dir /tmp/kavita-gds-preflight \
  --label current-covers-fast \
  --check-covers \
  --compare-json /tmp/kavita-gds-preflight/before-covers-fast-diagnostics.json \
  --postflight-gates
```

결과:

- GDS config cover reference: `4,423`개로 감소 없음
- TXT config cover series: `3,650`개로 감소 없음
- source cover/YAML hint 기반 missing-cover debt는 `--check-cover-source-files`를 실행하지 않아 skip

로그 해석:

- `2026-05-31` 로그에서 3초 이상 slow request는 21개였습니다.
- reader latency 상관분석에서 DB와 매칭된 slow reader request 18개 중 17개는 ZIP이었고, 대부분 100MB 이상 chapter였습니다.
- 스캔 병목은 force scan의 file discovery/rclone listing 비용과 old runtime의 일부 일반 scan series update 비용으로 분리됩니다.
- report 폴더의 외부 제보는 `0.9.0.2-4` Web UI dev bundle이 `localhost:5000`을 호출한 문제로 확인했습니다.

운영 결론:

- 현재 운영 DB는 read-only 기준 정합성 위반이 없습니다.
- 현재 운영이 `0.9.0.2-1`이므로 최신 공개 이미지의 회복 효과는 아직 운영 DB에서 증명되지 않았습니다.
- 운영 목표 완료를 위해서는 `0.9.0.2-5` 전환, 재스캔, postflight 비교가 남아 있습니다.

## 19:07 최신 이미지 별도 스캔 smoke

운영 컨테이너를 변경하지 않고 `0.9.0.2-5` 이미지를 운영 DB snapshot과 임시 config로 별도 기동했습니다.

검증 구성:

- 컨테이너: `kavita-gds-scan-smoke-0902-5`
- 이미지: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`
- 포트: `5017:5000`
- config: LXC `/tmp/kavita-scan-smoke-0902-5-config`
- media: `/your/gds/mount:/mnt/gds:ro`

확인 결과:

- `/api/health`: `Ok`
- startup migration 완료
- restart count `0`
- 현재 운영 DB snapshot에서는 startup FK 실패가 재현되지 않음

이후 텍스트 중심 라이브러리 force scan을 테스트 DB에만 요청했습니다. API 요청은 정상 enqueue됐지만, 로그는 file scan 시작 이후 추가 진행이 없었습니다.

```text
Beginning file scan on production-library-e
Warning! production-library-e has metadata turned off
```

관찰:

- 컨테이너는 healthy 상태를 유지했습니다.
- worker thread 하나가 FUSE request 대기 상태로 보였습니다.
- rclone service는 active였고, rclone 로그상 upload/delete/rename 징후는 없었습니다.
- 테스트 컨테이너와 임시 config/DB는 부하를 남기지 않도록 제거했습니다.

해석:

- `0.9.0.2-5` startup은 현재 DB snapshot에서 정상입니다.
- 대형 텍스트 라이브러리 force scan은 최신 이미지에서도 cold rclone/listing 조건에서는 file discovery가 병목이 될 수 있습니다.
- 운영 전환 후 검증은 전체/대형 force scan부터 시작하지 말고, 작은 라이브러리나 특정 series scan부터 진행해야 합니다.
- scanner 성능 판정은 `file discovery`와 `series update`를 분리해서 봐야 합니다.

## 19:12 file discovery 직접 측정

Kavita를 거치지 않고 PVE host의 rclone mount에서 텍스트 라이브러리 경로를 직접 순회했습니다. 새 도구는 기본적으로 경로명을 안정 해시로 숨기고, top-level child별 traversal 시간을 JSON line으로 출력합니다.

명령:

```bash
scripts/profile_gds_tree.py /your/gds/mount/<redacted-media-path>
  --time-limit 120
```

확인 결과:

- `--max-depth 1`로 얕게 보면 12개 top-level, 579개 하위 directory가 약 6.5ms에 끝났습니다.
- 전체 깊이에서는 첫 번째 top-level child가 약 13ms에 끝났습니다.
- 두 번째 top-level child는 120초 time limit에 걸렸고, 그동안 `dirs 452`, `files 641`, `scandir_calls 247`까지만 처리했습니다.
- 해당 child를 다시 root로 잡아 측정하니, 내부 child 중 하나는 약 24초, 다른 하나는 약 96초 이상을 소비하며 다시 120초 time limit에 걸렸습니다.

운영 해석:

- 텍스트 라이브러리 force scan이 느린 핵심은 root listing이 아니라 특정 깊은 하위 트리의 반복 `scandir/stat`입니다.
- 많은 작은 TXT/YAML 파일과 하위 directory가 rclone/FUSE 왕복을 크게 만들고 있습니다.
- `0.9.0.2-5`의 scanner update 최적화는 series update 비용을 줄일 수 있지만, 대형 force scan의 rclone file discovery 비용 자체를 없애지는 못합니다.
- 운영 전환 후 검증 순서는 작은 범위 scan, 특정 series scan, no-change scan, 필요한 경우 작은 library force scan 순서가 안전합니다.

rclone 상태 확인:

```bash
systemctl cat rclone-gds.service
rclone rc core/stats --url http://127.0.0.1:5275
rclone rc vfs/stats --url http://127.0.0.1:5275
du -sh /your/rclone/cache
```

확인 결과:

- mount 옵션은 `--read-only`, `--dir-cache-time=1000h`, `--poll-interval=0`, `--vfs-cache-mode=full`입니다.
- 반복 측정 전 RC `core/stats`: `listed 201,862`, `errors 0`, `deletes 0`, `renames 0`, `serverSideMoves 0`
- 반복 측정 전 RC `vfs/stats`: metadata cache `dirs 40,708`, `files 145,847`
- 반복 측정 전 VFS disk cache: 581 files, 약 4.7GB
- host cache directory: 약 4.3GB

같은 full-depth traversal을 다시 돌린 결과:

- 이전에 120초 timeout에 걸렸던 두 번째 top-level child는 약 39초에 완료됐습니다.
- 대신 다음 cold top-level child가 약 81초를 소비한 뒤 전체 120초 time limit에 도달했습니다.
- 반복 측정 후 RC `core/stats`: `listed 203,425`, `errors 0`, `deletes 0`, `renames 0`, `serverSideMoves 0`
- 반복 측정 후 RC `vfs/stats`: metadata cache `dirs 40,900`, `files 146,546`
- 반복 측정 후 VFS disk cache: 586 files, 약 5.5GB

해석:

- GDS 원본 쓰기나 rename/delete 위험은 현재 관찰되지 않았습니다.
- dir cache TTL이 짧아서 반복 listing이 발생하는 구조도 아닙니다.
- 문제는 깊은 하위 tree를 처음 강제 순회할 때 많은 작은 file/directory metadata를 rclone/FUSE가 채워야 하는 비용입니다.
- metadata warming은 실제로 일부 subtree 시간을 줄였지만, 아직 cache가 없는 다음 subtree로 병목이 이동했습니다.
- 따라서 운영 전환 직후 전체 텍스트 라이브러리 force scan을 성공/실패 기준으로 삼으면 scanner 개선 효과를 제대로 분리하기 어렵습니다.
- 필요한 경우 `profile_gds_tree.py` 또는 rclone RC refresh로 subtree 단위 warming을 계획하되, 이는 Google Drive listing 부하를 만들 수 있으므로 운영 스캔과 별도 단계로 다뤄야 합니다.

## report 폴더 제보 해석 보정

`/your/kavita/report`에 받은 외부 제보의 compose 들여쓰기는 Discord 전달 과정에서 깨졌을 가능성이 큽니다. 따라서 해당 제보의 핵심은 YAML 문법 문제가 아니라, `0.9.0.2-4` 이미지에서 Web UI가 production bundle이 아니라 개발 bundle로 들어가 외부 브라우저가 `localhost:5000/api`를 호출하던 증상입니다.

이후 제보 환경은 Oracle A1이 아니라 Proxmox 위 Ubuntu 환경으로 정정되었습니다. 따라서 이 사례는 ARM64 전용 문제로 보기 어렵고, `0.9.0.2-4` Web UI bundle 문제 또는 기존 DB/config volume 전환 상태를 우선 확인해야 합니다.

현재 정리:

- `0.9.0.2-4` Web UI bundle 문제는 `0.9.0.2-5`에서 수정했습니다.
- compose 문법 자체가 원인인지 판단하려면 원본 compose 파일이나 `docker compose config` 출력이 필요합니다.
- 제보에 포함된 UI 증상만 놓고 보면 `localhost:5000` 호출 문제가 우선 원인입니다.

운영 컨테이너 전환 후 확인:

- 실행 이미지: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`
- 상태: `running healthy`, restart count `0`
- `/kavita/wwwroot`의 실제 실행 JS/CSS/HTML에서 `localhost:5000`, `:5000/api`, Angular development mode 문자열: `0`건
- startup manual migration과 EF migration은 정상 완료
- DB `integrity_check`: `ok`
- DB `foreign_key_check`: 위반 없음

## 승인 후 운영 전환 절차

현재 운영 compose는 LXC 101의 `/opt/compose/kavita/docker-compose.yml`이고, 실행 중인 이미지는 `local/kavita-gds:0.9.0.2-1`입니다. 전환 대상 이미지는 LXC 101에 이미 받아져 있는 `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`입니다. 아래 절차는 운영 컨테이너를 재시작하므로 명시적으로 승인한 뒤에만 실행합니다.

전환 직전 백업:

```bash
ts=$(date +%Y%m%d-%H%M%S)
pct exec 101 -- cp /opt/compose/kavita/docker-compose.yml /opt/compose/kavita/docker-compose.yml.bak-$ts
sqlite3 -readonly /your/kavita/config/kavita.db ".backup '/your/kavita/config/kavita.db.pre-0902-5-$ts.bak'"
```

compose image tag 교체와 기동:

```bash
pct exec 101 -- sed -i.bak-0902-5 's#image: .*kavita-gds:.*#image: ghcr.io/suikano1304/kavita-gds:0.9.0.2-5#' /opt/compose/kavita/docker-compose.yml
pct exec 101 -- docker compose -f /opt/compose/kavita/docker-compose.yml up -d
pct exec 101 -- docker inspect kavita --format '{{.Config.Image}} {{.State.Status}} {{.State.Health.Status}} {{.RestartCount}}'
curl -fsS http://127.0.0.1:5657/api/health
```

기동 직후 로그 확인:

```bash
pct exec 101 -- docker logs --tail 160 kavita
sqlite3 -readonly /your/kavita/config/kavita.db 'PRAGMA integrity_check; PRAGMA foreign_key_check;'
```

전환 후 postflight:

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

롤백:

```bash
pct exec 101 -- sed -i.rollback-0902-5 's#image: .*kavita-gds:.*#image: local/kavita-gds:0.9.0.2-1#' /opt/compose/kavita/docker-compose.yml
pct exec 101 -- docker compose -f /opt/compose/kavita/docker-compose.yml up -d
pct exec 101 -- docker inspect kavita --format '{{.Config.Image}} {{.State.Status}} {{.State.Health.Status}} {{.RestartCount}}'
```

DB backup은 자동으로 되돌리지 않습니다. startup 직후 DB integrity/FK가 깨졌거나 migration 중단이 확인될 때만, 컨테이너를 멈춘 뒤 어떤 backup으로 되돌릴지 별도 판단합니다.

## 19:29 운영 전환 및 작은 라이브러리 검증

승인 후 운영 컨테이너를 `0.9.0.2-5`로 전환했습니다.

전환 전 백업:

- compose backup: `/opt/compose/kavita/docker-compose.yml.bak-20260531-192819`
- DB backup: `/your/kavita/config/kavita.db.pre-0902-5-20260531-192819.bak`
- appsettings backup: `/your/kavita/config/appsettings.json.pre-0902-5-20260531-192819.bak`

전환 결과:

- compose image: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`
- container image: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`
- 상태: `running healthy`, restart count `0`
- LXC 내부 `/api/health`: `Ok`
- startup manual migration/EF migration: 정상 완료
- SQLite integrity/FK: `ok`, 위반 없음

전환 직후 postflight:

- `Pages=0`: 49개로 증가 없음
- 복구 가능 archive `Pages=0`: 39개로 증가 없음
- same-series duplicate cleanup 후보: 26개로 전환 직후에는 변화 없음
- cross-series duplicate: 153개로 증가 없음
- MediaError: 637개로 증가 없음
- GDS config cover references: `4,423 -> 4,423`, 감소 없음
- TXT config cover series: `3,650 -> 3,650`, 감소 없음

작은 라이브러리 일반 스캔:

- 첫 일반 스캔은 기존 DB에 없던 실제 폴더 1개를 발견해 `1 Series / 4 files`를 처리했습니다.
- 같은 라이브러리를 다시 일반 스캔하자 `Found 0 Series`, `0 files`, 전체 약 `70 ms`로 끝났습니다.
- 이 결과는 변경 없음 최적화가 운영 이미지에서 정상 동작한다는 증거입니다.

작은 라이브러리 force scan:

- `Found 69 Series that need processing in 90,796 ms`
- 전체 결과: `134 files / 69 series / 92,147 ms`
- 대부분의 series update는 ms 단위였고, 총 시간 대부분은 `Found ...` 전 file discovery/YAML/rclone read 단계였습니다.
- force scan 중 rclone RC에서 여러 `kavita.yaml` 읽기와 VFS cache 증가가 관찰됐습니다.
- rclone `core/stats`: `errors 0`, `deletes 0`, `renames 0`, `serverSideMoves 0`

force scan 후 postflight:

- same-series duplicate cleanup 후보: `26 -> 13`
- 해당 작은 라이브러리의 duplicate file path: `13 groups / 45 row refs -> 0`
- cross-series duplicate: `153 -> 153`, 증가 없음
- MediaError: `637 -> 637`, 증가 없음
- GDS config cover references: `4,423 -> 4,424`, 감소 없음
- TXT config cover series: `3,650 -> 3,650`, 감소 없음
- DB integrity/FK: `ok`, 위반 없음

운영 해석:

- `0.9.0.2-5`는 운영 DB에서 startup FK 문제 없이 기동했습니다.
- 일반 재스캔의 변경 없음 최적화는 실제 운영에서 sub-second로 확인됐습니다.
- same-series duplicate cleanup은 작은 force scan에서 실제로 동작했습니다.
- 다만 force scan의 병목은 scanner update 단계가 아니라 file discovery와 YAML/rclone read 단계입니다.
- 남은 same-series duplicate는 아직 force scan이 닿지 않은 라이브러리의 scan debt이며, 작은 범위부터 순차적으로 처리하는 편이 안전합니다.

## 운영 체크리스트

## 추가 운영 검증: 중간/소형 라이브러리 재스캔과 `0.9.0.2-6`

`0.9.0.2-5` 운영 적용 후 작은 라이브러리부터 순차적으로 재스캔했습니다.

- 텍스트 중심 소형 라이브러리 일반 스캔은 `Found 0 Series`, 약 `91 ms`로 끝났습니다.
- 중간 규모 번역 라이브러리 일반 스캔은 실제 미등록 폴더를 추가 발견했고, 남은 `Pages=0` 10개와 same-series duplicate 10개는 모두 nested archive 계열로 남았습니다.
- production-library-d 라이브러리 일반 스캔 후 복구 가능 `Pages=0` archive는 `39 -> 0`, same-series duplicate는 `3 -> 0`으로 감소했습니다.
- 최종 postflight 기준 DB integrity/FK는 정상, MediaError는 `637 -> 637`, cross-series duplicate는 `153 -> 153`, GDS config cover reference는 `4,424 -> 4,424`로 악화가 없었습니다.
- rclone RC 기준 `errors 0`, `deletes 0`, `renames 0`, `serverSideMoves 0`입니다.

이 과정에서 scanner가 아니라 word-count analyzer의 별도 문제가 확인됐습니다. 대표 포맷이 EPUB인 혼합 포맷 시리즈에서 PDF/TXT 같은 비 EPUB 파일까지 EPUB 리더로 열어 `There was an issue counting words on an epub` 오류를 낼 수 있었습니다.

`0.9.0.2-6`에서 이 부분을 수정했습니다.

- 비 EPUB 파일은 EPUB word count 대상에서 제외합니다.
- 제외된 파일도 분석 시각을 갱신해 같은 오류가 반복되지 않게 했습니다.
- 회귀 테스트, `linux/amd64`/`linux/arm64` publish, multi-arch OCI build, `linux/amd64` startup smoke를 통과했습니다.
- GitHub Release: `v0.9.0.2-6`
- GHCR: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-6`, `latest`

`0.9.0.2-6` 운영 적용 뒤 신규 GDS archive 시리즈 일부에서 파일과 페이지 수는 정상 등록됐지만 `Series`, `Volume`, `Chapter` 커버 참조가 모두 비어 있는 사례를 확인했습니다.

원인은 `0.9.0.2-6`의 GDS 커버 생성 분기입니다. YAML/base64 커버 또는 TXT 제목 커버가 없을 때 일반 ZIP/CBZ 첫 페이지 커버 추출 경로로 fallback하지 않고 커버 처리를 종료했습니다.

`0.9.0.2-7`에서 이 부분을 수정했습니다.

- YAML/base64 커버가 있으면 기존처럼 우선 사용합니다.
- TXT 파일은 기존 제목 기반 fallback cover를 유지합니다.
- ZIP/CBZ archive는 YAML/TXT 커버가 없으면 기존 Kavita archive cover extractor로 fallback합니다.
- 회귀 테스트, `linux/amd64`/`linux/arm64` publish, multi-arch OCI build, `linux/amd64` startup smoke를 통과했습니다.
- GitHub Release: `v0.9.0.2-7`
- GHCR: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-7`, `latest`

`0.9.0.2-7` 운영 적용 뒤 기본 시리즈 정렬이 다시 제목 오름차순으로 보이는 문제가 확인됐습니다.

원인은 두 군데였습니다. Web UI의 기본 series sort field가 아직 `SortName`으로 남아 있었고, 필터 상태 복원 코드가 명시적인 내림차순 값(`false`)을 `|| true`로 다시 오름차순 처리했습니다. 백엔드의 null sort fallback도 `SortName asc`로 남아 있어 API 호출자가 sort option을 보내지 않으면 같은 문제가 재현될 수 있었습니다.

`0.9.0.2-8`에서 이 부분을 수정했습니다.

- Web UI 기본 series sort field를 `LastModified`로 변경했습니다.
- series 기본 정렬 방향을 내림차순으로 변경했습니다.
- 필터 상태 복원에서 `false` 값이 유지되도록 nullish coalescing을 사용했습니다.
- 백엔드 series/bookmark null sort fallback도 `LastModifiedDate desc`로 맞췄습니다.
- 운영 API에서 sort option 없이 조회했을 때 DB의 `Series.LastModified desc`와 같은 순서가 나오는 것을 확인했습니다.
- GitHub Release: `v0.9.0.2-8`
- GHCR: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-8`, `latest`

재배포 전:

```bash
pct exec 101 -- docker ps --filter name=kavita --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}'
pct exec 101 -- python3 - <<'PY'
import sqlite3
con = sqlite3.connect('file:/your/kavita/config/kavita.db?mode=ro', uri=True)
print(con.execute('pragma integrity_check').fetchone()[0])
PY
```

재스캔 전:

```bash
pct exec 101 -- cp /your/kavita/config/kavita.db /your/kavita/config/kavita.db.pre-scanfix-$(date +%Y%m%d)
```

검증:

- 컨테이너가 healthy인지 확인합니다.
- 접두가 붙은 중복 시리즈가 새로 생기지 않는지 확인합니다.
- `kavita.yaml`의 `Summary`가 DB에 들어왔는지 표본 검사합니다.
- 회차 제목이 `meta.Name`이 아니라 파일명 기반으로 표시되는지 표본 검사합니다.
- rclone/GDS 원본 경로에 업로드/삭제/rename이 없는지 host rclone log/RC 기준으로 확인합니다.
