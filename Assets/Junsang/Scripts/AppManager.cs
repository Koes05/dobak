using System.Collections.Generic;
using UnityEngine;

public class AppManager : MonoBehaviour
{
    public static AppManager Instance {get; private set;}

    private Dictionary<AppID, GameObject> _activeApps = new ();

    [SerializeField] private Transform popupAppsParent;

    private void Awake()
    {
        Instance = this;
    }

    public void OpenApp(AppData data)
    {
        if (_activeApps.ContainsKey(data.appID))
        {
            _activeApps[data.appID].transform.SetAsLastSibling();
            return;
        }

        GameObject newPopup = Instantiate(data.appPrefab, popupAppsParent);
        _activeApps.Add(data.appID, newPopup);
        newPopup.GetComponent<PopupUI>().Setup(data);
        newPopup.transform.SetAsLastSibling();
    }

    public void CloseApp(AppID id)
    {
        if (_activeApps.ContainsKey(id))
        {
            Destroy(_activeApps[id]);
            _activeApps.Remove(id);
        }
    }
}
