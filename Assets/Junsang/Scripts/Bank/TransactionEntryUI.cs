using UnityEngine;
using TMPro;
using Dobak.Manager;

namespace Dobak.App.Bank
{
    // Transaction History의 한 줄 (예: "Use 1$ in Casino"). 프리팹으로 만들어서 BankUI에 연결.
    public class TransactionEntryUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text descriptionText;

        public void Set(TransactionRecord record)
        {
            string sign = record.amount >= 0 ? "+" : "";
            descriptionText.text = $"{record.description}   {sign}{record.amount:N0}원   잔액 {record.bankBalanceAfter:N0}원";
        }
    }
}
