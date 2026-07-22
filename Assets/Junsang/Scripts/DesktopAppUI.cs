using UnityEngine;
using UnityEngine.UI;

public class DesktopAppUI : MonoBehaviour
{
    [SerializeField] private AppData data;
    [SerializeField] private Button button;

    private void Awake()
    {
        if(button is null) button = GetComponent<Button>();
    }

    private void Start()
    {
        button.onClick.AddListener(delegate { OnClickOpen(); });
    }

    private void OnClickOpen()
    {
        AppManager.Instance.OpenApp(data);
    }
}
