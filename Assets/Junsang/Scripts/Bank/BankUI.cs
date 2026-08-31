using UnityEngine;
using Dobak.Manager;
using TMPro;
using UnityEngine.UI;

namespace Dobak.App.Bank
{
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

            ApplyKoreanStyle();

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
            cashText.text = $"통장 잔액  {bankCash:N0}원";
        }

        private void RefreshFullList()
        {
            foreach (Transform child in entryContainer)
                Destroy(child.gameObject);

            // 뱅크 화면에는 "뱅크 -> 카지노 충전" 기록만 표시 (카지노 내부 베팅/당첨은 표시 안 함)
            int visibleCount = 0;
            foreach (var record in CoinManager.Instance.History)
            {
                if (IsVisibleBankRecord(record.scope))
                {
                    CreateEntry(record);
                    visibleCount++;
                }
            }

            if (visibleCount == 0)
                CreateEmptyState();
        }

        private void HandleTransactionAdded(TransactionRecord record)
        {
            if (IsVisibleBankRecord(record.scope))
            {
                RemoveEmptyState();
                CreateEntry(record);
            }
        }

        private void CreateEntry(TransactionRecord record)
        {
            var entry = Instantiate(entryPrefab, entryContainer);
            foreach (TMP_Text text in entry.GetComponentsInChildren<TMP_Text>(true))
                text.font = cashText.font;
            entry.Set(record);
            entry.transform.SetAsFirstSibling();
        }

        private static bool IsVisibleBankRecord(TransactionScope scope)
        {
            return scope == TransactionScope.BankToCasinoCharge ||
                   scope == TransactionScope.CasinoToBankCashOut ||
                   scope == TransactionScope.DebtRepayment ||
                   scope == TransactionScope.ExternalIncome;
        }

        private void ApplyKoreanStyle()
        {
            TMP_FontAsset koreanFont = FindKoreanFont();
            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (koreanFont != null)
                    text.font = koreanFont;
                if (text.text == "Transaction History")
                    text.text = "거래 내역";
            }

            Transform current = entryContainer;
            while (current != null && current != transform)
            {
                Image background = current.GetComponent<Image>();
                if (background != null && Mathf.Max(background.color.r, background.color.g, background.color.b) < 0.25f)
                    background.color = new Color(0.96f, 0.97f, 0.99f, 1f);
                current = current.parent;
            }
        }

        private static TMP_FontAsset FindKoreanFont()
        {
            TMP_FontAsset fallback = null;
            foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font == null)
                    continue;
                fallback ??= font;
                if (font.name.Contains("NotoSansKR-Regular"))
                    return font;
            }

            return fallback;
        }

        private void CreateEmptyState()
        {
            GameObject empty = new GameObject("Empty History", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            empty.transform.SetParent(entryContainer, false);
            TMP_Text text = empty.GetComponent<TextMeshProUGUI>();
            text.font = cashText.font;
            text.fontSize = 28f;
            text.color = new Color(0.35f, 0.38f, 0.44f);
            text.alignment = TextAlignmentOptions.Center;
            text.text = "아직 거래 내역이 없습니다.";
            text.rectTransform.sizeDelta = new Vector2(1000f, 120f);
        }

        private void RemoveEmptyState()
        {
            Transform empty = entryContainer.Find("Empty History");
            if (empty != null)
                Destroy(empty.gameObject);
        }
    }
}

