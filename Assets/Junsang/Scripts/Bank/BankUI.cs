using UnityEngine;
using UnityEngine.UI;
using TMPro;

// BankPanel에 부착.
// Hierarchy 예시:
// BankPanel
//  ├─ CashText (Text)
//  └─ ScrollView
//      └─ Viewport
//          └─ Content (Vertical Layout Group) <- entryContainer로 연결
public class BankUI : MonoBehaviour
{
    [SerializeField] private TMP_Text cashText;
    [SerializeField] private Transform entryContainer;       // Content 오브젝트
    [SerializeField] private TransactionEntryUI entryPrefab; // 거래 1건 표시용 프리팹

    private void OnEnable()
    {
        if (CoinManager.Instance == null) return;

        CoinManager.Instance.OnBankCashChanged += UpdateCash;
        CoinManager.Instance.OnTransactionAdded += HandleTransactionAdded;

        UpdateCash(CoinManager.Instance.BankCash);
        RefreshFullList();
    }

    private void OnDisable()
    {
        if (CoinManager.Instance == null) return;

        CoinManager.Instance.OnBankCashChanged -= UpdateCash;
        CoinManager.Instance.OnTransactionAdded -= HandleTransactionAdded;
    }

    private void UpdateCash(int bankCash)
    {
        cashText.text = $"Cash: ${bankCash}";
    }

    private void RefreshFullList()
    {
        foreach (Transform child in entryContainer)
            Destroy(child.gameObject);

        // 뱅크 화면에는 "뱅크 -> 카지노 충전" 기록만 표시 (카지노 내부 베팅/당첨은 표시 안 함)
        foreach (var record in CoinManager.Instance.History)
        {
            if (record.scope == TransactionScope.BankToCasinoCharge)
                CreateEntry(record);
        }
    }

    private void HandleTransactionAdded(TransactionRecord record)
    {
        if (record.scope == TransactionScope.BankToCasinoCharge)
            CreateEntry(record);
    }

    private void CreateEntry(TransactionRecord record)
    {
        var entry = Instantiate(entryPrefab, entryContainer);
        entry.Set(record);
        entry.transform.SetAsFirstSibling();
    }
}
