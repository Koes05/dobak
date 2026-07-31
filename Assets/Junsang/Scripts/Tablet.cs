using Dobak.App;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.Tablet
{
    public class Tablet : MonoBehaviour
    {
        [SerializeField] private Button homeButton;
        [SerializeField] private Transform openedAppParent;

        private void Awake()
        {
            homeButton.onClick.AddListener(OnHomeButtonClicked);
        }

        private void OnDestroy()
        {
            homeButton.onClick.RemoveListener(OnHomeButtonClicked);
        }

        private void OnHomeButtonClicked()
        {
            PopupUI openedApp = openedAppParent?.GetChild(0)?.GetComponent<PopupUI>();
            openedApp.CloseApp();
        }
    }
}
