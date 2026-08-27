using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Dobak.Manager;
using Dobak.App.Casino.Auth;

namespace Dobak.App.Casino
{
    public class CasinoUIManager : MonoBehaviour
    {
        [Header("카지노 메뉴")]
        [SerializeField] private Button menu_homeButton;
        [SerializeField] private Button menu_slotMachineButton;
        [SerializeField] private Button[] menu_rechargeButtons;

        [SerializeField] private TMP_Text casinoCashText;

        [Header("홈")]
        [SerializeField] private TMP_Text home_cashText;
        [SerializeField] private Button home_rechargeButton;

        [Header("충전")]
        [SerializeField] private Button _1DollorButton;
        [SerializeField] private Button _10DollorButton;
        [SerializeField] private Button _100DollorButton;
        [SerializeField] private Button _1000DollorButton;

        [Header("패널")]
        [SerializeField] private GameObject homePanel;
        [SerializeField] private GameObject slotMachinePanel;
        [SerializeField] private GameObject rechargePanel;

        private NotificationManager notificationManager;

        [Header("참조")]
        [SerializeField] private AuthUIController auth;

        private void OnEnable()
        {
            menu_homeButton.onClick.AddListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.AddListener(OnSlotMachineButtonClicked);

            foreach(var rechargeButton in menu_rechargeButtons)
            {
                rechargeButton.onClick.AddListener(OnRechargeButtonClicked);
            }

            _1DollorButton.onClick.AddListener(On1DollarButtonClicked);
            _10DollorButton.onClick.AddListener(On10DollarButtonClicked);
            _100DollorButton.onClick.AddListener(On100DollarButtonClicked);
            _1000DollorButton.onClick.AddListener(On1000DollarButtonClicked);

            // CoinManager가 있을 때만 연결
            if (CoinManager.Instance != null)
            {
                CoinManager.Instance.OnCasinoCashChanged += UpdateDisplay;
                UpdateDisplay(CoinManager.Instance.CasinoCash);
            }

            Init();
        }

        private void OnDisable()
        {
            if (CoinManager.Instance == null) return;
            CoinManager.Instance.OnCasinoCashChanged -= UpdateDisplay;

            menu_homeButton.onClick.RemoveListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.RemoveListener(OnSlotMachineButtonClicked);

            foreach(var rechargeButton in menu_rechargeButtons)
            {
                rechargeButton.onClick.RemoveListener(OnRechargeButtonClicked);
            }

            _1DollorButton.onClick.RemoveListener(On1DollarButtonClicked);
            _10DollorButton.onClick.RemoveListener(On10DollarButtonClicked);
            _100DollorButton.onClick.RemoveListener(On100DollarButtonClicked);
            _1000DollorButton.onClick.RemoveListener(On1000DollarButtonClicked);
        }

        private void UpdateDisplay(int casinoCash)
        {
            casinoCashText.text = $"{casinoCash} 원";
        }

        private void Init()
        {
            homePanel.SetActive(true);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
        }

        private void OnHomeButtonClicked()
        {
            Debug.Log("home");
            homePanel.SetActive(true);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
        }

        private void OnSlotMachineButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(true);
            rechargePanel.SetActive(false);
        }

        private void OnRechargeButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(true);
        }

        private void OnProfileButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
        }

        private void OnCashButtonClicked(int cash)
        {
            if (CoinManager.Instance == null)
            {
                ShowPopup("출금 실패", "은행 정보를 불러올 수 없습니다.");
                return;
            }

            if (CoinManager.Instance.TryChargeToCasino(cash, out ChargeToCasinoFailureReason failureReason))
            {
                ShowPopup("출금 완료", $"카지노에 {cash}원을 충전했습니다.");
                return;
            }

            if (failureReason == ChargeToCasinoFailureReason.InsufficientBankCash)
            {
                ShowPopup("출금 오류", "은행 잔액이 부족합니다.");
                return;
            }

            ShowPopup("출금 오류", "잘못된 출금 금액입니다.");
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

        private void On1DollarButtonClicked() => OnCashButtonClicked(1000);

        private void On10DollarButtonClicked() => OnCashButtonClicked(5000);

        private void On100DollarButtonClicked() => OnCashButtonClicked(10000);

        private void On1000DollarButtonClicked() => OnCashButtonClicked(50000);
    }
}
