using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Linq;

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

        private bool listenersBound;

        private void Start()
        {
            ApplyVisualAssets();
            BindLocationButtons();
            RefreshMap();
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
                RefreshMap();
        }

        private void BindLocationButtons()
        {
            if (listenersBound)
                return;

            listenersBound = true;
            foreach (LocationPoint loc in locations)
            {
                LocationPoint target = loc;
                loc.button.onClick.AddListener(() => ShowMarker(target));
                ConfigureLocationLabel(loc);
            }
        }

        private void ApplyVisualAssets()
        {
            Sprite markerSprite = Resources.LoadAll<Sprite>("Map/2288553").FirstOrDefault();
            if (markerSprite != null && marker != null)
            {
                Image markerImage = marker.GetComponent<Image>();
                if (markerImage != null)
                {
                    markerImage.sprite = markerSprite;
                    markerImage.color = Color.white;
                    markerImage.preserveAspect = true;
                    marker.sizeDelta = new Vector2(46f, 46f);
                }
            }

            Sprite[] sprites = Resources.LoadAll<Sprite>("Map/Code_Generated_Image (1)")
                .OrderBy(sprite => sprite.name, StringComparer.Ordinal)
                .ToArray();
            if (sprites.Length < 3)
            {
                Debug.LogWarning("MapLocationController: 장소 이미지 리소스를 불러오지 못했습니다.");
                return;
            }

            foreach (LocationPoint loc in locations)
            {
                if (loc?.button == null)
                    continue;

                int spriteIndex = loc.locationName switch
                {
                    "1" => 1,
                    "2" => 2,
                    "3" => 0,
                    _ => 0
                };

                Image image = loc.button.targetGraphic as Image ?? loc.button.GetComponent<Image>();
                if (image == null)
                    continue;

                image.sprite = sprites[spriteIndex];
                image.color = Color.white;
                image.preserveAspect = true;
                loc.button.targetGraphic = image;
                loc.button.GetComponent<RectTransform>().sizeDelta = new Vector2(170f, 170f);
            }
        }

        private void RefreshMap()
        {
            if (marker == null || locations == null)
                return;

            string current = GameFlowManager.Instance != null
                ? GameFlowManager.Instance.CurrentLocation
                : "집";

            marker.gameObject.SetActive(false);
            foreach (LocationPoint loc in locations)
            {
                ConfigureLocationLabel(loc);
                if (GetDisplayName(loc.locationName) != current)
                    continue;

                marker.gameObject.SetActive(true);
                marker.anchoredPosition = loc.point.anchoredPosition + new Vector2(32f, 38f);
            }

            if (currentLocationText != null)
                currentLocationText.text = $"현재 위치 : {current}";
        }

        private void ShowMarker(LocationPoint loc)
        {
            marker.gameObject.SetActive(true);
            marker.anchoredPosition = loc.point.anchoredPosition + new Vector2(32f, 38f);

            string displayName = GetDisplayName(loc.locationName);
            int travelHours = GameFlowManager.Instance != null
                ? GameFlowManager.Instance.GetTravelHours(displayName)
                : 1;

            if (currentLocationText != null)
            {
                currentLocationText.text = travelHours == 0
                    ? $"현재 위치 : {displayName}"
                    : $"{displayName}까지 예상 이동시간 : 약 {travelHours}시간";
            }

            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.TravelTo(displayName);
            else if (appWindow != null)
                appWindow.CloseCurrentApp();
        }

        private void ConfigureLocationLabel(LocationPoint loc)
        {
            if (loc == null || loc.button == null)
                return;

            TMP_Text label = loc.button.transform.Find("Travel Info")?.GetComponent<TMP_Text>();
            if (label == null)
            {
                GameObject labelObject = new GameObject("Travel Info", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                labelObject.layer = loc.button.gameObject.layer;
                labelObject.transform.SetParent(loc.button.transform, false);
                label = labelObject.GetComponent<TextMeshProUGUI>();

                RectTransform rect = label.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 0f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0f, -8f);
                rect.sizeDelta = new Vector2(230f, 58f);
            }

            string displayName = GetDisplayName(loc.locationName);
            int travelHours = GameFlowManager.Instance != null
                ? GameFlowManager.Instance.GetTravelHours(displayName)
                : 1;

            label.font = currentLocationText != null ? currentLocationText.font : label.font;
            label.fontSize = 20f;
            label.alignment = TextAlignmentOptions.Top;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = travelHours == 0
                ? $"{displayName}\n현재 위치"
                : $"{displayName}\n예상 {travelHours}시간";
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
