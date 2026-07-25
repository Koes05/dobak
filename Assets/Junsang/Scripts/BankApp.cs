using Dobak.Event.SO;
using TMPro;
using UnityEngine;

namespace Dobak.App.Bank
{
    public class BankApp : MonoBehaviour
    {
        [SerializeField] private GameStateSO gameState;
        [SerializeField] private IntEventChannelSO onBalanceChanged;
        [SerializeField] private IntEventChannelSO onDebtChanged;

        [SerializeField] private TextMeshProUGUI playerDeptText;
        [SerializeField] private TextMeshProUGUI balanceText;

        private void OnEnable()
        {
            onBalanceChanged.RegisterListener(UpdateBalanceText);
            onDebtChanged.RegisterListener(UpdateDebtText);
        }

        private void OnDisable()
        {
            onBalanceChanged.UnregisterListener(UpdateBalanceText);
            onDebtChanged.UnregisterListener(UpdateDebtText);
        }

        private void Start()
        {
            playerDeptText.text = $"Debt: {gameState.Debt}";
            balanceText.text = $"Balance: {gameState.Balance}";
        }

        private void UpdateBalanceText(int newBalance)
        {
            balanceText.text = $"Balance: {newBalance}";
        }

        private void UpdateDebtText(int newDebt)
        {
            playerDeptText.text = $"Debt: {newDebt}";
        }
    }
}
