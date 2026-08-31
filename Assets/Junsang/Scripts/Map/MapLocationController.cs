using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Dobak.App.Map
{
    public class MapLocationController : MonoBehaviour
    {
        [SerializeField] private RectTransform marker; // 표시할 마커(핀) UI

        [SerializeField] private AppWindow appWindow;

        [Header("현재 위치 텍스트")]
        [SerializeField] private TMP_Text currentLocationText;

        [System.Serializable]
        public class LocationPoint
        {
            public string locationName;
            public Button button;
            public RectTransform point; // 이 버튼(또는 별도 좌표)의 위치
        }

        [SerializeField] private LocationPoint[] locations; //장소 목록

        private void Start()
        {
            marker.gameObject.SetActive(false);

            currentLocationText.text = "현재 위치 : 선택되지 않음";

            foreach (var loc in locations)
            {
                // 클로저 문제 방지를 위해 지역 변수로 복사
                var target = loc;
                loc.button.onClick.AddListener(() => ShowMarker(target));
            }
        }

        private void ShowMarker(LocationPoint loc)
        {
            marker.gameObject.SetActive(true);
            marker.anchoredPosition = loc.point.anchoredPosition;

            string displayName = GetDisplayName(loc.locationName);
            currentLocationText.text = $"이동할 장소 : {displayName}";

            Debug.Log($"{displayName} 위치 선택됨");

            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.TravelTo(displayName);
            else
                appWindow.CloseCurrentApp();
        }

        private static string GetDisplayName(string value)
        {
            switch (value)
            {
                case "1": return "학교";
                case "2": return "카페";
                case "3": return "집";
                default: return value;
            }
        }
    }
}
