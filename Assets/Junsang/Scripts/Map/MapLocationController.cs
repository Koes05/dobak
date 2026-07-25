using UnityEngine;
using UnityEngine.UI;

namespace Dobak.App.Map
{
    public class MapLocationController : MonoBehaviour
    {
        [SerializeField] private RectTransform marker; // 표시할 마커(핀) UI

        [System.Serializable]
        public class LocationPoint
        {
            public string locationName;
            public Button button;
            public RectTransform point; // 이 버튼(또는 별도 좌표)의 위치
        }

        [SerializeField] private LocationPoint[] locations;

        private void Start()
        {
            marker.gameObject.SetActive(false);

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

            Debug.Log($"{loc.locationName} 위치 선택됨");
        }
    }
}
