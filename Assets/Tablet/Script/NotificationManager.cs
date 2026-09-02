using System.Collections.Generic;
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
    private int maxNotificationCount = 20;

    // 저장된 알림
    private List<NotificationData> notifications = new List<NotificationData>();

    private void Awake()
    {
        if (dialogueManager == null)
            dialogueManager = FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
        if (appWindow == null)
            appWindow = FindAnyObjectByType<AppWindow>(FindObjectsInactive.Include);
    }

    public void OpenNotification(NotificationData data)
    {
        if (data == null)
            return;

        if (data.appType == AppType.Message)
        {
            // 메시지 앱이 이미 열려 있으면 OpenApp의 same-app guard 때문에
            // PreferConversation만 남고 실제 대화방 전환이 일어나지 않았다.
            if (appWindow != null && appWindow.CurrentAppType == AppType.Message)
            {
                dialogueManager?.OpenDialogue(data.speakerType);
                return;
            }

            dialogueManager?.PreferConversation(data.speakerType);
        }
        appWindow?.OpenApp(data.appType);
    }

    /// <summary>
    /// 새로운 알림 생성
    /// </summary>
    public void SendNotification(NotificationData data)
    {
        // 리스트 저장
        notifications.Add(data);
        if (notifications.Count > maxNotificationCount)
            notifications.RemoveAt(0);
        GameFlowManager.Instance?.V3MarkAppAttention(data.appType);


        if (data.appType == AppType.Message)
        {
            // DialogueManager가 존재하는지 확인
            if (dialogueManager != null)
            {
                dialogueManager.ReceiveNotificationMessage(data.speakerType, data.title, data.message);
            }
        }

        // 팝업 표시
        popup?.Show(data);

        // 패널에도 추가
        if (itemPrefab == null || content == null)
            return;

        NotificationItem item = Instantiate(itemPrefab, content);

        // 새로 추가된 알림을 먼저 맨 위로 이동한다.
        item.transform.SetAsFirstSibling();

        // DOBak V13-N01: 최대 개수에 도달했을 때 방금 만든 최신 알림이 아니라 가장 오래된 항목을 제거한다.
        int visibleLimit = Mathf.Max(1, maxNotificationCount);
        for (int index = content.childCount - 1; index >= visibleLimit; index--)
            Destroy(content.GetChild(index).gameObject);

        // 데이터 연결
        item.SetData(data, this);
    }

    /// <summary>
    /// 모든 알림 삭제
    /// </summary>
    public void Clear()
    {
        notifications.Clear();
        popup?.HideImmediately();

        if (content == null)
            return;

        foreach (Transform child in content)
        {
            Destroy(child.gameObject);
        }
    }

    public void ClearMessageNotifications(SpeakerType speaker)
    {
        notifications.RemoveAll(data =>
            data != null && data.appType == AppType.Message && data.speakerType == speaker);
        popup?.HideImmediately();

        if (content != null)
        {
            foreach (Transform child in content)
            {
                NotificationItem item = child.GetComponent<NotificationItem>();
                if (item != null && item.MatchesMessageSpeaker(speaker))
                    Destroy(child.gameObject);
            }
        }

        bool hasUnreadMessageNotification = notifications.Exists(data =>
            data != null && data.appType == AppType.Message);
        if (!hasUnreadMessageNotification)
            GameFlowManager.Instance?.V3ClearAppAttention(AppType.Message);
    }

    public void DismissNotification(NotificationData data)
    {
        notifications.Remove(data);
    }

    public void HidePopup()
    {
        popup?.HideImmediately();
    }
}
