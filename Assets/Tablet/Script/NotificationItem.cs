using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;


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
        
        icon.sprite = data.icon;
        title.text = data.title;
        message.text = data.message;
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