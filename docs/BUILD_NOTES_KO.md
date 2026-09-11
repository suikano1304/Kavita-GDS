# 0.9.1.4-3 빌드

빌드 소스는 `d4263af41ad908313d3ac5f865a981e000d91dc1`입니다. UI를 한 번 빌드하고 같은 소스의 linux-x64/linux-arm64/linux-arm 출력으로 하나의 Dockerfile/buildx 흐름에서 세 플랫폼을 조립했습니다. 검증한 OCI 인덱스를 digest 보존 방식으로 게시했습니다. 아키텍처별 소스 패치는 없습니다.

amd64·arm64·armv7 기동/SQLite/읽기 및 기존 DB 재기동 검사를 통과했습니다. 공개 이미지와 검증 범위는 [릴리스 노트](../RELEASE_NOTES.md)를 확인하세요.
