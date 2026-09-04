# Kavita-GDS 0.9.1.4-1

공식 Kavita `0.9.1.4` 기반으로 GDS 패치를 이식했습니다.

- 시리즈 설명의 문자 `\n`·`\r\n`, 실제 줄바꿈, YAML 여러 줄 문자열을 표시합니다. 기존 DB에 저장된 `\n`도 화면에서 처리하므로 표시 복구를 위한 전체 재스캔은 필요하지 않습니다.
- 큰 YAML의 커버 payload를 불필요하게 읽지 않는 제한된 메모리 파서를 유지했습니다.
- GDS/Book 라이브러리 생성·설정 저장의 공급자 검증 오류와 시간대 경계의 읽기 기록 집계 오류를 수정했습니다.
- 기존 GDS 라이브러리가 있는 DB에서 업그레이드 중 서버가 종료되는 공급자 기본값 마이그레이션 오류를 수정했습니다.
- 기존 GDS 스캔·페이지·표지·혼합 형식 처리와 게임패드/PageUp·PageDown 보정을 유지했습니다.

검증: 서비스 테스트 2,614개, DB 테스트 75개, 서버 테스트 85개 통과. 시리즈 설명 브라우저 검증과 EPUB/TXT/PDF/ZIP/CBZ 리더 API 회귀 검증, 운영 DB 복사본의 전체 서버 기동·마이그레이션·데이터 보존 검사를 통과했습니다.

배포 전 config/DB를 백업하세요. DB 마이그레이션 후 이전 버전으로 돌아갈 때는 이전 DB 백업도 함께 복원해야 합니다.

지원 플랫폼: `linux/amd64`, `linux/arm64`, `linux/arm/v7`. 세 플랫폼 모두 기존 합성 GDS DB의 마이그레이션과 `/api/health=Ok`를 확인했습니다. ARM은 QEMU 실행 검증입니다.

```bash
docker pull ghcr.io/suikano1304/kavita-gds:0.9.1.4-1
```

GHCR `0.9.1.4-1`과 `latest`는 같은 멀티아키텍처 이미지를 가리킵니다.

- Multi-arch: `sha256:26efe330dd48ba4578913a9f2c7acf41e326b2414804071deec03e3b6f1762cd`
- `linux/amd64`: `sha256:be4b386d151427cecd6a1ba9791dd685d4affe37f20cedfb53c3088098084f15`
- `linux/arm64`: `sha256:784d7dc9ef1067a432d3642e2f4ae03d8ec472ae7facca5d0411933d6200a25f`
- `linux/arm/v7`: `sha256:084a8b0bb363748f8ed3ad41076ad6247af86fc7bb69dc6365f0d4e9210921b5`

빌드 소스: [1d8611ffd](https://github.com/suikano1304/Kavita-GDS/commit/1d8611ffd5804d051d81e605bc0d37f0d5e603ee). GHCR가 기본 배포물이며 이번 릴리스에는 별도 오프라인 archive asset을 제공하지 않습니다.
