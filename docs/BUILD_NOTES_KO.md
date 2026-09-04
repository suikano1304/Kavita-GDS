# 0.9.1.4-2 빌드

공식 Kavita `v0.9.1.4` 기반의 단일 GDS 소스와 Dockerfile을 사용했습니다.

- 검증된 `0.9.1.4-1` Angular UI 번들을 세 아키텍처에 공통 사용합니다.
- .NET self-contained 패키지: `linux-x64`, `linux-arm64`, `linux-arm`.
- buildx가 `TARGETPLATFORM`에 맞는 패키지를 선택합니다.
- 서비스 2,618개·DB 75개·서버 87개 테스트와 실제 HTTP 작품 스캔, 리더 API, 기존 DB 기동을 검증했습니다.
- amd64·arm64·armv7 실행 검증 후 동일 OCI 인덱스를 GHCR에 게시했습니다.

최종 소스와 이미지 digest는 [릴리스 노트](../RELEASE_NOTES.md)에 기록합니다.
