using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

[System.Serializable]
public class AppIconData
{
    [Header("앱 타입")]
    public AppType appType;

    [Header("앱 아이콘")]
    public Sprite icon;
}

public class NotificationItem : MonoBehaviour,
    IBeginDragHandler,
    IDragHandler,
    IEndDragHandler,
    IPointerClickHandler
{
    [Header("UI")]
    [SerializeField] private Image icon;
    [SerializeField] private TMP_Text title;
    [SerializeField] private TMP_Text message;

    [Header("앱 아이콘 매핑")]
    [SerializeField] private AppIconData[] appIcons;

    [Header("삭제 거리")]
    [SerializeField] private float removeDistance = 250f;

    private NotificationData data;
    private NotificationManager manager;

     // 자기 자신의 RectTransform
    private RectTransform rect;

    // 처음 위치
    private Vector2 startPos;

    //------------------------------------------------

    private void Awake()
    {
        rect = GetComponent<RectTransform>();

        startPos = rect.anchoredPosition;
    }

    //------------------------------------------------

    public void SetData(NotificationData notificationData, NotificationManager notificationManager)
    {
        data = notificationData;
        manager = notificationManager;
        
        SetAppIcon(data.appType);
        title.text = data.title;
        message.text = data.message;
    }

    private void SetAppIcon(AppType appType)
    {
        if (data != null && data.icon != null)
        {
            icon.sprite = data.icon;
            return;
        }

        if (appIcons == null)
        {
            Debug.LogWarning("NotificationItem: appIcons 배열이 비어 있습니다.");
            return;
        }

        foreach (AppIconData appIcon in appIcons)
        {
            if (appIcon != null && appIcon.appType == appType)
            {
                icon.sprite = appIcon.icon;
                return;
            }
        }

        Debug.LogWarning(
            $"NotificationItem: {appType}에 해당하는 아이콘을 찾을 수 없습니다.");
    }

    //------------------------------------------------
    // 드래그 시작
    //------------------------------------------------

    public void OnBeginDrag(PointerEventData eventData)
    {
        startPos = rect.anchoredPosition;
    }

    //------------------------------------------------
    // 드래그 중
    //------------------------------------------------

    public void OnDrag(PointerEventData eventData)
    {
        // X축만 이동
        rect.anchoredPosition +=
            new Vector2(eventData.delta.x, 0);
    }

    //------------------------------------------------
    // 손을 뗐을 때
    //------------------------------------------------

    public void OnEndDrag(PointerEventData eventData)
    {
        float distance =
            Mathf.Abs(rect.anchoredPosition.x);

        // 충분히 밀었으면 삭제
        if (distance > removeDistance)
        {
            Destroy(gameObject);
        }
        else
        {
            // 원래 위치로 복귀
            rect.anchoredPosition = startPos;
        }
    }

    //------------------------------------------------
    // 클릭하면 앱 들어가기
    //------------------------------------------------
    public void OnPointerClick(PointerEventData eventData)
    {
        manager.OpenNotification(data);

        Destroy(gameObject);
    }
}