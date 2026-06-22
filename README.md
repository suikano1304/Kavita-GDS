# Kavita GDS

Google Drive/rclone 같은 원격 저장소에 큰 만화/책 라이브러리를 두고 쓰는 환경을 위한 Kavita 비공식 Docker 빌드입니다. official Kavita `0.9.0.10` nightly를 기반으로 GDS 스캔, 표지, 페이지 수, reader/cache 문제를 보정했습니다.

현재 릴리즈: `9.0.10-1`

## 빠른 시작

```bash
docker pull ghcr.io/suikano1304/kavita-gds:9.0.10-1
```

```yaml
services:
  kavita:
    image: ghcr.io/suikano1304/kavita-gds:9.0.10-1
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

`/your/kavita/config`, `/your/gds/mount`, `WAIT_ANCHOR_DIRS`는 본인 환경에 맞게 바꾸세요. 원본 media mount는 읽기 전용으로 연결하는 것을 권장합니다. 전체 compose 예시는 [compose/docker-compose.production.yml](compose/docker-compose.production.yml)에 있습니다.

## 이런 경우에 사용하세요

- Google Drive, rclone, FUSE mount 같은 원격 media 경로를 Kavita에 연결합니다.
- ZIP/CBZ, EPUB, PDF, TXT가 한 라이브러리 안에 섞여 있습니다.
- 큰 라이브러리 스캔 중 멈춤, 메모리 부족, 반복 재스캔, 잘못된 페이지 수 문제가 있었습니다.
- `kavita.yaml`/`kavita.yml`, folder cover, TXT/EPUB cover fallback을 사용합니다.

일반 로컬 디스크 기반 Kavita만 쓰는 경우에는 official Kavita 이미지를 먼저 권장합니다.

## 주요 수정

- 필터 저장: 스마트 필터 이름 없이도 현재 정렬/필터를 기본값으로 저장합니다.
- 읽기 안정화: 깨진 EPUB 정보와 정상 EPUB 정보가 함께 있을 때 읽을 수 있는 파일을 우선 선택합니다.
- 스캔 안정화: 특정 시리즈 스캔이 큰 상위 폴더까지 번지는 일을 줄이고, 대형 GDS/rclone 라이브러리의 메모리 사용량을 낮췄습니다.
- 페이지/표지 보정: EPUB, TXT, PDF, ZIP/CBZ의 페이지 수, 표지 선택, 한글 TXT 표지 fallback 문제를 줄였습니다.
- 운영 진단: runtime image에 `sqlite3`와 읽기 전용 진단 스크립트를 포함했습니다.

OPDS 실험 패치는 이번 릴리즈에서 제외했습니다. 기존 OPDS 기능은 제거하지 않았고, 새 OPDS 호환성 변경만 원복했습니다.

## 검증

배포 전 다음을 확인했습니다.

- `linux/amd64` 이미지 시작 및 `/api/health=Ok`
- `linux/arm64` 이미지 Windows Docker Desktop/WSL qemu smoke 및 `/api/health=Ok`
- `linux/arm/v7` 이미지 CT101 qemu smoke 및 `/api/health=Ok`
- Docker health `healthy`
- reader/cache 회귀 테스트 통과
- GHCR `9.0.10-1`와 `latest`가 같은 multi-arch 이미지로 발행됨
- 운영 컨테이너는 아직 자동으로 교체하지 않았습니다. production rollout은 별도 절차로 진행하세요.

상세 변경 내역과 digest는 [RELEASE_NOTES.md](RELEASE_NOTES.md), [docs/CHANGELOG_KO.md](docs/CHANGELOG_KO.md), [docs/GDS_0.9.0.10_VALIDATION.md](docs/GDS_0.9.0.10_VALIDATION.md)를 참고하세요.

## 태그와 플랫폼

운영에서는 고정 버전 태그를 권장합니다.

```text
ghcr.io/suikano1304/kavita-gds:9.0.10-1
```

지원 플랫폼: `linux/amd64`, `linux/arm64`, `linux/arm/v7`

## 업그레이드 주의

기존 Kavita DB를 연결하기 전에는 config 디렉터리와 DB를 백업하세요.

```bash
cp -a /your/kavita/config /your/kavita/config.backup
```

적용 후에는 다음을 확인하세요.

```bash
curl http://127.0.0.1:5657/api/health
docker ps --filter name=kavita
```

이 이미지는 official Kavita 이미지가 아닙니다. ARM 이미지는 qemu smoke test를 통과했지만, ARM 실서비스 환경에서는 적용 후 별도 확인을 권장합니다.
