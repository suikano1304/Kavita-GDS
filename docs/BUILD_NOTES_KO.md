# 0.9.1.4-4 빌드

하나의 패치 소스(`eb0078229`)에서 UI를 한 번 빌드하고 linux-x64/linux-arm64/linux-arm 백엔드를 순차 publish했습니다. 동일 Dockerfile/buildx 흐름으로 amd64·arm64·armv7 OCI 이미지를 만들었습니다. 아키텍처별 소스 패치는 없습니다.

빌드와 실행 검증은 Linux/PVE 내부에서 수행했습니다. ARM은 QEMU 10.2.1 에뮬레이션이며, 통합 이미지 게시 전 각 플랫폼의 새 DB 초기화·핵심 읽기·프로필 보존·기존 DB 재기동을 확인했습니다. 검증한 OCI digest를 보존하여 게시합니다.

이미지 digest와 최종 검증 범위는 [릴리스 노트](../RELEASE_NOTES.md)를 확인하세요.
