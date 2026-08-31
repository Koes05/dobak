using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Dobak.Manager;
namespace Dobak.App.Casino
{
    public class CasinoUIManager : MonoBehaviour
    {
        [Header("카지노 메뉴")]
        [SerializeField] private Button menu_homeButton;
        [SerializeField] private Button menu_slotMachineButton;
        [SerializeField] private Button menu_rechargeButton;
        [SerializeField] private Button menu_myPageButton;
        [SerializeField] private TMP_Text casinoCashText;

        [Header("홈")]
        [SerializeField] private TMP_Text home_cashText;
        [SerializeField] private Button home_rechargeButton;

        [Header("충전")]
        [SerializeField] private Button _1DollorButton;
        [SerializeField] private Button _10DollorButton;
        [SerializeField] private Button _100DollorButton;
        [SerializeField] private Button _1000DollorButton;
        [SerializeField] private Button _10000DollorButton;
        [SerializeField] private Button _100000DollorButton;

        [Header("패널")]
        [SerializeField] private GameObject homePanel;
        [SerializeField] private GameObject slotMachinePanel;
        [SerializeField] private GameObject rechargePanel;
        [SerializeField] private GameObject profilePanel;

        private NotificationManager notificationManager;
        private TMP_Text homePromotionText;
        private TMP_Text rechargeStatusText;
        private RawImage siteBackgroundImage;
        private RawImage siteBannerImage;
        private RawImage rechargeArtImage;
        private readonly Button[] chargeButtons = new Button[4];
        private static readonly int[] ChargeWonOptions = { 5000, 10000, 50000, 100000 };

        private void OnEnable()
        {
            menu_homeButton.onClick.AddListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.AddListener(OnSlotMachineButtonClicked);
            menu_rechargeButton.onClick.AddListener(OnRechargeButtonClicked);
            menu_myPageButton.onClick.AddListener(OnCashOutButtonClicked);

            _1DollorButton.onClick.AddListener(On1DollarButtonClicked);
            _10DollorButton.onClick.AddListener(On10DollarButtonClicked);
            _100DollorButton.onClick.AddListener(On100DollarButtonClicked);
            _1000DollorButton.onClick.AddListener(On1000DollarButtonClicked);
            _10000DollorButton.onClick.AddListener(On10000DollarButtonClicked);
            _100000DollorButton.onClick.AddListener(On100000DollarButtonClicked);

            // CoinManager가 있을 때만 연결
            if (CoinManager.Instance != null)
            {
                CoinManager.Instance.OnCasinoCashChanged += UpdateDisplay;
                UpdateDisplay(CoinManager.Instance.CasinoCash);
            }

            Init();
            ConfigureMenuLabels();
            EnsureHomeContent();
            ConfigureChargeOptions();
        }

        private void OnDisable()
        {
            if (CoinManager.Instance == null) return;
            CoinManager.Instance.OnCasinoCashChanged -= UpdateDisplay;

            menu_homeButton.onClick.RemoveListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.RemoveListener(OnSlotMachineButtonClicked);
            menu_rechargeButton.onClick.RemoveListener(OnRechargeButtonClicked);
            menu_myPageButton.onClick.RemoveListener(OnCashOutButtonClicked);
            _1DollorButton.onClick.RemoveListener(On1DollarButtonClicked);
            _10DollorButton.onClick.RemoveListener(On10DollarButtonClicked);
            _100DollorButton.onClick.RemoveListener(On100DollarButtonClicked);
            _1000DollorButton.onClick.RemoveListener(On1000DollarButtonClicked);
            _10000DollorButton.onClick.RemoveListener(On10000DollarButtonClicked);
            _100000DollorButton.onClick.RemoveListener(On100000DollarButtonClicked);
        }

        private void UpdateDisplay(int casinoCash)
        {
            casinoCashText.text = $"사이트 포인트  {casinoCash:N0}P";
            if (home_cashText != null)
                home_cashText.text = $"보유 포인트  {casinoCash:N0}P";

            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (text != null && !ReferenceEquals(text, casinoCashText) && !ReferenceEquals(text, home_cashText) &&
                    (text.text?.Contains("Cash:") == true || text.text?.Contains('$') == true))
                    text.text = $"사이트 포인트  {casinoCash:N0}P";
            }
        }

        private void Init()
        {
            Transform legacyAuth = transform.Find("Auth");
            if (legacyAuth != null)
                legacyAuth.gameObject.SetActive(false);

            homePanel.SetActive(true);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
            profilePanel.SetActive(false);

            // 계정 화면 대신 예방 시나리오의 환전 시도를 제공한다.
            if (menu_myPageButton != null)
                menu_myPageButton.gameObject.SetActive(true);
        }

        private void EnsureHomeContent()
        {
            if (homePanel == null || homePromotionText != null)
                return;

            GameObject message = new GameObject("Site Promotion", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            message.transform.SetParent(homePanel.transform, false);
            homePromotionText = message.GetComponent<TextMeshProUGUI>();
            homePromotionText.font = casinoCashText.font;
            homePromotionText.fontSize = 42f;
            homePromotionText.color = Color.white;
            homePromotionText.alignment = TextAlignmentOptions.Center;
            homePromotionText.text = "첫 이용 보너스 지급 완료\n\n지금 시작하면 추가 포인트를 받을 수 있습니다";

            RectTransform rect = homePromotionText.rectTransform;
            rect.anchorMin = new Vector2(0.12f, 0.18f);
            rect.anchorMax = new Vector2(0.88f, 0.55f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            siteBackgroundImage = CreateArt("Site Pattern", homePanel.transform,
                "TestAssets/Gemini_Generated_Image_iob66iiob66iiob6", Vector2.zero, Vector2.one);
            if (siteBackgroundImage != null)
            {
                siteBackgroundImage.transform.SetAsFirstSibling();
                siteBackgroundImage.color = new Color(1f, 1f, 1f, 0.7f);
            }

            siteBannerImage = CreateArt("Site Banner", homePanel.transform,
                "TestAssets/Gemini_Generated_Image_hfrgu0hfrgu0hfrg", new Vector2(0.11f, 0.62f), new Vector2(0.89f, 0.84f));
            if (siteBannerImage != null)
                siteBannerImage.transform.SetAsLastSibling();

            rechargeArtImage = CreateArt("Recharge Promotion", rechargePanel.transform,
                "TestAssets/Gemini_Generated_Image_rh9r09rh9r09rh9r", new Vector2(0.08f, 0.6f), new Vector2(0.92f, 0.87f));
        }

        private static RawImage CreateArt(string name, Transform parent, string resourcePath, Vector2 anchorMin, Vector2 anchorMax)
        {
            Texture2D texture = Resources.Load<Texture2D>(resourcePath);
            if (texture == null || parent == null)
                return null;

            GameObject art = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            art.layer = parent.gameObject.layer;
            art.transform.SetParent(parent, false);
            RectTransform rect = art.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            RawImage image = art.GetComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }

        private void OnHomeButtonClicked()
        {
            Debug.Log("home");
            homePanel.SetActive(true);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
            profilePanel.SetActive(false);
        }

        private void OnSlotMachineButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(true);
            rechargePanel.SetActive(false);
            profilePanel.SetActive(false);
        }

        private void OnRechargeButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(true);
            profilePanel.SetActive(false);
        }

        private void OnCashOutButtonClicked()
        {
            GameFlowManager.Instance?.AttemptCashOut();
        }

        private void ConfigureMenuLabels()
        {
            SetButtonLabel(menu_homeButton, "홈");
            SetButtonLabel(menu_slotMachineButton, "게임");
            SetButtonLabel(menu_rechargeButton, "충전");
            SetButtonLabel(menu_myPageButton, "환전");
        }

        private static void SetButtonLabel(Button button, string value)
        {
            if (button == null)
                return;

            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label == null)
                return;

            label.text = value;
            label.fontSize = 28f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 20f;
            label.fontSizeMax = 28f;
            label.alignment = TextAlignmentOptions.Center;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }

        private void OnCashButtonClicked(int won)
        {
            if (CoinManager.Instance == null)
            {
                ShowPopup("출금 실패", "은행 정보를 불러올 수 없습니다.");
                return;
            }

            if (CoinManager.Instance.TryChargeToCasino(won, out ChargeToCasinoFailureReason failureReason))
            {
                int points = CoinManager.ConvertWonToPoints(won);
                ShowRechargeStatus($"{won:N0}원 충전 완료  +{points:N0}P", false);
                return;
            }

            if (failureReason == ChargeToCasinoFailureReason.InsufficientBankCash)
            {
                ShowRechargeStatus("통장 잔액이 부족합니다.", true);
                return;
            }

            ShowRechargeStatus("선택할 수 없는 충전 금액입니다.", true);
        }

        private void ConfigureChargeOptions()
        {
            chargeButtons[0] = _1DollorButton;
            chargeButtons[1] = _10DollorButton;
            chargeButtons[2] = _100DollorButton;
            chargeButtons[3] = _1000DollorButton;

            for (int i = 0; i < chargeButtons.Length; i++)
            {
                Button button = chargeButtons[i];
                if (button == null)
                    continue;

                int won = ChargeWonOptions[i];
                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.text = $"{won:N0}원\n{CoinManager.ConvertWonToPoints(won):N0}P";
                    label.fontSize = 30f;
                    label.enableAutoSizing = true;
                    label.fontSizeMin = 22f;
                    label.fontSizeMax = 30f;
                    label.alignment = TextAlignmentOptions.Center;
                    label.lineSpacing = -8f;
                    label.overflowMode = TextOverflowModes.Ellipsis;
                    RectTransform labelRect = label.rectTransform;
                    labelRect.anchorMin = Vector2.zero;
                    labelRect.anchorMax = Vector2.one;
                    labelRect.offsetMin = new Vector2(12f, 8f);
                    labelRect.offsetMax = new Vector2(-12f, -8f);
                }

                RectTransform buttonRect = button.GetComponent<RectTransform>();
                if (buttonRect != null)
                {
                    float x = i % 2 == 0 ? 0.36f : 0.68f;
                    float y = i < 2 ? 0.35f : 0.2f;
                    buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(x, y);
                    buttonRect.pivot = new Vector2(0.5f, 0.5f);
                    buttonRect.anchoredPosition = Vector2.zero;
                    buttonRect.sizeDelta = new Vector2(360f, 118f);
                }
            }

            if (_10000DollorButton != null)
                _10000DollorButton.gameObject.SetActive(false);
            if (_100000DollorButton != null)
                _100000DollorButton.gameObject.SetActive(false);

            if (rechargeStatusText == null && rechargePanel != null)
            {
                GameObject status = new GameObject("Recharge Status", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                status.transform.SetParent(rechargePanel.transform, false);
                rechargeStatusText = status.GetComponent<TextMeshProUGUI>();
                rechargeStatusText.font = casinoCashText.font;
                rechargeStatusText.fontSize = 30f;
                rechargeStatusText.alignment = TextAlignmentOptions.Center;
                rechargeStatusText.color = new Color(0.12f, 0.18f, 0.28f);
                RectTransform rect = rechargeStatusText.rectTransform;
                rect.anchorMin = new Vector2(0.15f, 0.08f);
                rect.anchorMax = new Vector2(0.85f, 0.18f);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
            }
        }

        private void ShowRechargeStatus(string message, bool error)
        {
            if (rechargeStatusText == null)
                ConfigureChargeOptions();
            if (rechargeStatusText == null)
                return;

            rechargeStatusText.text = message;
            rechargeStatusText.color = error ? new Color(0.72f, 0.18f, 0.2f) : new Color(0.08f, 0.48f, 0.3f);
        }

        private void ShowPopup(string title, string message)
        {
            NotificationData data = new NotificationData
            {
                title = title,
                message = message,
                appType = AppType.Bank
            };

            if (notificationManager == null)
            {
                notificationManager = FindAnyObjectByType<NotificationManager>(FindObjectsInactive.Include);
            }

            if (notificationManager != null)
            {
                notificationManager.SendNotification(data);
                return;
            }

            Debug.LogWarning($"{nameof(CasinoUIManager)} could not find a NotificationManager.");
        }

        private void On1DollarButtonClicked() => OnCashButtonClicked(5000);

        private void On10DollarButtonClicked() => OnCashButtonClicked(10000);

        private void On100DollarButtonClicked() => OnCashButtonClicked(50000);

        private void On1000DollarButtonClicked() => OnCashButtonClicked(100000);

        private void On10000DollarButtonClicked() => OnCashButtonClicked(10000);

        private void On100000DollarButtonClicked() => OnCashButtonClicked(100000);
    }
}
