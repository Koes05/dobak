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
 
    [SerializeField]
    private Sprite testIcon;

    [SerializeField]
    private int maxNotificationCount = 20;

    // 저장된 알림
    private List<NotificationData> notifications =
        new List<NotificationData>();

    private void Update()
    {
        // 스페이스바 테스트
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            TestNotification();
        }
    }

    /// <summary>
    /// 테스트용 알림
    /// </summary>
    private void TestNotification()
    {
        NotificationData data = new NotificationData();

        data.icon = testIcon;      // Inspector에서 연결
        data.title = "국민은행";
        data.message = "500,000원이 출금되었습니다.";

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

        item.SetData(data);
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