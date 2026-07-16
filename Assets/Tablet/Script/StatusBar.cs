using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class StatusBar : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [Header("패널")]
    [SerializeField]
    private RectTransform panel;

    [Header("위치")]

    [Tooltip("패널이 닫혀있는 위치")]
    [SerializeField]
    private float closedY = 700f;

    [Tooltip("패널이 완전히 열린 위치")]
    [SerializeField]
    private float openedY = 0f;

    [Header("애니메이션")]

    [SerializeField]
    private float animationTime = 0.2f;

    private Vector2 startPointerPos;

    private float startPanelY;

    private Coroutine animationCoroutine;

    [Header("터치 영역")]
    [SerializeField]
    private Image touchAreaImage;

    [SerializeField]
    [Range(0f, 1f)]
    private float pressedAlpha = 0.35f;

    //----------------------------------------------------

    private void Start()
    {
        panel.anchoredPosition = new Vector2(0, closedY);
        // 처음에는 완전 투명
        SetTouchAreaAlpha(0f);
    }

    //----------------------------------------------------

    public void OnPointerDown(PointerEventData eventData)
    {
        // 현재 마우스 위치 저장
        startPointerPos = eventData.position;

        // 현재 패널 위치 저장
        startPanelY = panel.anchoredPosition.y;

        // 애니메이션 중이라면 중단
        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
        }

        SetTouchAreaAlpha(pressedAlpha);
    }

    //----------------------------------------------------

    public void OnDrag(PointerEventData eventData)
    {
        // 드래그 거리
        float dragAmount = eventData.position.y - startPointerPos.y;

        // 새로운 위치
        float newY = startPanelY + dragAmount;

        // 범위 제한
        newY = Mathf.Clamp(newY, openedY, closedY);

        panel.anchoredPosition = new Vector2(0, newY);

        float openPercent = 1f - (newY / closedY);

        SetTouchAreaAlpha(openPercent * pressedAlpha);
    }

    //----------------------------------------------------

    public void OnPointerUp(PointerEventData eventData)
    {
        SetTouchAreaAlpha(0f);

        float currentY = panel.anchoredPosition.y;

        // 절반 이상 열렸다면 열기
        if (currentY < closedY / 2f)
        {
            animationCoroutine = StartCoroutine(MovePanel(openedY));
        }
        else
        {
            animationCoroutine = StartCoroutine(MovePanel(closedY));
        }

    }

    //----------------------------------------------------

    IEnumerator MovePanel(float targetY)
    {
        float startY = panel.anchoredPosition.y;

        float time = 0;

        while (time < animationTime)
        {
            time += Time.deltaTime;

            float t = time / animationTime;

            float y = Mathf.Lerp(startY, targetY, t);

            panel.anchoredPosition = new Vector2(0, y);

            float openPercent = 1f - (y / closedY);

            SetTouchAreaAlpha(openPercent * pressedAlpha);

            yield return null;
        }

        panel.anchoredPosition = new Vector2(0, targetY);
        
        if (targetY == openedY)
            SetTouchAreaAlpha(pressedAlpha);
        else
            SetTouchAreaAlpha(0f);
    }

    private void SetTouchAreaAlpha(float alpha)
    {
        Color color = touchAreaImage.color;
        color.a = alpha;
        touchAreaImage.color = color;
    }
    
}

