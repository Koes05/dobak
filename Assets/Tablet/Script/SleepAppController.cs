using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class SleepAppController : MonoBehaviour
{
    [SerializeField] private Button sleepButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private AppWindow appWindow;

    private void Awake()
    {
        Transform title = transform.Find("Sleep Title");
        if (title != null)
            title.gameObject.SetActive(false);

        RectTransform artwork = transform.Find("Sleep Screen Artwork") as RectTransform;
        if (artwork != null)
        {
            artwork.anchorMin = new Vector2(0.015f, 0.01f);
            artwork.anchorMax = new Vector2(0.985f, 0.99f);
            artwork.offsetMin = artwork.offsetMax = Vector2.zero;
        }

        if (sleepButton != null)
        {
            sleepButton.onClick.RemoveListener(SleepNow);
            sleepButton.onClick.AddListener(SleepNow);
        }
    }

    private void OnEnable()
    {
        RefreshStatus();
    }

    private void OnDestroy()
    {
        if (sleepButton != null)
            sleepButton.onClick.RemoveListener(SleepNow);
    }

    public void SleepNow()
    {
        GameFlowManager flow = GameFlowManager.Instance;
        if (flow == null)
            return;

        if (flow.CurrentLocation != "집")
        {
            if (statusText != null)
                statusText.text = "집으로 돌아온 뒤에 잘 수 있다.";
            return;
        }

        if (!flow.CanSleepNow)
        {
            if (statusText != null)
                statusText.text = "아직 잘 시간이 아니다.";
            return;
        }

        appWindow?.CloseCurrentApp();
        flow.Sleep();
    }

    private void RefreshStatus()
    {
        if (statusText == null)
            return;

        GameFlowManager flow = GameFlowManager.Instance;
        if (flow == null)
        {
            statusText.text = "오늘 하루를 마무리한다.";
            return;
        }

        bool atHome = flow.CurrentLocation == "집";
        bool canSleep = flow.CanSleepNow;
        statusText.text = !atHome
            ? "집으로 돌아온 뒤에 잘 수 있다."
            : canSleep ? "주무시겠습니까?" : "아직 잘 시간이 아니다.";
        if (sleepButton != null)
            sleepButton.interactable = canSleep;
    }
}
