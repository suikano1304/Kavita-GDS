# Kavita-GDS 스캐너 근본 원인 분석

작성일: 2026-05-31

## 현재 결론

Kavita-GDS의 문제는 단일 버그라기보다, 기존 Kavita 스캐너가 전제하던 모델과 GDS 자료 구조가 맞지 않으면서 생긴 구조적 문제다.

- Kavita 기본 스캐너는 대체로 `시리즈 = 하나의 폴더`, `시리즈 = 하나의 주 포맷`, `파일 변경 = 폴더 mtime 변경`을 전제로 한다.
- GDS 자료는 같은 시리즈 안에 `zip/cbz/pdf/epub/txt/image`가 섞일 수 있고, 파일이 포맷별 하위 폴더에 있거나, `kavita.yaml` 같은 폴더 메타데이터가 별도로 존재한다.
- 이 차이 때문에 스캔 변경 감지, 시리즈 병합, 볼륨/챕터 갱신, 페이지 수 계산, 커버/메타데이터 반영이 서로 다른 방향으로 어긋난다.

## 해결 상태 요약

| 영역 | 결론 | 현재 상태 | 남은 검증 |
| --- | --- | --- | --- |
| 변경 감지 과전파 | scan batch 전체의 changed 상태가 개별 시리즈로 번져 불필요한 재처리가 발생했다. | source와 release `0.9.0.2-5`에 보정 포함. 테스트 재스캔에서 `0 Series / 0 files / 770 ms`까지 감소 확인. | 운영 컨테이너가 아직 `0.9.0.2-1`이면 배포 후 큰 라이브러리 일반 재스캔에서 `Processing series`가 늘지 않는지 확인. |
| archive `Pages=0` | GDS 빠른 스캔이 archive 페이지 계산까지 생략해 신규/이동/기존 `Pages=0` 행이 남을 수 있었다. | 신규/변경 archive와 기존 `Pages=0` 재분석 트리거를 source와 release에 포함. | 직접 이미지가 있는 ZIP은 운영 재스캔 뒤 감소해야 한다. nested archive CBZ는 자동 회복 대상이 아니다. |
| YAML/로컬 메타데이터 | `EnableMetadata=false`와 별개로 GDS sidecar metadata를 읽어야 한다. | `kavita.yaml`/`kavita.yml` 안전 필드 반영, `meta.Name`의 회차명 덮어쓰기 방지 포함. | 신규/변경 샘플에서 회차명, 작가, 설명, 태그가 YAML 우선순위대로 유지되는지 표본 확인. |
| mixed-format reader | `LibraryType.GDS`가 기존 Kavita switch에서 빠지면 제목/reader 라우팅이 깨진다. | GDS를 Manga 제목 렌더링 및 chapter-info 경로에 포함. PDF/EPUB/TXT는 파일 format 기준 reader를 타도록 보정. | 전체 mixed-format 라이브러리 재스캔 후 PDF/EPUB/TXT/ZIP 샘플 열람 확인. |
| cover cache | GDS 원본 읽기 전용과 Kavita config cover cache 보존은 별개 문제다. | source `cover.*`가 없을 때 기존 config cache 삭제 방지, TXT title-cover fallback 포함. | 운영 적용 후 `--check-covers` 전후 비교로 cache가 줄지 않고 fallback cover가 config에만 생성되는지 확인. |
| TXT no-cover | TXT 내부에는 추출할 cover가 없으므로 cover 없음 자체는 오류가 아니다. | `cover: TEXT`/URL을 이미지로 오인하지 않고, 원본 cover가 없으면 deterministic title-cover 생성. | 텍스트 중심 라이브러리 재스캔 후 blank cover와 MediaError 증가가 없는지 확인. |
| duplicate file path | 같은 파일 경로가 여러 chapter에 남아 있으면 기존 cleanup이 임의 중복을 보존할 수 있었다. | same-series/same-volume duplicate cleanup은 `0.9.0.2-5`에 포함. cross-series duplicate는 자동 삭제 제외. | 운영 재스캔 후 same-series/same-volume group 감소 확인. cross-series group은 자료 구조 확인 후 수동 정책 필요. |
| startup FK 오류 | startup migration 실패가 BaseUrl 저장 FK 오류처럼 보일 수 있었다. | migration/BaseUrl 저장 scope 분리, migration rethrow, `foreign_key_check` 로그 포함. 운영 DB 사본 smoke test 정상. 진단 JSON에 EF/manual migration history, server setting, 핵심 row count 포함. | Oracle A1 실패 DB에서 `integrity_check`, `foreign_key_check`, migration history, compose volume 연결, 기존 컨테이너 종료 상태 확인. |
| reader request 지연 | 스캔이 아닌 `/api/reader/image` 첫 접근에서 archive cache 추출/rclone cold read가 발생할 수 있다. | 로그 요약 도구가 slow reader request를 scanner timing과 분리 집계. | cache miss, 큰 ZIP, rclone read latency를 scanner 병목과 별도로 확인. |
| MediaError | DB의 MediaError는 대부분 EPUB 내부 구조/manifest 문제 또는 PDF metadata/encryption 문제다. | 진단 스크립트가 원인 범주별로 분류하도록 확장. | 실제 파일 수정/변환 대상과 Kavita parser 한계를 분리해 처리. |
| source/release 일치 | 운영 hotfix와 release source가 갈라지면 분석 근거가 깨진다. | `0.9.0.2-5` 후보는 `0.9.0.2-3` 이후 source 보강까지 포함해 다시 빌드했다. | 이후 변경은 source branch, release tarball, image를 같은 commit에서 다시 생성. |

현재 가장 큰 미완료 항목은 운영 적용 후 postflight 검증을 수행하는 것이다. 특히 운영 컨테이너가 `0.9.0.2-1`인 동안에는 `0.9.0.2-5`에 포함된 duplicate cleanup, startup FK 진단 강화, 일부 scan debt 회복 동작이 실제 운영 DB에는 아직 반영되지 않을 수 있다.

## 확인한 운영 증거

운영 컨테이너:

- 컨테이너: `kavita`
- 이미지: `local/kavita-gds:0.9.0.2-1`
- 이미지 ID: `sha256:<redacted>`
- `/mnt/gds` 마운트: 읽기 전용

최근 production-library-d 스캔 로그:

- 강제 스캔: `3004 files / 297 series / 593313 ms`
- 직후 일반 스캔: `171 files / 297 series / 11950 ms`
- DB의 폴더 mtime 비교 기준으로는 production-library-d 297개 시리즈 모두 변경 후보 `0`
- 추가 패치 전 테스트 재스캔: `5 Series / 108 files / 약 7-10초`
- 추가 패치 후 테스트 재스캔: `0 Series / 0 files / 770 ms`

즉, 일반 스캔에서 처리된 7개 시리즈는 실제 소스 폴더 변경 때문이 아니라 스캐너 내부 변경 전파 로직 때문에 다시 처리된 것으로 보는 것이 맞다.

전체 DB 정합성 샘플:

| LibraryId | Series | Files | mtime newer | missing folder | duplicate file paths | Pages=0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 4240 | 40347 | 0 | 0 | 0 | 0 |
| 2 | 1888 | 14061 | 0 | 0 | 0 | 0 |
| 3 | 1144 | 9637 | 0 | 0 | 0 | 0 |
| 4 | 187 | 2261 | 0 | 0 | 10 | 10 |
| 5 | 68 | 162 | 0 | 0 | 13 | 0 |
| 6 | 80 | 564 | 0 | 0 | 0 | 0 |
| 7 | 2061 | 2805 | 0 | 0 | 0 | 0 |
| 8 | 13891 | 63588 | 0 | 0 | 153 | 0 |
| 9 | 297 | 3002 | 0 | 0 | 3 | 39 |

2026-05-31 재확인 결과:

- 현재 운영 DB의 `Pages=0` archive는 총 49개다.
- 39개 ZIP은 실제 파일이 존재하고, archive를 열 수 있으며, 내부 이미지가 총 13,301개 있었다. 즉 파일 자체 문제라기보다 기존 DB 행이 아직 회복되지 않은 상태다.
- 10개 CBZ는 실제 파일이 존재하고 archive 자체는 열리지만, 내부가 이미지가 아니라 ZIP 84개로 구성되어 있었다. Kavita의 일반 CBZ 페이지 카운터가 직접 이미지 파일만 페이지로 세는 구조와 맞지 않는다.
- duplicate file path는 179개 path group, 387개 row reference가 남아 있다. 이 중 일부는 같은 파일이 여러 chapter에 매핑된 경우이고, 일부는 같은 파일이 여러 series에 중복 매핑된 경우다.
- cover cache 위험군은 source `cover.*`는 없지만 `_s{id}.jpg/png/webp` cache 이름을 쓰는 GDS series 4,315개로 집계됐다. 이 값은 원본 손상이 아니라, cover 재생성 로직이 config cache를 다시 만들거나 지울 수 있는 범위를 뜻한다.
- duplicate path와 `Pages=0` 잔여 행에는 현재 progress/bookmark/reading list/toc/rating 참조가 붙어 있지 않다. 다만 cross-series duplicate가 많아 일괄 삭제는 아직 위험하다.

핵심 scanner 파일의 현재 git source와 release source tarball SHA256도 일치했다.

| File | SHA256 |
| --- | --- |
| `Kavita.Services/Scanner/ParseScannedFiles.cs` | `<redacted-hash>` |
| `Kavita.Services/Scanner/ProcessSeries.cs` | `<redacted-hash>` |
| `Kavita.Services/Helpers/GdsMetadataParser.cs` | `<redacted-hash>` |
| `Kavita.Services/Reading/ReadingItemService.cs` | `<redacted-hash>` |

읽기 전용 재현 도구:

```bash
python3 scripts/diagnose_kavita_gds.py \
  --db /your/kavita/config/kavita.db \
  --container-root /mnt/gds \
  --host-root /your/gds/mount \
  --check-archives \
  --check-covers
```

운영 중인 live DB는 WAL/SHM 대기로 직접 진단이 오래 멈출 수 있으므로, postflight 수집은 `collect_gds_preflight.sh --snapshot-db`를 우선 사용한다.

## 원인 1: 변경 상태가 시리즈 단위로 보존되지 않음

파일:

- `Kavita.Services/Scanner/ParseScannedFiles.cs`

현재 문제 코드:

- `CheckSurfaceFiles()`가 전달받은 `hasChanged`를 무시하고 항상 `CreateScanResult(..., true, files)`로 넣는다.
- `CreateFinalSeriesResults()`가 각 시리즈의 변경 여부를 계산할 때 `scanResults.Any(sr => sr.HasChanged)`를 사용한다.

영향:

- 한 폴더나 surface file 하나만 changed로 잡혀도 같은 scan batch 안의 다른 시리즈까지 changed로 전파된다.
- DB mtime상 변경이 없어도 일반 재스캔에서 일부 시리즈가 계속 처리된다.
- 대형 GDS 라이브러리에서는 이 작은 과다 처리도 rclone directory listing, 메타데이터 갱신, 커버 갱신과 겹쳐 체감 속도를 크게 늦춘다.

개선 방향:

- `CheckSurfaceFiles()`는 `hasChanged` 값을 그대로 사용해야 한다.
- `ScannedSeriesResult.HasChanged`는 전체 `scanResults.Any()`가 아니라 해당 시리즈의 `ParserInfo`를 포함한 `ScanResult`들만 대상으로 계산해야 한다.
- 더 안정적으로는 `ParsedSeries` 또는 `SeriesModified`에 `SeriesId`를 포함해 폴더명 충돌 없이 시리즈별 changed state를 전달해야 한다.

적용한 추가 보정:

- surface file 처리 시 전달받은 `hasChanged`를 유지한다.
- 최종 `ScannedSeriesResult.HasChanged`는 전체 scan batch가 아니라 해당 시리즈의 `ParserInfo`가 들어 있는 scan result만 보고 계산한다.
- GDS 포맷 하위 폴더와 정규화명이 같은 형제 폴더는 기존 시리즈 경로맵으로 안전하게 매칭한다.
- 변경 없음으로 처리된 폴더의 fake `ParserInfo` 생성도 직접 dictionary index 대신 fallback lookup을 사용한다.

## 원인 2: GDS 최적화가 archive 페이지 수까지 0으로 생략함

파일:

- `Kavita.Services/Scanner/ProcessSeries.cs`

현재 동작:

- GDS에서는 hash/page 계산 비용을 줄이기 위해 `skipExpensiveFileStats`를 사용한다.
- `GetFastGdsPageCount()`는 기존 페이지가 있으면 보존하고, `epub/pdf/txt`는 1을 넣지만, archive는 0을 반환한다.

영향:

- 기존 archive 행은 문제가 덜하다. 이미 페이지 수가 있으면 보존된다.
- 새로 생긴 archive 행, 이동된 archive 행, 기존 페이지 수가 0인 행은 계속 `Pages=0`으로 남을 수 있다.
- Image reader는 페이지 수가 0이면 커버/읽기/진행률 계산에서 실패하거나 비정상 동작할 수 있다.

개선 방향:

- GDS에서도 archive는 `readingItemService.GetNumberOfPages()`를 호출해야 한다.
- 다만 `KoreaderHash` 계산은 계속 생략해도 된다. 병목은 전체 파일 hash가 훨씬 크고, ZIP 중앙 디렉터리 기반 페이지 카운트는 상대적으로 비용이 낮다.
- 기존 `Pages=0` archive 행은 재스캔으로 회복되도록, `existingPages <= 0 && format == Archive`일 때도 페이지 수를 다시 계산해야 한다.

적용한 보정:

- `GetGdsPageCount()`는 기존 페이지 수가 1 이상이면 보존한다.
- GDS archive에서 기존 페이지 수가 0이면 `readingItemService.GetNumberOfPages()`를 호출한다.
- EPUB/PDF/TXT는 기존처럼 빠른 스캔에서 1로 처리한다.

남은 제약:

- 변경 감지가 완전히 skip한 series는 `AddOrUpdateFileForChapter()`까지 들어오지 않으므로 기존 `Pages=0` DB 행은 일반 스캔만으로 회복되지 않을 수 있다.
- 내부에 이미지가 아니라 nested ZIP만 들어 있는 CBZ는 archive reader가 지원하지 않는 구조다. 이 경우는 scanner bug가 아니라 자료 구조 변환 또는 nested archive 지원이 필요한 영역이다.

## 원인 3: GDS 메타데이터 소스가 운영 소스와 git 소스에 다르게 반영됨

초기에는 운영 빌드 소스와 git 작업트리가 어긋날 위험이 있었다. 현재 기준으로는 다음 변경이 git source와 release source tarball에 모두 들어 있다.

- `GdsMetadataParser`
- GDS에서 `kavita.yaml`을 `EnableMetadata=false`와 무관하게 보조 메타데이터로 읽는 흐름
- GDS 포맷 하위 폴더명 보정
- GDS archive `Pages=0` 회복 로직
- GDS scan changed propagation 보정

현재 상태:

- 운영 이미지: `local/kavita-gds:0.9.0.2-1`
- source branch: `codex/gds-light-novel-scan-fixes`
- public release: `0.9.0.2-5`
- source branch delta after public release: 없음
- public GHCR: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`, `latest`

남은 운영 원칙:

- 이후 패치는 반드시 같은 source branch에서 만들고, 같은 source tarball로 운영 이미지와 GHCR 이미지를 빌드해야 한다.
- 운영 hotfix만 따로 만들 경우 release source와 실제 운영 image가 다시 갈라지므로 금지한다.

## 원인 4: Kavita의 기본 모델이 GDS를 1급 타입으로 가정하지 않음

이미 일부는 고쳤지만, 구조적 위험은 남아 있다.

- `LibraryType.GDS`는 기존 Kavita에 없던 타입이다.
- reader, 제목 포맷터, 라이브러리 타입 switch, UI pipe, 파일 타입 그룹, 외부 메타데이터 매칭 등 여러 곳에서 명시 처리하지 않으면 런타임 예외가 난다.
- 포맷별 reader는 `MangaFormat`을 기준으로 갈라지지만, 라이브러리 화면/시리즈/챕터 포맷은 `LibraryType`과 섞여 판단된다.
- 실제로 `UI/Web/src/app/_services/entity-title.service.ts`는 `LibraryType.GDS` case가 빠져 있어 `app-entity-title` 기반 화면에서 GDS 볼륨/회차명이 빈 문자열로 표시될 수 있었다.

개선 방향:

- GDS를 단순히 Manga/Book 중 하나로 흉내내지 말고, `LibraryType.GDS + MangaFormat.*` 조합을 공식 지원 매트릭스로 정리해야 한다.
- switch/default 예외 지점은 테스트로 막아야 한다.
- GDS mixed-format 시리즈는 series format 하나만 믿지 말고 chapter/file format을 우선해야 한다.

적용한 보정:

- source branch에서 `LibraryType.GDS`를 Manga 제목 렌더링 경로로 태우도록 패치했다.
- source commit: `7e0279e fix: render GDS entity titles`
- 이 패치는 `0.9.0.2-2` 배포 후보 이미지와 source tarball에 포함했다.

## 원인 5: 회차 제목과 파일명 제목이 Kavita 내부에서 다른 필드로 분리됨

파일:

- `Kavita.Services/Scanner/GdsParser.cs`
- `Kavita.Services/Extensions/ChapterExtensions.cs`
- `Kavita.Services/Scanner/ProcessSeries.cs`

현재 구조:

- `GdsParser`는 파일명 기반 제목을 `ParserInfo.Title`에 넣는다.
- `ChapterExtensions.UpdateFrom()`은 일반 chapter에서는 `chapter.Title`을 `chapter.Range` 기반 숫자/범위로 되돌린다.
- `UpdateChapterFromComicInfo()`는 `ComicInfo.Title`을 `chapter.TitleName`에 넣는다.

영향:

- 사용자가 UI에서 보는 “회차명”이 어느 컴포넌트에서 `Title`, `TitleName`, `Range` 중 무엇을 쓰는지에 따라 다르게 보인다.
- 파일명 기반 부제는 `TitleName`에는 들어갈 수 있지만, `Title`이 숫자/범위로 다시 정리되면 기대한 표시와 다를 수 있다.
- `meta.Name`을 그대로 쓰면 시리즈명 또는 회차명이 덮여 회차 정보가 사라질 수 있어서, 현재 GDS YAML parser는 `meta.Name`을 의도적으로 반영하지 않는다.

개선 방향:

- GDS에서는 파일명 기반 표시명을 `TitleName`에 안정적으로 넣는 현재 방향이 맞다.
- UI/DTO에서는 GDS를 Manga 계열 제목 계산에 포함해 `TitleName`을 우선 표시해야 한다.
- 특정 파일명 정규화 규칙은 parser가 아니라 GDS sidecar metadata/parser 보정 계층에 묶어야 한다.

## 원인 5-1: duplicate file path는 기존 중복 chapter/series를 자동 청소하지 못함

파일:

- `Kavita.Services/Extensions/ChapterListExtensions.cs`
- `Kavita.Services/Scanner/ProcessSeries.cs`

현재 구조:

- `GetChapterByRange()`는 먼저 chapter `Range`로 기존 chapter를 찾는다.
- 같은 파일 경로가 다른 chapter에 이미 있는지 확인하는 fallback은 `chapter == null`일 때만 실행된다.
- 기존 DB에 같은 `Range`를 가진 중복 chapter가 이미 있으면 첫 번째 chapter만 갱신되고, 나머지 중복 chapter는 파일이 존재하는 한 cleanup 대상에서 빠질 수 있다.

운영 DB 구조:

| Pattern | Groups |
| --- | ---: |
| same series / same volume / 3 chapters / 1 range / same pages | 17 |
| same series / same volume / 4 chapters / 1 range / same pages | 6 |
| two series / two volumes / two chapters / 1 range / same pages | 94 |
| two series / two volumes / two chapters / 1 range / different pages | 1 |
| two series / two volumes / two chapters / 2 ranges / same pages | 2 |
| two series / two volumes / two chapters / 2 ranges / different pages | 56 |
| same series / same volume / 2 chapters / 1 range / different pages | 3 |

사용자 데이터 참조:

- duplicate path group 중 progress/bookmark/reading list/toc/rating이 붙은 group은 0개였다.
- same series / same volume / same range / 사용자 데이터 없음 조건을 만족하는 group은 26개다.
- cross-series duplicate는 153개 group이다. 사용자 데이터 참조는 없지만, 자동 삭제하면 series 분류 자체가 바뀔 수 있다.

영향:

- scanner가 새 중복을 만들지 않더라도, 이미 DB에 생긴 duplicate rows는 자동으로 사라지지 않는다.
- 같은 파일이 여러 series에 걸친 경우는 단순 dedupe로 지우면 사용자가 의도한 분류를 깨뜨릴 수 있다.
- 같은 series/volume/range 안의 duplicate는 DB repair 후보지만, reading progress와 chapter metadata 연결을 확인해야 한다.

개선 방향:

- 자동 삭제보다 read-only report를 먼저 기준으로 삼는다.
- same series/same volume/same range duplicate만 별도 repair 대상으로 분리한다.
- cross-series duplicate는 parser/폴더 구조 문제인지, 실제로 같은 파일을 여러 라이브러리/시리즈에서 공유하는 의도된 구조인지 구분해야 한다.

## 원인 6: cover cache는 원본 메타데이터와 Kavita config cache 사이에 있음

파일:

- `Kavita.Services/MetadataService.cs`

현재 구조:

- GDS 원본의 `cover.jpg`, `cover.png`, `cover.webp` 또는 `kavita.yaml` base64 cover를 읽는다.
- 실제 UI cover는 Kavita config의 `covers` 디렉터리에 복사/생성된 cache 파일을 본다.
- GDS 원본은 읽기 전용이어도 config cache는 쓰기 대상이다.

영향:

- 운영 config 폴더를 정리하거나 이름을 바꾸면 DB는 살아 있어도 cover cache 파일이 사라질 수 있다.
- 원본 cover가 없고 기존 `_s{id}.jpg/png/webp` cache만 남은 경우, GDS folder cover 경로는 stale cache를 삭제할 수 있다.
- 따라서 “GDS가 읽기 전용이라 안전하다”와 “Kavita config cache가 안전하다”는 별개다.

개선 방향:

- config/covers는 DB와 같이 백업해야 한다.
- folder cover가 없는 경우 기존 cache 삭제가 필요한지, fallback cover 생성을 방해하지 않는지 추가 검토가 필요하다. 현재 운영 DB 기준 위험군은 4,315개 series다.
- cover 재생성은 원본 파일을 수정하지 않지만 config에는 대량 쓰기를 만들 수 있으므로 스캔과 분리해서 실행하는 편이 안전하다.

적용한 보정:

- source branch에서 source `cover.*`가 없을 때 기존 config cache 파일을 삭제하지 않도록 패치했다.
- source commit: `77fa01f fix: preserve GDS cover cache fallback`
- C# build 검증: `dotnet build Kavita.Server/Kavita.Server.csproj` 성공, 480 warnings, 0 errors.
- 이 패치는 `0.9.0.2-2` 배포 후보 이미지와 source tarball에 포함했다.

## 원인 6-1: TXT 파일은 자체 cover extraction 대상이 아님

파일:

- `Kavita.Services/Reading/ReadingItemService.cs`
- `Kavita.Services/MetadataService.cs`
- `Kavita.Services/Helpers/GdsMetadataParser.cs`

현재 구조:

- TXT는 페이지 수 계산은 `BookService.GetNumberOfPagesText()`를 타지만, cover 추출은 `ReadingItemService.GetCoverImage()`의 switch에서 처리되지 않는다.
- EPUB/PDF는 `BookService.GetCoverImage()`로 표지 이미지를 추출할 수 있지만, TXT는 파일 내부에서 추출할 표지가 없으므로 빈 cover가 정상 결과다.
- GDS에서는 TXT 자체보다 폴더 `cover.jpg/png/webp` 또는 `kavita.yaml`의 base64 cover가 권위 있는 커버 소스다.
- `cover: TEXT`는 이미지가 아니라 텍스트 자료 표식이므로 cover 후보로 취급하면 안 된다.

운영 DB 기준 TXT 커버 상태:

| LibraryId | Library | TXT series | TXT files | config cover series | yaml base64 | yaml TEXT marker | source cover file | no usable cover hint |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| <redacted> | production-library-a | 1 | 1 | 1 | 0 | 1 | 0 | 0 |
| <redacted> | production-library-b | 80 | 84 | 80 | 1 | 79 | 79 | 0 |
| <redacted> | production-library-e | 2061 | 2805 | 0 | 0 | 2061 | 0 | 2061 |
| <redacted> | production-library-c | 3548 | 3548 | 3544 | 3540 | 0 | 1 | 4 |
| <redacted> | production-library-d | 25 | 125 | 25 | 24 | 1 | 0 | 0 |

해석:

- TXT에 cover가 없다는 것은 파일 오류가 아니다.
- production-library-e의 `kavita.yaml`은 `cover: TEXT`라서 이미지 커버가 아니다.
- 진짜로 사용 가능한 원본 cover 후보가 없는 TXT series는 production-library-e 기준 2,061개다.

개선 방향:

- 우선순위는 기존 config cover cache, folder `cover.*`, `kavita.yaml` base64 cover 순으로 유지한다.
- 위 세 가지가 모두 없으면 스캔 오류를 만들지 않고 GDS/TXT 전용 deterministic title-cover를 Kavita config `covers` 디렉터리에 생성한다.
- title-cover를 만들더라도 GDS 원본에는 쓰지 않고, 외부 이미지 다운로드도 하지 않는다.
- title-cover 생성은 source cover가 나중에 생기면 source cover가 다시 이기도록 fallback으로만 동작해야 한다.
- 진단 스크립트의 빠른 `--check-covers` 출력에서는 DB/config cover 감소 여부를 본다.
- source `cover.*`와 YAML hint까지 포함한 `series_without_any_cover_hint`는 `--check-covers --check-cover-source-files`를 별도 실행했을 때만 수동 큐레이션 또는 fallback 생성 대상으로 삼는다.
- 한글 제목 렌더링을 위해 Docker image에 Nanum Gothic TTF를 포함해야 한다.

적용한 보정:

- `cover: TEXT`와 URL 값을 base64 cover로 오인하지 않도록 source branch에서 `GdsMetadataParser`를 보정했다.
- GDS/TXT에서 folder cover와 YAML base64 cover가 없을 때 제목 기반 cover를 생성하도록 source branch에서 fallback을 추가했다.
- 생성 cover는 Kavita config `covers`에만 저장되고 GDS 원본에는 쓰지 않는다.
- Dockerfile에 Nanum Gothic 폰트를 포함해 한글 제목이 네모 박스로 렌더링되지 않도록 했다.
- C# build 검증: `dotnet build Kavita.Server/Kavita.Server.csproj` 성공, 481 warnings, 0 errors.
- 런타임 스모크 검증: 제목 기반 PNG 생성 성공, Nanum Gothic 사용 시 한글 제목 렌더링 확인.
- 이 패치는 `0.9.0.2-2` 배포 후보 이미지와 source tarball에 포함했다.

## 원인 7: `Pages=0` 잔여 행은 일반 스캔에서 복구되지 않을 수 있음

파일:

- `Kavita.Services/Scanner/ParseScannedFiles.cs`
- `Kavita.Services/Scanner/ProcessSeries.cs`
- `Kavita.Database/Repositories/SeriesRepository.cs`

현재 구조:

- GDS 일반 library scan은 폴더 mtime이 기존 `LastScanned`보다 오래되면 실제 파일 목록을 다시 읽지 않고, 기존 series 정보를 기반으로 fake `ParserInfo`만 만든다.
- 이 최적화는 정상 상태에서는 빠른 재스캔에 필요하지만, DB에 이미 `Pages=0` 같은 scan debt가 남아 있으면 해당 파일을 다시 열 기회도 같이 사라진다.
- 운영 DB 기준 `Pages=0` archive는 두 라이브러리에만 남아 있다.

운영 DB 기준:

| LibraryId | Library | Ext | Pages=0 files | Archive readable | Direct images | Nested archives |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| <redacted> | production-library-a | cbz | 10 | 10 | 0 | 84 |
| <redacted> | production-library-d | zip | 39 | 39 | 13,301 | 0 |

해석:

- Library 4의 잔여 CBZ는 직접 이미지가 아니라 nested archive 중심이라 Kavita의 archive page counter 기준으로 `Pages=0`이 자연스러운 케이스다.
- Library 9의 잔여 ZIP은 archive 자체가 읽히고 직접 이미지도 있으므로 파일 손상보다는 기존 DB row가 최적화 때문에 재분석되지 않은 상태로 보는 것이 맞다.
- 전체 `Pages=0` 중 duplicate path와 직접 겹치는 것은 3 row뿐이라, 모든 `Pages=0`을 중복 row 문제로 단정하면 안 된다.

적용한 보정:

- source branch에서 `SeriesModified`에 `HasZeroPageFiles`를 추가했다.
- GDS series에 `Pages=0` 파일이 하나라도 있으면 `HasSeriesFolderNotChangedSinceLastScan()`이 변경 없음으로 처리하지 않고 실제 scan path를 타도록 했다.
- C# build 검증: `dotnet build Kavita.Server/Kavita.Server.csproj` 성공, 481 warnings, 0 errors.
- 이 패치는 `0.9.0.2-2` 배포 후보 이미지와 source tarball에 포함했다.

## 원인 8: startup migration 실패가 BaseUrl 저장 FK 오류처럼 보일 수 있음

제보 증상:

```text
Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'FOREIGN KEY constraint failed'
Kavita.Server.Startup.Configure(...) Startup.cs:line 289
```

해석:

- 해당 line은 운영 설정의 BaseUrl을 `ServerSetting`에 저장하는 코드였다.
- BaseUrl 저장 자체가 외래키를 직접 건드리지는 않는다.
- 더 가능성이 높은 경로는 startup manual migration 중 FK 오류가 먼저 발생했지만, 기존 코드가 migration 예외를 catch 후 계속 진행하면서 같은 EF context 또는 pooled context에 실패한 변경 상태가 남고, 이후 BaseUrl `SaveChanges()`에서 다시 표면화되는 경우다.
- 일반 x86/NAS에서는 같은 이미지가 정상 기동되고 Oracle 쪽에서만 발생했다면, CPU 아키텍처 자체보다는 Oracle 서버의 기존 DB 상태, 기존 컨테이너와 새 컨테이너의 동시 접근, compose volume이 다른 DB를 가리키는 문제, 또는 이전 이미지에서 생성된 특정 migration 상태 차이를 먼저 의심해야 한다.
- 실제 DB 자체에 영구 FK 위반이 있는지는 `PRAGMA foreign_key_check;`로 확인해야 한다.

적용한 보정:

- startup migration은 별도 DI scope에서 실행하고, BaseUrl 저장도 별도 DI scope에서 실행하도록 분리했다.
- migration 예외를 더 이상 삼키지 않고 rethrow해 실제 실패 지점이 로그에 남도록 했다.
- BaseUrl 저장에서 `DbUpdateException`이 발생하면 `PRAGMA foreign_key_check` 일부 결과를 로그에 남기도록 했다.
- source commit: `6fa0173 fix: stabilize GDS startup and cleanup`
- C# build 검증: `dotnet build Kavita.Server/Kavita.Server.csproj` 성공, 481 warnings, 0 errors.
- 이 패치는 `0.9.0.2-5` 배포 후보 이미지와 source tarball에 포함했다.

운영자 진단 명령:

```bash
sqlite3 /path/to/kavita.db 'PRAGMA integrity_check;'
sqlite3 /path/to/kavita.db 'PRAGMA foreign_key_check;'
```

환경 비교용 preflight 수집:

```bash
scripts/collect_gds_preflight.sh \
  --db /path/to/kavita.db \
  --output-dir /tmp/kavita-gds-preflight \
  --label oracle-a1-startup \
  --snapshot-db
```

이 manifest에는 `host_arch`, `host_uname`, Docker client/server version, Docker architecture가 기록된다. 진단 JSON에는 `ef_migration_summary`, `manual_migration_summary`, `server_settings`, `core_table_counts`가 들어간다. x86/NAS 쪽 정상 사례와 Oracle 쪽 실패 사례를 비교할 때 이 값과 `foreign_key_check` 결과를 같이 보면 이미지 아키텍처 문제인지, DB/운영 환경 문제인지 더 빨리 분리할 수 있다.

## 원인 9: same-volume duplicate file path는 스캔 cleanup에서 살아남을 수 있음

현재 구조:

- 같은 volume 안에서 같은 파일 경로가 여러 chapter에 붙어 있어도, 기존 cleanup은 “현재 스캔 결과에 존재하는 파일 경로”이면 각 chapter의 파일을 계속 보존할 수 있었다.
- 운영 DB 기준 same-series/same-volume duplicate group은 일부 라이브러리에 남아 있으며, 같은 range와 같은 page 값이면 사용자가 보기에는 중복 chapter로 보일 수 있다.

적용한 보정:

- 같은 volume cleanup 중 이번 스캔에서 선택된 chapter만 해당 normalized file path를 보존하도록 했다.
- cross-series duplicate는 자료 구조 의도일 수 있어 자동 정리하지 않는다.
- source commit: `6fa0173 fix: stabilize GDS startup and cleanup`
- source commit: `40ec52d fix: prefer scanned GDS duplicate target`
- C# build 검증: `dotnet build Kavita.Server/Kavita.Server.csproj` 성공, 481 warnings, 0 errors.
- 이 패치는 `0.9.0.2-5` 배포 후보 이미지와 source tarball에 포함했다.

## 원인 10: 스캔 wall-time은 file discovery와 series update를 나눠 봐야 함

운영 로그에서 같은 “스캔이 느림”이라도 병목 위치가 서로 달랐다.

재현 도구:

```bash
python3 scripts/summarize_kavita_scan_logs.py \
  /your/kavita/config/logs/kavita20260531.log
```

기본 출력은 `library_key`, `series_key` 해시만 보여주며 실제 library/series 이름은 숨긴다. 로컬에서만 이름 확인이 필요하면 `--show-library-names`, `--show-series-names`를 붙인다.

운영 로그에서 확인한 패턴:

| Case | found series | processed series | processed files | found/file-discovery ms | series update sum ms | total ms | 해석 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| large mixed scan | 297 | 297 | 3004 | 579,414 | 13,289 | 593,313 | 대부분 file discovery/listing 시간이다. series update 자체는 빠르다. |
| repeated small scan before stabilization | 7 | 7 | 109 | 9,230 | 594,623 | 608,282 | 일부 series update가 매우 길어 archive/page/metadata 처리 병목이 의심된다. |
| repeated small scan after stabilization | 7 | 7 | 171 | 11,786 | 117 | 11,950 | update 비용은 사라지고 discovery 시간이 주가 됐다. |
| no-change scan | 0 | 0 | 0 | 522 | 0 | 709 | 변경 없음 최적화가 정상 동작하면 sub-second 수준이다. |

해석:

- 대형 강제 스캔은 `ProcessSeries`보다 폴더 열거와 parser 입력 구성 시간이 대부분이다.
- 작은 반복 스캔이 수십만 ms까지 느려지는 경우는 file discovery가 아니라 특정 series update 내부 작업이 원인이다.
- 같은 라이브러리라도 패치 전후 또는 force 여부에 따라 병목 위치가 달라지므로, 단순 총 시간만 보면 원인을 잘못 짚을 수 있다.
- postflight에서는 DB gate와 별도로 scan log summary의 `found_ms`, `series_update_ms_sum`, `processed_series`, `processed_files`를 같이 비교해야 한다.

### 2026-05-31 file discovery 직접 측정

Kavita 로그만으로는 `Found N Series that need processing in X ms` 전의 시간이 실제 어디서 쓰이는지 알기 어렵다. 그래서 read-only 파일 트리 프로파일러를 추가했다.

```bash
scripts/profile_gds_tree.py /your/gds/mount/<redacted-media-path>
  --time-limit 120
```

도구 특징:

- DB나 Kavita API를 사용하지 않고 `os.scandir` 기반으로 media tree만 읽는다.
- 기본 출력은 경로명을 안정 해시로 숨긴다.
- `--show-names`를 넣은 경우에만 실제 경로명을 출력한다.
- top-level child별 elapsed time, file/dir count, extension count, slowest scandir path를 JSON line으로 출력한다.

측정 결과:

- 같은 경로의 `--max-depth 1` 측정은 12개 top-level, 579개 하위 directory를 약 6.5ms에 끝냈다.
- 전체 깊이 측정은 첫 번째 top-level child를 약 13ms에 끝냈지만, 두 번째 top-level child에서 120초 time limit에 도달했다.
- 느린 child는 120초 동안 `dirs 452`, `files 641`, `scandir_calls 247`만 처리했고, slowest single scandir는 약 1.2초였다.
- 느린 child를 다시 root로 삼아 측정하니, 그 내부에서 한 child는 약 24초, 다른 child는 약 96초 이상을 소비하며 120초 time limit에 도달했다.

해석:

- 루트 또는 1-depth directory listing 자체가 느린 것이 아니다.
- 특정 깊은 하위 트리에서 많은 작은 TXT/YAML 파일과 directory stat/listing이 rclone/FUSE 왕복으로 누적된다.
- 최신 이미지 `0.9.0.2-5`의 별도 scan smoke에서 텍스트 중심 라이브러리 force scan이 file scan 시작 후 멈춘 현상은 이 직접 측정과 같은 계열이다.
- 따라서 대형 텍스트 라이브러리 force scan은 Kavita scanner update 최적화만으로 해결할 수 없고, file discovery 범위 축소, 변경 후보 제한, rclone directory cache 예열/TTL, 특정 subtree 분할 검증이 필요하다.

rclone 상태:

- mount 옵션은 이미 `--read-only`, `--dir-cache-time=1000h`, `--poll-interval=0`, `--vfs-cache-mode=full`이다.
- 따라서 원본 쓰기 문제나 짧은 dir cache TTL 문제가 아니라, 아직 cache가 채워지지 않은 깊은 하위 tree를 처음 재귀 순회할 때 발생하는 비용으로 보는 편이 맞다.
- 반복 측정 전 RC `core/stats` 기준 `listed`는 201,862였고, delete/rename/server-side move는 0이었다.
- 반복 측정 전 RC `vfs/stats` 기준 metadata cache는 `dirs 40,708`, `files 145,847`, disk cache는 약 581 files / 4.7GB였다.
- 같은 full-depth 측정을 다시 실행하자 이전에 120초 timeout에 걸렸던 두 번째 top-level child는 약 39초에 끝났지만, 다음 cold child가 약 81초를 소비한 뒤 전체 120초 timeout에 도달했다.
- 반복 측정 후 RC `core/stats`의 `listed`는 203,425로 증가했고, delete/rename/server-side move는 계속 0이었다.
- 반복 측정 후 RC `vfs/stats`의 metadata cache는 `dirs 40,900`, `files 146,546`, disk cache는 약 586 files / 5.5GB로 증가했다.
- 이 수치는 분석/reader/scan 과정에서 이미 많은 directory metadata와 file cache가 쌓였음을 보여주지만, 특정 subtree의 cold traversal은 여전히 길게 걸릴 수 있다.
- 즉 prewarming은 효과가 있지만, 한 번에 전체 문제를 없애는 방식이 아니라 아직 차가운 다음 subtree로 병목이 이동하는 방식이다.
- 대형 force scan을 운영 검증의 첫 단계로 쓰면 scanner 개선 효과와 rclone metadata warming 비용이 섞인다. 최신 이미지 검증은 작은 범위 scan, 특정 series scan, no-change scan을 먼저 보고 필요한 경우 subtree 단위 prewarm을 별도 절차로 분리해야 한다.

운영 전환 후 추가 증거:

- `0.9.0.2-5` 운영 이미지에서 작은 GDS 라이브러리를 일반 스캔했을 때, 새로 발견된 실제 폴더 1개만 처리한 뒤 다음 일반 스캔은 `Found 0 Series`, 전체 약 `70 ms`로 끝났다.
- 같은 라이브러리를 force scan하자 `Found 69 Series that need processing in 90,796 ms`, 전체 `134 files / 69 series / 92,147 ms`가 걸렸다.
- force scan의 series update 로그는 대부분 ms 단위였고, 총 시간의 대부분은 `Found ...` 로그 전 file discovery/YAML/rclone read 단계에서 발생했다.
- force scan 중 rclone RC는 여러 `kavita.yaml` 읽기와 VFS cache 증가를 보여줬고, delete/rename/server-side move는 계속 0이었다.
- force scan 후 same-series duplicate cleanup 후보는 전체 기준 `26 -> 13`으로 줄었고, 대상 작은 라이브러리의 duplicate file path는 0이 됐다.

따라서 현재 남은 성능 병목은 “scanner가 series update를 오래 잡고 있는 문제”보다 “force scan이 GDS 트리의 YAML/파일 목록을 다시 읽는 비용” 쪽으로 더 강하게 좁혀진다. 일반 스캔은 최적화 효과가 확인됐고, duplicate cleanup은 force scan이 해당 라이브러리에 닿으면 실제로 작동한다.

## 원인 11: reader 지연은 scanner 병목과 분리해야 함

운영 로그에는 스캔이 아닌 reader 요청에서도 긴 지연이 확인됐다. 예를 들어 `/api/reader/image` 요청 하나가 약 18.9초 걸린 사례가 있었다. 해당 DB 행은 archive format의 ZIP이고 파일 크기는 약 385MB였다.

코드 경로:

- `ReaderController.GetImage()`는 먼저 `cacheService.Ensure(chapterId, extractPdf)`를 호출한다.
- `CacheService.Ensure()`는 chapter cache 폴더가 없거나 비어 있으면 `ExtractChapterFiles()`로 들어간다.
- archive format이면 `ReadingItemService.Extract()`를 통해 `ArchiveService.ExtractArchive()`가 호출된다.
- 이후 `GetCachedPagePath()`가 cache 폴더의 이미지 파일을 다시 열거하고 page 번호에 맞는 파일을 반환한다.

해석:

- 큰 ZIP을 처음 열거나 cache miss가 발생한 chapter는 “reader image 요청” 안에서 archive 추출 비용을 지불한다.
- GDS/rclone 환경에서는 이 비용에 remote cold read, rclone VFS/cache 상태, ZIP 중앙 디렉터리 접근, archive 압축 해제 비용이 함께 섞일 수 있다.
- 따라서 사용자가 체감하는 느림이 모두 scanner 문제는 아니다. 스캔 로그의 `Finished library scan...`과 HTTP request log의 `/api/reader/image... responded ... ms`는 별도 지표로 봐야 한다.

진단 도구:

```bash
python3 scripts/summarize_kavita_scan_logs.py \
  /your/kavita/config/logs/kavita20260531.log \
  --slow-request-ms 3000
```

이 출력은 기본적으로 endpoint, status, ms, page, `chapter_key`만 보여준다. raw chapter id가 필요할 때만 `--show-request-ids`를 붙인다.

DB/cache 상관분석:

```bash
python3 scripts/analyze_kavita_reader_latency.py \
  /your/kavita/config/logs/kavita20260531.log \
  --db /your/kavita/config/kavita.db \
  --cache-dir /your/kavita/config/cache \
  --slow-request-ms 3000
```

2026-05-31 운영 로그 기준으로는 3초 이상 느린 reader 요청 8개 중 7개가 ZIP archive였고, 가장 느린 `reader/image` 요청들은 120-367MB archive 및 150-377MB cache folder와 연결됐다. 현재 cache가 남아 있는 항목은 사후 상태라 “요청 당시 이미 cache hit였다”는 뜻은 아니다. `CacheService.Ensure()`가 요청 처리 안에서 cache miss를 채운 뒤 같은 요청을 완료할 수 있기 때문이다.

2026-05-31 17:45 재수집한 운영 baseline에서도 같은 결론이다.

- slow request 25개 중 scanner endpoint가 아니라 reader endpoint가 상위권을 차지했다.
- reader latency 상관분석 기준 slow reader request 8개 중 7개가 ZIP이었다.
- 가장 느린 `/api/reader/image`는 약 24.3초였고, 354MB archive와 364MB cache folder에 연결됐다.

따라서 사용자가 “느리다”고 느낀 지점은 최소 두 계층으로 분리해야 한다.

- scan time: file discovery, changed propagation, series update
- read time: archive open/cache extraction/rclone cold read

두 지표를 섞으면 scanner patch 효과를 과소평가하거나 reader cache 문제를 scanner 문제로 오인하게 된다.

## 원인 12: MediaError는 scanner bug와 파일 구조 문제를 분리해야 함

운영 DB의 MediaError를 `Extension`, `Comment`, `Details` 기준으로 분류하면 다음과 같다.

| Category | Count | 해석 |
| --- | ---: | --- |
| `epub-unrecognized-by-parser` | 284 | 파일 확장자는 EPUB이지만 Kavita parser가 series/chapter 정보를 만들지 못했다. |
| `epub-missing-referenced-file` | 177 | OPF/manifest가 참조하는 내부 XHTML 등이 실제 EPUB 안에 없다. |
| `epub-invalid-manifest` | 117 | EPUB manifest 중복/누락 등 구조 오류다. |
| `pdf-malformed-or-metadata` | 30 | PDF metadata 파싱 중 형식 오류가 난다. |
| `archive-unreadable-or-unsupported` | 10 | archive reader가 읽을 수 없거나 지원하지 않는 구조다. |
| `epub-missing-nav` | 9 | EPUB3 NAV 항목이 manifest에 없다. |
| `epub-page-count-failed` | 5 | 페이지/word count 계산 실패다. |
| `epub-not-a-valid-zip` | 2 | EPUB이 ZIP 컨테이너로 유효하지 않다. |
| `pdf-encrypted` | 2 | 암호화 PDF라 Kavita가 지원하지 않는다. |
| `scanner-unrecognized-file` | 1 | 미디어 파일로 보기 어려운 파일이 스캔 대상에 들어왔다. |

해석:

- 이 분포는 “GDS scanner가 정상 파일을 대량으로 망가뜨렸다”기보다, Kavita의 EPUB/PDF parser가 엄격하게 보는 내부 구조 문제와 일부 잘못 분류된 파일이 누적된 상태로 보는 것이 맞다.
- `EnableMetadata=false`를 꺼도 word count, cover generation, reader 진입 시 BookService가 EPUB 내부 구조를 다시 열 수 있어 오류가 남을 수 있다.
- EPUB 계열은 원본 수정 없이 해결하려면 스캔에서 무시할지, TXT/ZIP 등 다른 format으로 변환할지, 또는 BookService 예외를 더 부드럽게 degrade할지 정책을 정해야 한다.

## 우선순위

1. `ParseScannedFiles`의 changed propagation을 시리즈 단위로 고친다. 완료 및 운영 반영.
2. GDS archive 페이지 수가 0으로 남지 않게 고친다. 신규/변경 파일 보정 완료, 기존 DB scan debt 재분석 트리거는 `0.9.0.2-2` 이후 배포 후보에 포함.
3. 운영 source, release source, GitHub 배포 source를 같은 기준으로 맞춘다. 완료.
4. 기존 DB의 `Pages=0`, duplicate file path, media error를 읽기 전용 검증 쿼리로 분류한다. 1차 완료.
5. GDS UI 제목 표시 누락과 cover cache 삭제 방어를 `0.9.0.2-2` 배포 후보에 포함한다. 완료.
6. TXT no-cover는 오류로 처리하지 않고, YAML/base64 cover 반영 누락과 실제 no-cover fallback을 분리한다. 완료.
7. same series/same volume/same range duplicate group은 cleanup patch를 `0.9.0.2-5`에 포함했고, 운영 재스캔 후 감소 여부를 확인한다.
8. cross-series duplicate 153개 group과 nested archive는 자동 수정하지 말고 자료 구조/분류 의도를 먼저 확인한다.
9. scan log summary로 file discovery 병목과 series update 병목을 분리해서 postflight에 같이 남긴다.
10. reader slow request는 scanner 병목과 분리해 cache miss/큰 archive/rclone read latency 관점으로 집계한다.
11. MediaError는 원인 범주별로 나눠 scanner 수정 대상과 파일 변환/정리 대상을 분리한다.
12. 혼합 포맷 시리즈의 EPUB word-count 오류는 scanner가 아니라 `WordCountAnalyzerService`의 파일별 포맷 확인 누락이었다. `0.9.0.2-6`에서 비 EPUB 파일을 EPUB reader에 넘기지 않도록 수정했다.

## 다음 검증 기준

패치 후에는 최소한 다음이 성립해야 한다.

- 폴더 mtime상 변경 후보가 0인 라이브러리에서 일반 재스캔이 대량의 `Processing series`를 만들지 않는다.
- GDS archive 파일의 신규/이동 DB 행이 `Pages=0`으로 남지 않는다.
- 기존 `Pages=0` archive 행은 force scan 또는 targeted repair로 실제 페이지 수를 회복한다. nested archive는 별도 처리한다.
- `kavita.yaml`이 있는 폴더는 `EnableMetadata=false` 상태에서도 안전한 로컬 메타데이터를 반영한다.
- 운영 이미지, Release source, git 작업트리의 핵심 scanner 파일이 서로 달라지지 않는다.
- duplicate path group이 추가 스캔 후 늘어나지 않는다.
- cover cache 재생성은 원본 GDS 경로가 아니라 Kavita config 경로에만 쓰기를 만든다.
- source `cover.*`가 없는 series라도 기존 config cover cache를 삭제하지 않는다.
- TXT series는 source cover가 없어도 스캔 오류로 남기지 않고, YAML/base64 또는 fallback cover 정책으로 일관되게 표시한다.

## `0.9.0.2-8` 반영 상태

`0.9.0.2-7` 운영 적용 뒤 scanner와 별개로 기본 시리즈 정렬이 제목 오름차순으로 돌아가는 문제가 확인됐다. UI 기본값과 백엔드 null sort fallback이 서로 `최근 수정 내림차순` 기준을 강제하지 못했고, UI 필터 상태 복원에서 명시적 `false` 값이 오름차순으로 바뀌는 문제가 있었다.

`0.9.0.2-8`에는 다음 보정이 포함됐다.

- UI 기본 series sort field를 `LastModified`로 변경했다.
- series 기본 정렬 방향을 내림차순으로 지정했다.
- 명시적 내림차순 값이 보존되도록 필터 상태 복원 로직을 보정했다.
- 백엔드 series/bookmark null sort fallback을 `LastModifiedDate desc`로 맞췄다.
- 운영 API에서 sort option 없이 조회했을 때 `Series.LastModified desc`와 일치함을 확인했다.

## `0.9.0.2-7` 반영 상태

`0.9.0.2-6` 운영 적용 뒤 신규 GDS archive 시리즈 일부에서 파일과 page count는 정상인데 cover reference가 비어 있는 문제가 확인됐다. scanner가 파일을 놓친 것이 아니라, `MetadataService`의 GDS 커버 분기가 YAML/base64 커버나 TXT 제목 커버가 없을 때 일반 archive cover extraction으로 fallback하지 않고 종료한 것이 원인이었다.

`0.9.0.2-7`에는 다음 보정이 포함됐다.

- GDS YAML/base64 커버가 있으면 기존처럼 우선 사용한다.
- GDS TXT 제목 기반 fallback cover는 유지한다.
- GDS archive는 YAML/TXT 커버가 없으면 일반 ZIP/CBZ 첫 페이지 커버 추출 경로로 fallback한다.
- `linux/amd64`, `linux/arm64` self-contained publish와 multi-arch OCI build를 통과했다.

## `0.9.0.2-6` 반영 상태

운영 재스캔에서 production-library-d 라이브러리의 복구 가능 `Pages=0` archive와 same-series duplicate는 해소됐고, 남은 `Pages=0`/same-series duplicate는 nested archive 구조로 분리됐다. 이 항목은 scanner가 직접 복구할 수 있는 direct-image archive scan debt와 다르게 취급한다.

별도로, 대표 포맷이 EPUB인 혼합 포맷 시리즈에서 word-count analyzer가 챕터의 PDF/TXT 파일까지 EPUB로 열어 오류 이벤트를 만들 수 있었다. 이 문제는 scanner 단계의 series/chapter 매칭 문제가 아니라 분석 서비스의 파일별 포맷 검사 누락이다.

`0.9.0.2-6`에는 다음 보정이 포함됐다.

- `WordCountAnalyzerService`가 `MangaFile.Format == Epub`인 파일만 EPUB reader로 연다.
- 비 EPUB 파일은 word-count 대상에서 제외하고 `LastFileAnalysis`를 갱신한다.
- 혼합 포맷 회귀 테스트를 추가했다.
- `linux/amd64`, `linux/arm64` self-contained publish와 multi-arch OCI build를 통과했다.
- 공개 GHCR manifest는 `linux/amd64`, `linux/arm64`를 포함한다.

운영 baseline 수집도 단계별로 분리한다.

- 먼저 `--snapshot-db` DB-only preflight로 integrity, FK, Pages=0, duplicate, MediaError를 확인한다.
- 다음으로 `--check-archives`만 실행해 `Pages=0` archive가 파일 문제인지 DB debt인지 분리한다.
- `--check-covers`는 DB/config cover reference만 확인하는 빠른 gate로 일반 postflight에 포함할 수 있다.
- rclone source cover probe가 필요한 경우에만 `--check-covers --check-cover-source-files`를 별도 단계로 실행한다. 이 검사는 많은 directory stat/list를 만들 수 있다.
