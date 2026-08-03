using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class NotificationPopup : MonoBehaviour
{
    [SerializeField] private TMP_Text title;
    [SerializeField] private TMP_Text message;

    [SerializeField] private RectTransform rect;

    [SerializeField] private float moveTime = 0.25f;

    [SerializeField] private float stayTime = 2f;

    private Vector2 hidePos;
    private Vector2 showPos;

    private void Awake()
    {
        showPos = rect.anchoredPosition;
        hidePos = showPos + Vector2.up * 250;

        rect.anchoredPosition = hidePos;
    }

    public void Show(NotificationData data)
    {
        StopAllCoroutines();

        title.text = data.title;
        message.text = data.message;

        StartCoroutine(PopupRoutine());
    }

    IEnumerator PopupRoutine()
    {
        float t = 0;

        while (t < moveTime)
        {
            t += Time.deltaTime;

            rect.anchoredPosition =
                Vector2.Lerp(hidePos, showPos, t / moveTime);

            yield return null;
        }

        yield return new WaitForSeconds(stayTime);

        t = 0;

        while (t < moveTime)
        {
            t += Time.deltaTime;

            rect.anchoredPosition =
                Vector2.Lerp(showPos, hidePos, t / moveTime);

            yield return null;
        }

        rect.anchoredPosition = hidePos;
    }
}