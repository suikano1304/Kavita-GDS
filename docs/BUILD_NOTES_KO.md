# 0.9.1.4-1 빌드

공식 Kavita `v0.9.1.4`에 하나의 GDS 패치 묶음을 적용했습니다.

- Angular production UI는 한 번 빌드해 모든 아키텍처에 공유합니다.
- .NET self-contained 패키지: `linux-x64`, `linux-arm64`, `linux-arm`.
- 동일 Dockerfile과 buildx가 `TARGETPLATFORM`에 맞는 패키지를 선택합니다.
- 통합 이미지: `ghcr.io/suikano1304/kavita-gds:0.9.1.4-1`.

서비스 테스트 2,614개와 DB 테스트 75개, 서버 테스트 85개가 통과했습니다. 브라우저 설명 표시, 리더 API, 운영 DB 복사본 마이그레이션을 검증했습니다. 플랫폼별 실행 결과와 최종 digest는 [릴리스 노트](../RELEASE_NOTES.md)에 기록합니다.
