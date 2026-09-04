# Kavita GDS

Google Drive/rclone 같은 원격 저장소에 큰 만화/책 라이브러리를 두고 쓰는 환경을 위한 Kavita 비공식 Docker 빌드입니다. official Kavita `0.9.1.4`를 기반으로 GDS 스캔, 표지, 페이지 수, reader/cache, 검색, 정렬 문제를 보정했습니다.

현재 릴리즈: `0.9.1.4-1`

## 빠른 시작

```bash
docker pull ghcr.io/suikano1304/kavita-gds:0.9.1.4-1
```

```yaml
services:
  kavita:
    image: ghcr.io/suikano1304/kavita-gds:0.9.1.4-1
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
- 웹소설 완결 처리: 파일명 completion marker와 range marker 인식을 보강하고, 한국어 `Ended` 표시를 `종료`로 바로잡았습니다.
- WebUI 이동: 우측 JumpBar/list 클릭이 현재 정렬 기준에서도 실제 항목으로 스크롤되도록 보정했고, 최근 수정순처럼 같은 날짜가 많은 목록에서도 점프 위치가 촘촘하게 잡히도록 보강했습니다.
- **GDS mtime 우회 (0.9.0.12-1)**: rclone FUSE 마운트에서 `--dir-cache-time`으로 인해 디렉터리 mtime이 고정되어 Library Scan이 신규 파일을 누락하던 문제를 해결했습니다. GDS 라이브러리는 항상 디렉터리를 전수 열거합니다.
- **한국어 검색 정규화 (0.9.0.12-2)**: 공백 제거와 유니코드 NFC 정규화를 적용해 분해형 한글 입력과 챕터/라이브러리 검색 매칭을 보강했습니다.
- **GDS scan fingerprint / 콘텐츠 수정일 (0.9.0.12-3)**: 폴더 mtime skip 없이 파일은 계속 열거하되, 변경 없는 시리즈는 fingerprint로 재처리를 건너뜁니다. WebUI "마지막 수정" 정렬과 JumpBar 날짜는 DB 수정 시간이 아니라 실제 콘텐츠 파일 timestamp를 사용합니다.
- **GDS scan memory / 최신성 정렬 (0.9.0.12-4)**: 처리 단계에서 parser metadata 참조를 series 단위로 해제하고, "마지막 수정" 정렬에 신규 추가 시각을 포함합니다.
- **GDS broad scan hardening (0.9.0.12-5)**: scan phase 결과를 streaming grouping으로 처리하고, 대형 sidecar YAML, mixed-format fingerprint, 반복 재처리, post-scan CPU tail 문제를 보강했습니다.
- **GDS scan/cover memory 보강 (0.9.0.12-6)**: scan queue/index 참조를 줄이고, scan 종료 후 sidecar cache를 해제하며, 대표 cover와 권/챕터 cover 생성 경로의 메모리 사용을 보강했습니다.

OPDS: upstream #4759에서 정식 해결되어(단일엔트리+병합cbz) GDS 패치스택에 별도 OPDS 커스텀 없이 upstream 동작을 그대로 상속합니다.

## 이번 버전

- 시리즈 설명의 `\n`·`\r\n`, 실제 줄바꿈, YAML 여러 줄 문자열 표시를 복원했습니다. 기존 저장된 설명도 전체 재스캔 없이 표시됩니다.
- GDS/Book 라이브러리 생성·설정 저장과 시간대 경계의 읽기 기록 집계를 수정했습니다.
- 기존 GDS 기능과 게임패드/PageUp·PageDown 보정을 유지했습니다.

서비스 테스트 2,614개, DB 테스트 75개, 서버 테스트 85개, 설명 표시 브라우저 테스트와 리더 API 회귀 검증을 통과했습니다. 플랫폼별 실행 결과와 digest는 [RELEASE_NOTES.md](RELEASE_NOTES.md)를 참고하세요.

## 태그와 플랫폼

운영에서는 고정 버전 태그를 권장합니다.

```text
ghcr.io/suikano1304/kavita-gds:0.9.1.4-1
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

