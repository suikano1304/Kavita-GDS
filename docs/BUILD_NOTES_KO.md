# 0.9.1.4-6 빌드

하나의 패치 소스 `e0853e3a1`에서 Linux .NET 10.0.9 publish를 linux-x64/linux-arm64/linux-arm 순서로 수행했습니다. 해당 소스의 UI 변경분으로 이전에 한 번 빌드한 동일 UI 번들을 세 플랫폼에 사용합니다. 단일 Dockerfile/buildx 흐름으로 이미지를 조립하며 아키텍처별 소스 패치는 없습니다.

ARM은 QEMU 검증이며 네이티브 기기 검증을 의미하지 않습니다. 검증한 OCI digest를 보존하여 게시합니다. 최종 결과와 플랫폼별 digest는 [릴리스 노트](../RELEASE_NOTES.md)를 확인하세요.

ARMv7 QEMU 부하 검사는 독립된 .NET GC 프로그램에서도 재현되는 신호 복귀 오류 때문에 검사 컨테이너에만 `DOTNET_INTERNAL_ThreadSuspendInjection=0`을 설정했습니다. 기본 기동·health는 해당 설정 없이 확인했으며, 배포 이미지와 실제 운영 설정에는 추가하지 않았습니다.
