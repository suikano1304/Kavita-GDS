# Kavita GDS 사용 설명서

## 개요

Kavita GDS는 Kavita official `0.9.0.10` nightly source에 GDS/rclone scanfix를 포팅한 비공식 Docker 빌드입니다.

현재 릴리즈는 `9.0.10-2`입니다. GHCR의 `9.0.10-2`와 `latest` 태그는 `linux/amd64`, `linux/arm64`, `linux/arm/v7`를 포함합니다.

이 빌드는 GDS/rclone 원본 media mount를 읽기 전용으로 두고, Kavita config 경로 안에서만 DB, cache, cover를 관리하는 구성을 전제로 합니다.

## Docker 설치

권장 방식은 GHCR에서 직접 pull하는 것입니다.

```bash
docker pull ghcr.io/suikano1304/kavita-gds:9.0.10-2
```

운영에서는 `latest`보다 고정 버전 태그를 권장합니다.

```text
ghcr.io/suikano1304/kavita-gds:9.0.10-2
```

## Compose 설정

예시 파일은 [../compose/docker-compose.production.yml](../compose/docker-compose.production.yml)에 있습니다.

최소 구성:

```yaml
services:
  kavita:
    image: ghcr.io/suikano1304/kavita-gds:9.0.10-2
    container_name: kavita
    restart: always
    ports:
      - "5657:5000"
    volumes:
      - /your/kavita/config:/kavita/config
      - type: bind
        source: /your/gds/mount
        target: /mnt/gds
        read_only: true
        bind:
          propagation: rslave
    environment:
      TZ: Asia/Seoul
      WAIT_ANCHOR_DIRS: /mnt/gds/READING_ROOT
```

바꿔야 하는 값:

- `/your/kavita/config`: Kavita DB, config, cache, cover가 저장될 로컬 경로
- `/your/gds/mount`: rclone/FUSE/GDS media mount 경로
- `5657:5000`: 외부 접속 포트가 다르면 왼쪽 숫자만 변경
- `WAIT_ANCHOR_DIRS`: 컨테이너 시작 전 존재를 확인할 mount anchor 경로

실행:

```bash
docker compose -f compose/docker-compose.production.yml up -d
```

상태 확인:

```bash
docker ps --filter name=kavita
docker logs -f kavita
```

## 수동 다운로드

`9.0.10-2`의 기본 배포물은 GHCR multi-arch image입니다. Docker pull이 불가능한 폐쇄망 환경에서는 GHCR image를 별도 registry로 미러링하거나, 필요한 플랫폼의 OCI archive를 따로 생성해 반입하세요. 과거 릴리스의 GitHub tarball은 해당 릴리스 태그 기준 산출물입니다.

## 업그레이드 전 확인

운영 DB를 연결하기 전에는 반드시 config 경로를 백업하세요.

```bash
cp -a /your/kavita/config /your/kavita/config.backup.$(date +%Y%m%d-%H%M%S)
```

현재 컨테이너 image와 health 확인:

```bash
docker inspect kavita --format '{{.Config.Image}} {{.Image}} {{.State.Health.Status}}'
```

GDS/rclone 원본 mount는 compose에서 `read_only: true`로 유지하는 것을 권장합니다.

## GDS 라이브러리 설정

Kavita Web UI에서 GDS library path는 컨테이너 내부 경로 기준으로 추가합니다.

```text
/mnt/gds
```

또는 실제 작품이 들어 있는 하위 경로를 library root로 지정합니다.

GDS sidecar metadata:

- `kavita.yaml`
- `kavita.yml`
- `cover.jpg`
- `cover.png`
- `cover.webp`

이 파일들은 media 파일로 등록되지 않고 metadata/cover 후보로만 사용됩니다.

## TXT 커버 정책

TXT 파일은 내부 표지가 없으므로 다음 순서로 cover를 찾습니다.

1. 같은 폴더의 `cover.jpg`, `cover.png`, `cover.webp`
2. `kavita.yaml`/`kavita.yml`의 base64 cover
3. 제목 기반 fallback cover

fallback cover는 외부 API나 외부 이미지 다운로드를 쓰지 않습니다. Kavita config의 cover 저장소에만 생성하며 GDS/rclone 원본 mount에는 쓰지 않습니다.

`kavita.yaml`의 `cover: TEXT`는 이미지가 아니라 텍스트 자료 표식으로 취급합니다.

## EPUB `1/1` 확인

`9.0.10-2`는 다음 EPUB/WebUI/웹소설 metadata 문제를 완화합니다.

- WebUI metadata filter에서 smart filter 이름 없이 정렬/필터 기본값을 저장할 수 없던 문제
- WebUI 우측 JumpBar/list 클릭이 현재 정렬 기준에서 실제 항목으로 스크롤되지 않던 문제
- 웹소설 파일명 completion marker가 metadata의 완료/종료 상태에 반영되지 않던 문제
- 같은 chapter에 broken/empty EPUB row와 valid EPUB row가 함께 있을 때 reader/cache가 valid EPUB row를 선택하지 못하던 문제
- scanner가 GDS EPUB/PDF/TXT 신규 파일을 무조건 `Pages=1`로 저장하던 문제
- 단일 XHTML 안에 여러 TOC anchor가 있는 EPUB
- duplicate manifest id/href가 있는 EPUB
- reader 진입 시 page-count가 잘못 남아 있는 GDS EPUB

단, EPUB 안에 실제 본문 XHTML이 없고 `cover.xhtml`, `cover.jpg`, `toc.ncx`만 있는 파일은 Kavita가 복구할 수 없습니다. 이 경우 `1/1`은 원본 EPUB 구조 문제입니다.

## 읽기 전용 진단

운영 DB를 수정하지 않고 상태를 집계하려면 `scripts/diagnose_kavita_gds.py`를 사용합니다.

```bash
python3 scripts/diagnose_kavita_gds.py \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --check-archives \
  --check-covers
```

live DB에서는 직접 오래 열기보다 preflight 스크립트의 `--snapshot-db` 사용을 권장합니다.

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --compose-file compose/docker-compose.production.yml \
  --scan-log /your/kavita/config/logs/kavitaYYYYMMDD.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label before \
  --snapshot-db \
  --check-archives \
  --check-covers
```

재스캔 후 postflight:

```bash
scripts/collect_gds_preflight.sh \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --scan-log /your/kavita/config/logs/kavitaYYYYMMDD.log \
  --output-dir /tmp/kavita-gds-preflight \
  --label after \
  --snapshot-db \
  --check-archives \
  --check-covers \
  --compare-json /tmp/kavita-gds-preflight/before-diagnostics.json \
  --compare-scan-json /tmp/kavita-gds-preflight/before-scan-log-summary.json \
  --postflight-gates
```

확인 항목:

- SQLite integrity/FK
- library별 `Pages=0`
- duplicate file path
- MediaError 분포와 원인 분류
- GDS config cover reference
- TXT fallback cover 상태
- scan log timing과 반복 scan churn
- slow reader request와 DB/cache 상태

## 스캔 로그 분석

스캔 로그 요약:

```bash
python3 scripts/summarize_kavita_scan_logs.py \
  /your/kavita/config/logs/kavitaYYYYMMDD.log
```

느린 reader 요청 분석:

```bash
python3 scripts/analyze_kavita_reader_latency.py \
  /your/kavita/config/logs/kavitaYYYYMMDD.log \
  --db /your/kavita/config/kavita.db \
  --cache-dir /your/kavita/config/cache \
  --slow-request-ms 3000
```

기본 출력은 library/series title/path/raw id를 숨기는 방향으로 설계되어 있습니다. 로컬에서 상세 확인이 필요할 때만 각 스크립트의 `--show-*` 옵션을 사용하세요.

## 로그인 화면에서 `localhost:5000`으로 요청하는 경우

브라우저 콘솔에 `localhost:5000/api/... ERR_CONNECTION_REFUSED`가 보이면 오래된 `0.9.0.2-4` 이미지의 Web UI development bundle 문제일 가능성이 큽니다.

`0.9.0.2-5` 이후와 `9.0.10-2` 이미지는 production UI bundle을 포함합니다.

```bash
docker pull ghcr.io/suikano1304/kavita-gds:9.0.10-2
docker compose up -d
```

컨테이너 안의 `appsettings.json`에서 `Port: 5000`은 정상입니다. 외부 포트는 compose의 `ports`에서 `5657:5000`처럼 매핑합니다.

## SQLite FK 오류 확인

startup 중 `SQLite Error 19: 'FOREIGN KEY constraint failed'`가 발생하면 먼저 기존 DB를 백업한 뒤 읽기 전용 진단을 실행하세요.

```bash
python3 scripts/diagnose_kavita_gds.py \
  --db /path/to/kavita.db \
  --json-output /tmp/kavita-gds-diagnostics.json
```

간단히 SQLite만 확인할 수도 있습니다.

```bash
sqlite3 /path/to/kavita.db 'PRAGMA integrity_check;'
sqlite3 /path/to/kavita.db 'PRAGMA foreign_key_check;'
```

같은 DB가 x86/NAS에서는 정상이고 ARM 서버에서만 실패한다면, 이미지 아키텍처보다 기존 DB migration 상태, 이전 컨테이너 종료 상태, compose volume 연결, 같은 DB를 두 컨테이너가 동시에 잡는 상황을 먼저 확인하세요.

## 주의사항

- 공식 Kavita 이미지가 아닙니다.
- 기존 DB를 연결하기 전에는 backup을 권장합니다.
- GDS/rclone 원본 mount는 읽기 전용을 권장합니다.
- `linux/arm64`는 Windows Docker Desktop/WSL qemu smoke test에서 `/api/health` `Ok`를 확인했지만, native ARM 실서비스 검증은 별도 환경에서 다시 확인하는 것이 좋습니다.
- `linux/arm/v7`는 CT101 qemu smoke test에서 `/api/health` `Ok`를 확인했지만, native ARMv7 실서비스 검증은 별도 환경에서 다시 확인하는 것이 좋습니다.
- 큰 binary 파일은 git에 직접 commit하지 않고 GitHub Release asset으로만 배포합니다.
