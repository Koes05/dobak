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
            descriptionText.text = record.description;
            // 필요하면 아래처럼 시각/잔액도 같이 표시 가능
            // descriptionText.text = $"{record.description}  ({record.timestamp:HH:mm})";
        }
    }
}
