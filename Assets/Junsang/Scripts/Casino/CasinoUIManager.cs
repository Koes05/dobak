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

        [Header("참조")]
        [SerializeField] private AuthUIController auth;

        private void OnEnable()
        {
            if (CoinManager.Instance == null) return;

            menu_homeButton.onClick.AddListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.AddListener(OnSlotMachineButtonClicked);
            menu_rechargeButton.onClick.AddListener(OnRechargeButtonClicked);
            menu_myPageButton.onClick.AddListener(OnProfileButtonClicked);
            _1DollorButton.onClick.AddListener(() => { OnCashButtonClicked(1); });
            _10DollorButton.onClick.AddListener(() => { OnCashButtonClicked(10); });
            _100DollorButton.onClick.AddListener(() => { OnCashButtonClicked(100); });
            _1000DollorButton.onClick.AddListener(() => { OnCashButtonClicked(1000); });
            _10000DollorButton.onClick.AddListener(() => { OnCashButtonClicked(10000); });
            _100000DollorButton.onClick.AddListener(() => { OnCashButtonClicked(100000); });

            CoinManager.Instance.OnCasinoCashChanged += UpdateDisplay;
            UpdateDisplay(CoinManager.Instance.CasinoCash);

            Init();
        }

        private void OnDisable()
        {
            if (CoinManager.Instance == null) return;
            CoinManager.Instance.OnCasinoCashChanged -= UpdateDisplay;

            menu_homeButton.onClick.RemoveListener(OnHomeButtonClicked);
            menu_slotMachineButton.onClick.RemoveListener(OnSlotMachineButtonClicked);
            menu_rechargeButton.onClick.RemoveListener(OnRechargeButtonClicked);
            menu_myPageButton.onClick.RemoveListener(OnProfileButtonClicked);
            _1DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(1); });
            _10DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(10); });
            _100DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(100); });
            _1000DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(1000); });
            _10000DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(10000); });
            _100000DollorButton.onClick.RemoveListener(() => { OnCashButtonClicked(100000); });
        }

        private void UpdateDisplay(int casinoCash)
        {
            casinoCashText.text = $"Cash: ${casinoCash}";
        }

        private void Init()
        {
            homePanel.SetActive(true);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
            profilePanel.SetActive(false);
        }

        private void OnHomeButtonClicked()
        {
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

        private void OnProfileButtonClicked()
        {
            homePanel.SetActive(false);
            slotMachinePanel.SetActive(false);
            rechargePanel.SetActive(false);
            profilePanel.SetActive(true);
        }

        private void OnCashButtonClicked(int cash)
        {
            CoinManager.Instance.ChargeToCasino(cash);
        }
    }
}
