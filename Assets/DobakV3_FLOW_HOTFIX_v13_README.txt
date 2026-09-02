DOBak V3 FLOW HOTFIX v13 — 덮어쓰기용 Assets 패키지
============================================================

기준
----
- GitHub: Koes05/dobak
- branch: codex/scenario-v3-rebuild
- checked HEAD: e231f0b913db7c07d9de84e09aeb53cd7ecc2a58
- compatible source baselines:
  - ScenarioV3Director.cs  dab1e9f7ac2c4d96b5228bcf4592f218edf30e48
  - GameFlowManager.cs     4ac54c4e174b8aa7f7525bb6548cda33ac108923
  - NotificationManager.cs ccf324e0ccd673868faa20859c4f4251aaed811b

적용 방법
---------
1. 이 ZIP을 Unity 프로젝트 루트에 푼다. ZIP 안의 Assets 폴더가 기존 Assets 폴더와 합쳐지면 된다.
2. Unity를 연다. Assets/Editor/DobakV3FlowHotfixInstaller.cs가 자동으로 실행된다.
3. Console에서 `[DOBak V13] PASS`를 확인한다.
4. 수동 확인은 Unity 메뉴 `Tools > Dobak > Validate V13 Flow Hotfix`를 누른다.

안전장치
--------
- 기존 소스의 예상 코드가 정확히 일치할 때만 패치한다.
- 세 대상 파일을 모두 메모리에서 먼저 검증한 뒤 한 번에 쓴다.
- 쓰기 전에 `Library/DobakV3HotfixBackup/` 아래에 원본을 자동 백업한다.
- 중간 쓰기 실패 시 이미 쓴 파일을 원본 바이트로 자동 복원한다.
- 일부만 적용된 상태나 예상 밖 최신 소스에서는 잘못 덮어쓰지 않고 전체 작업을 중단한다.
- 패치 상태는 `Library/DobakV3Hotfix_v13_STATUS.txt`에 기록된다.

수정 내용
---------
- 밤에 차용 연락을 다음 날로 미룬 것만으로 10시 늦잠/밤샘 처리되던 오류 수정
- 실제 도박이 07:00 하루 경계를 넘긴 경우에만 다음 날 10시 늦잠 처리
- 실제 밤샘과 차용 대기가 동시에 있으면 늦잠·일정 결과 뒤 차용 선택지가 사라지지 않도록 연결
- 주말 자동 결근/점장 메시지의 return_to_tablet 처리로 예약된 차용 장면이 삭제되던 오류 수정
- 직접 라우팅 장면이 상위 트리거 완료 콜백을 삭제하던 오류 수정
- 되감기 뒤 런타임 전용 밤샘/차용 전환 플래그가 다음 분기로 새는 문제 수정
- 즉시 결근 처리된 날도 하루 확정 시 결근 횟수가 정확히 한 번 누적되도록 수정
- 홈 일정표에서 놓친 학교/공부/알바가 `[완료]`로 표시되던 문제를 `[놓침]`으로 구분
- 알림 목록 상한에서 최신 알림이 삭제되고 데이터와 UI가 어긋나던 문제 수정

수정하지 않는 것
---------------
- ScenarioV3.csv 및 기타 시나리오 CSV
- TabletUI.unity 및 다른 씬
- 대사, 선택지, 캐릭터 성격, 장면 날짜
- 목표 금액, 알바비, 차용 금액, 도박 결과
- 엔딩 조건과 교육 메시지
- 이미지, 음원, 폰트

이 패키지는 일정관리 요소가 있는 비주얼노벨의 기존 기획을 바꾸지 않고,
이미 존재하는 흐름과 연출이 의도한 순서로 실행되게 하는 코드 핫픽스다.

주의
----
현재 제작 환경에서는 Unity Editor를 직접 실행할 수 없어 실제 플레이 모드 테스트를 수행하지 못했다.
대신 설치기가 원본 조각 일치, 전체 사전 검증, 자동 백업, 실패 시 복원, 적용 후 마커 검증을 수행한다.
