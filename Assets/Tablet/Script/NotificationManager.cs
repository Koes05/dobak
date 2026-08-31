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

    public void OpenNotification(NotificationData data)
    {
        appWindow.OpenApp(data.appType);
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
        popup.HideImmediately();

        foreach (Transform child in content)
        {
            Destroy(child.gameObject);
        }
    }
}
