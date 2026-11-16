# Pixel To Voxel

PNG 도트 이미지를 일정한 두께의 3D 메시와 Unity 프리팹으로 변환하는 에디터 패키지입니다.
Unity 6을 대상으로 작성했으며, 기준 프로젝트 환경은 **Unity 6000.3.28f1 / URP 17.3**입니다.
Built-in과 URP의 Unlit 머티리얼을 선택하며 HDRP는 지원하지 않습니다.

## 설치

Unity Package Manager → **Install package from git URL**:

```text
https://github.com/mine-farm/pixel-to-voxel.git
```

비공개 저장소이므로 로컬 Git에 이 저장소를 읽을 수 있는 인증이 필요합니다.
또는 저장소를 내려받고 Package Manager의 **Install package from disk**에서 `package.json`을 선택합니다.

## 사용

1. **Tools → Pixel To Voxel**을 엽니다.
2. **New Conversion Group**으로 그룹 에셋을 저장합니다. 그룹과 프리셋은 `Assets/Editor/` 아래에 보관하는 것을 권장합니다.
3. PNG, Sprite 또는 프로젝트 폴더를 입력에 추가합니다. 폴더는 하위 폴더까지 검색하며 Sprite 시트는 개별 Sprite로 변환합니다. 중복 입력은 제거합니다.
4. 크기, 두께, 알파 기준, 원점 등을 설정합니다.
5. **Analyze Inputs**로 파일별 픽셀 수, 정점/삼각형 수와 경고를 확인합니다. **Preview**에서 회전/줌으로 확인할 수 있습니다.
6. 필요한 파일만 체크하고 **Generate Selected**를 누릅니다. 경고 파일은 포함하거나 제외할 수 있습니다.

출력은 기본적으로 `Assets/Generated/PixelToVoxel/<그룹 이름_GUID>/` 아래에 생성됩니다.
모델마다 전용 폴더에 Mesh와 Prefab을 저장하고, 공유 팔레트와 머티리얼은 그룹의 `Shared/`에 보관합니다.
프리팹을 씬에 드래그하면 일반적인 MeshFilter / MeshRenderer 오브젝트로 사용할 수 있습니다.

## 설정

| 설정 | 의미 |
|---|---|
| Pixel Step | 확대된 PNG에서 도트 한 칸에 해당하는 픽셀 수. 각 블록의 중앙 픽셀을 샘플링하며 가로·세로가 이 값으로 나누어져야 합니다. |
| Pixel Size | 복셀 한 칸의 월드 단위 크기. |
| Depth | 픽셀 크기 단위의 두께. 예: 크기 0.1, 두께 3 → Z축 두께 0.3. |
| Alpha Threshold | 1–255. 미만은 비우고 이상은 완전 불투명한 RGB로 변환합니다. 반투명은 유지하지 않습니다. |
| Trim Transparent | 그림 바깥 투명 여백 제거. 끄면 원본 캔버스를 기준으로 원점을 계산합니다. |
| Flip X / Y | 도트 좌우/상하 반전. |
| Pivot | 모델 중심 / 하단 중앙. Facing 회전 전의 좌표를 기준으로 합니다. |
| Facing | 전방 / 후방 / 위 / 아래 방향으로 메시를 회전합니다. |
| Collider | 없음 / 경계 Box / 비볼록 MeshCollider. 동적 Rigidbody에는 Box를 사용하세요. |
| Existing Outputs | 이 그룹 소유의 기존 결과 갱신 / 이미 생성한 모델 건너뛰기. |
| Shared Atlas | 그룹의 모델들이 한 팔레트와 머티리얼을 공유. 끄면 모델별로 생성합니다. |
| Atlas Size | 16–1024, 2의 거듭제곱. 4×4 색상 타일이므로 기본 256은 최대 4,096색입니다. |

설정은 프리셋 에셋으로 저장하거나 다른 그룹에 적용할 수 있습니다.

## 대량 변환과 재생성

- 유효 픽셀당 한 개의 메시 기둥을 만들며, 인접한 기둥 사이 면을 제거합니다. 깊이 방향의 동일한 복셀은 한 기둥으로 합쳐 정점 수가 두께에 비례해 늘어나지 않습니다.
- 픽셀별 GameObject는 만들지 않습니다. 모델 하나당 기본적으로 메시와 프리팹 하나입니다.
- 65,535개를 넘는 정점은 32비트 인덱스를 사용합니다.
- 공유 팔레트의 색상 슬롯은 추가만 합니다. 일부 모델을 건너뛰거나 배치를 취소해도 기존 모델의 UV와 색상이 유지됩니다.
- Mesh, Texture, Material은 기존 에셋을 갱신하여 GUID와 로컬 파일 ID를 유지합니다. Prefab은 내용을 불러와 갱신하여 사용자 컴포넌트와 자식 오브젝트를 유지합니다.
- 루트 MeshFilter, MeshRenderer, BoxCollider, MeshCollider는 생성기가 관리합니다. 이 부분은 재생성 시 설정에 맞게 변경됩니다.
- 입력 ID는 GUID + local file ID이므로 같은 이름의 파일과 Sprite 시트도 구분합니다. 소스 이름이 바뀌어도 기존 출력 위치를 사용합니다.
- 진행률과 취소를 지원합니다. 취소는 다음 검사 지점에서 반영되며, 이미 저장한 모델은 유지됩니다. 배치 전체 롤백은 하지 않습니다.
- 생성 이력에는 소스, 출력 에셋, 마지막 성공 시각, 적용 설정 JSON, 메시 통계를 저장합니다.
- 생성 후 그룹의 아틀라스 모드·크기와 출력 루트는 고정됩니다. 바꾸려면 새 그룹을 만드세요. 생성된 그룹을 복제한 에셋은 원본 결과의 변경을 막기 위해 재생성을 차단합니다.
- 생성된 에셋을 다른 폴더로 옮기면 자동 덮어쓰기를 막습니다. 원래 위치로 복원한 후 재생성하세요.

## 경고와 안전 한도

기본 경고는 원본 한 변 **256px 초과**, 불투명 셀 **16,384개 초과**, 예상 정점 **200,000개 초과**입니다.
세 기준은 그룹에서 조정할 수 있고, 경고가 있어도 명시적으로 포함해 진행할 수 있습니다.
투명 여백이 큰 이미지도 원본 크기 경고를 표시하되 실제 셀 수와 메시 규모는 별도로 계산합니다.

에디터 메모리 과다 사용을 막기 위한 고정 상한도 있습니다: 소스 PNG 128 MiB / 16,777,216픽셀,
샘플링 결과 한 장 1,048,576셀, 메시 한 개 500,000면, 배치 전체 보관 셀 16,777,216개.
상한 초과는 오류로 안내하며 Pixel Step을 높이거나 입력을 나누어야 합니다.

원본 PNG를 직접 디코딩하므로 Read/Write나 압축 설정을 수정하지 않습니다.
Sprite는 원본 PNG 영역을 사용합니다. 패킹된 런타임 Sprite는 받지 않으며, Sprite 임포트가 축소된 경우 Max Size 조정을 안내합니다.
색상 타일은 Point / Clamp, mipmap 없이 저장합니다. 머티리얼 공유가 모든 모델을 한 draw call로 합쳐준다는 의미는 아닙니다.

## 테스트

Unity 없이 실행하는 실제 C# 코어 테스트 (.NET SDK 8, 외부 NuGet 의존성 없음):

```bash
dotnet run --project Tests/Standalone~/PixelToVoxel.CoreTests.csproj --configuration Release -- .
```

면 제거, 법선/와인딩, 깊이, 알파, 샘플링, 원점, 반전, 팔레트 재로딩과 안정성,
메모리 상한과 취소, 큰 인덱스, 300개 무작위 입력의 표면적을 검사합니다.
패키지 전체 C# 구문과 어셈블리 설정도 검사합니다. `Standalone~`는 Unity 임포트에서 제외됩니다.
GitHub Actions에서도 같은 코어 테스트를 실행합니다.

Unity AssetDatabase / 프리팹 연동 테스트는 `Tests/Editor/`에 있습니다.
프로젝트 `Packages/manifest.json`의 최상위에 아래 설정을 추가하고 Test Runner의 EditMode에서 실행하세요.

```json
"testables": ["net.minefarm.pixel-to-voxel"]
```

에디터 테스트에는 에셋 참조 유지, 씬 참조, 사용자 자식 유지, 대량 공유 팔레트, 건너뛰기,
콜라이더 전환, 경고 제외, 원본 임포터 보존과 덮어쓰기 방지 검사가 포함됩니다.
**현재 작업 환경에는 Unity 실행 파일이 없어 Unity 컴파일, 에디터 연동 테스트와 실제 미리보기 렌더링은 실행하지 못했습니다.**

자세한 요구사항과 검수 항목: [기능 명세](Documentation~/specification.md).
