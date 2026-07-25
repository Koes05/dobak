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
    
    [SerializeField]
    private Sprite testIcon;

    [SerializeField]
    private int maxNotificationCount = 20;

    // 저장된 알림
    private List<NotificationData> notifications = new List<NotificationData>();

    private void Update()
    {
        // 스페이스바 테스트
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            TestNotification();
        }
    }

    public void OpenNotification(NotificationData data)
    {
        appWindow.OpenApp(data.appType);
    }

    /// <summary>
    /// 테스트용 알림
    /// </summary>
    private void TestNotification()
    {
        NotificationData data = new NotificationData();

        //data.icon = bankIcon;

        data.title = "메시지";

        data.message = "엄마 : 밥 먹었니?";

        data.appType = AppType.Message;

        SendNotification(data);
    }

    /// <summary>
    /// 새로운 알림 생성
    /// </summary>
    public void SendNotification(NotificationData data)
    {
        // 리스트 저장
        notifications.Add(data);

        // 팝업 표시
        popup.Show(data);

        // 패널에도 추가
        NotificationItem item = Instantiate(itemPrefab, content);
        
        if (content.childCount >= maxNotificationCount)
        {
            Destroy(content.GetChild(content.childCount - 1).gameObject);
        }

        item.transform.SetAsFirstSibling();

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