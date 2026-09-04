# Kavita-GDS 0.9.1.4-2

`scan-folder` API가 요청한 작품 경로를 잃어 전체 라이브러리를 스캔하던 문제를 수정했습니다.

- 상위 폴더를 공유하는 작품도 원래 요청 경로로 찾아 해당 작품만 스캔합니다.
- API 응답 전에 작품 조회와 스캔 예약을 완료합니다.
- `AbortOnNoSeriesMatch` 옵션과 기존 폴더 감시 동작을 유지합니다.
- 공식 Kavita `0.9.1.4` 기반이며, `0.9.1.4-1`의 시리즈 설명 줄바꿈 수정도 포함합니다. 기존 UI 번들을 재사용했습니다.

검증: 서비스 2,618개, DB 75개, 서버 87개 테스트 통과(기존 서비스 테스트 6개 제외). 실제 HTTP 폴더 스캔과 리더 API, 운영 DB 복사본의 기동·데이터 보존을 확인했습니다. amd64·arm64·armv7 모두 기존 합성 GDS DB로 기동 및 health 검증을 통과했습니다. ARM 실행은 QEMU 검증입니다.

```bash
docker pull ghcr.io/suikano1304/kavita-gds:0.9.1.4-2
```

GHCR 버전 태그와 `latest`는 같은 멀티아키텍처 이미지를 가리킵니다. 배포 전 config/DB를 백업하세요.

- Multi-arch: `sha256:3fdb4bcc997d1d6bdfd6ae96a3f922d46242bddf4ece1956b4994f4f161eedbb`
- `linux/amd64`: `sha256:f8c4db61f7d4649ba5f63e45caf0b2cf270cd0dbfef7d2cb810e5e4e8afe5851`
- `linux/arm64`: `sha256:ba3dd94b2907e31fb486650d5cb5fa107777af147aa328dc2321d88ad07c90c5`
- `linux/arm/v7`: `sha256:d579da0adf68308011173cb2378329575b8d0fbc0e56836caf92b3e93b42386b`

빌드 소스: [e28c60571](https://github.com/suikano1304/Kavita-GDS/commit/e28c6057189ec359cd3ce738066334306eae8260). GHCR가 기본 배포물이며 별도 오프라인 이미지 파일은 첨부하지 않습니다.
