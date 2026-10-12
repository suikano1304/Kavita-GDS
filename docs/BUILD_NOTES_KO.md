# 0.9.1.13-1 빌드

공식 nightly `0.9.1.13` commit `3233c26dd5b67d1b76db74c3ee96260f833a1cf1` 위에 GDS patch stack을 이식한 단일 소스 `0bc753390`에서 빌드했습니다. Node 24 production UI bundle 하나와 .NET 10 RID `linux-x64`, `linux-arm64`, `linux-arm` publish 결과를 단일 Dockerfile/buildx 흐름으로 조립했습니다. 아키텍처별 소스 패치는 없습니다.

source, build, cache, OCI archive와 검증 증거는 PVE `datapool`의 `/mnt/data/workspace/kavita-gds`에 두었습니다. BuildKit state도 datapool bind volume을 사용하며 검증 후 builder를 중지합니다.

ARM은 QEMU 검증이며 네이티브 기기 검증을 의미하지 않습니다. 검증한 OCI digest를 보존하여 게시합니다. 최종 결과와 플랫폼별 digest는 [릴리스 노트](../RELEASE_NOTES.md)를 확인하세요.

세 플랫폼은 최종 OCI manifest의 각 이미지를 QEMU 또는 native amd64로 기동해 `/api/health`를 확인했습니다. ARM 검증은 실제 ARM 기기 검증을 의미하지 않습니다.
