using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.App
{
    public class PopupUI : MonoBehaviour
    {
        private AppID _appID;

        public void Setup(AppData data)
        {
            _appID = data.appID;
        }

        public void OnClickClose()
        {
            CloseApp();
        }

        public void CloseApp()
        {
            AppManager.Instance.CloseApp(_appID);
        }
    }
}
