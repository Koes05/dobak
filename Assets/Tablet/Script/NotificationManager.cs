using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine;

public class NotificationManager : MonoBehaviour
{
    [Header("Popup")]
    [SerializeField] private NotificationPopup popup;

    [Header("알림 목록 부모")]
    [SerializeField] private Transform content;

    [Header("알림 프리팹")]
    [SerializeField] private NotificationItem itemPrefab;
 
    [SerializeField] private AppWindow appWindow;

    [SerializeField] private DialogueManager dialogueManager;   // 대화 관리자를 통해 발신자 타입을 확인하고, 알림에 표시할 수 있음
    
    [SerializeField]
    private Sprite testIcon;

    [SerializeField]
    private int maxNotificationCount = 20;

    // 저장된 알림
    private List<NotificationData> notifications = new List<NotificationData>();

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame)
            TestNotification(1);

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
            TestNotification(2);

        if (Keyboard.current.digit3Key.wasPressedThisFrame)
            TestNotification(3);

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
            TestNotification(4);
    }

    public void OpenNotification(NotificationData data)
    {
        appWindow.OpenApp(data.appType);
    }

    /// <summary>
    /// 테스트용 알림
    /// </summary>
    private void TestNotification(int testNumber)
    {
        NotificationData data = new NotificationData();

        switch (testNumber)
        {
        // 1번 : 엄마 메시지
        case 1:
            data.title = "메시지";
            data.message = "엄마 : 밥 먹었니?";
            data.appType = AppType.Message;
            data.speakerType = SpeakerType.Mom;
            break;

        // 2번 : 친구 메시지
        case 2:
            data.title = "메시지";
            data.message = "동창 친구 : 야 오랜만이다!";
            data.appType = AppType.Message;
            data.speakerType = SpeakerType.Friend;
            break;

        // 3번 : 익명 메시지
        case 3:
            data.title = "메시지";
            data.message = "익명 소모임 : 안녕하세요!";
            data.appType = AppType.Message;
            data.speakerType = SpeakerType.Stranger;
            break;

        // 4번 : 사기꾼 메시지
        case 4:
            data.title = "메시지";
            data.message = "김실장 : 오늘 추천주 있습니다.";
            data.appType = AppType.Message;
            data.speakerType = SpeakerType.Scammer;
            break;

        default:
            Debug.LogWarning($"존재하지 않는 테스트 번호 : {testNumber}");
            return;
        }

        SendNotification(data);
    }

    /// <summary>
    /// 새로운 알림 생성
    /// </summary>
    public void SendNotification(NotificationData data)
    {
        // 리스트 저장
        notifications.Add(data);


        if (data.appType == AppType.Message)
        {
            // DialogueManager가 존재하는지 확인
            if (dialogueManager != null)
            {
                dialogueManager.ReceiveNotificationMessage(data.speakerType, data.title, data.message);
            }
        }

        // 팝업 표시
        popup.Show(data);

        // 패널에도 추가
        NotificationItem item = Instantiate(itemPrefab, content);
        
        if (content.childCount >= maxNotificationCount)
        {
            Destroy(content.GetChild(content.childCount - 1).gameObject);
        }

        // 새로 추가된 알림을 맨 위로 이동
        item.transform.SetAsFirstSibling();

        // 데이터 연결 
        item.SetData(data, this);
    }

    /// <summary>
    /// 모든 알림 삭제
    /// </summary>
    public void Clear()
    {
        notifications.Clear();

        foreach (Transform child in content)
        {
            Destroy(child.gameObject);
        }
    }
}
