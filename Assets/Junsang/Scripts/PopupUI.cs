using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PopupUI : MonoBehaviour
{
    private AppID _appID;

    [SerializeField] private TMP_Text titleText;
    [SerializeField] private Button closeButton;

    private void Start()
    {
        closeButton?.onClick.AddListener(OnClickClose);
    }

    public void Setup(AppData data)
    {
        _appID = data.appID;
    }

    public void OnClickClose()
    {
        CloseApp();
    }

    private void CloseApp()
    {
        AppManager.Instance.CloseApp(_appID);
    }
}
