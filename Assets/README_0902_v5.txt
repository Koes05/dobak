0902 v5 - 요청한 3개 우선 수정

1) 초반 공부 앱 잠금
- 엄마 첫 메시지와 민재 첫 메시지를 실제 채팅방에서 확인하기 전에는 공부 앱 진입 불가.
- 메시지 앱에 유도 점을 표시.
- 두 메시지를 확인한 뒤 기존 일정 조건에 따라 공부 앱 사용 가능.

2) 공부 선택지 텍스트 위치
- 파란 번호 타원과 실제 문장 영역을 RectTransform으로 완전히 분리.
- 문장은 번호 영역 오른쪽에서 시작하므로 1333/1336/긴 문장이 타원과 겹치지 않음.

3) Stranger/Scammer 제거
- 구형 씬의 Stranger_Profile / Scammer_Profile GameObject 자체를 런타임에서 비활성화.
- 텍스트만 숨기던 이전 처리보다 강하게 적용.

4) 대화 기록 영역
- 배치된 History Text가 잘못된 부모에 있어도 History Content 밑으로 재배치.
- History Viewport에 RectMask2D + ScrollRect를 강제 구성.
- ContentSizeFitter 기반 세로 스크롤로 중앙 영역 밖 텍스트 노출 방지.

적용: Unity 종료 -> 프로젝트 최상위에 Assets 폴더 덮어쓰기.
