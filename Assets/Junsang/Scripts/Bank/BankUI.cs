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
            cashText.text = $"<size=27><b>생활 통장</b></size>\n<size=21><color=#B8CBE4>현재 잔액</color></size>\n<size=56><b>{bankCash:N0}원</b></size>";
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
            Image rootBackground = GetComponent<Image>();
            if (rootBackground != null)
                rootBackground.color = new Color(0.945f, 0.96f, 0.985f, 1f);

            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (koreanFont != null)
                    text.font = koreanFont;
                text.fontStyle |= FontStyles.Bold;
                if (text.text == "Transaction History")
                {
                    text.text = "거래 내역";
                    text.fontSize = 38f;
                    text.fontStyle = FontStyles.Bold;
                    text.color = new Color(0.09f, 0.13f, 0.2f);
                }
            }

            cashText.fontSize = 27f;
            cashText.fontStyle = FontStyles.Normal;
            cashText.color = Color.white;
            cashText.alignment = TextAlignmentOptions.MidlineLeft;
            cashText.lineSpacing = 4f;
            cashText.raycastTarget = false;
            cashText.rectTransform.offsetMin = new Vector2(48f, 20f);
            cashText.rectTransform.offsetMax = new Vector2(-48f, -18f);
            Image accountPanel = cashText.transform.parent != null
                ? cashText.transform.parent.GetComponent<Image>()
                : null;
            if (accountPanel != null)
            {
                accountPanel.sprite = Resources.Load<Sprite>("BankUI/account_card");
                accountPanel.color = Color.white;
                Outline outline = accountPanel.GetComponent<Outline>();
                if (outline != null)
                    outline.enabled = false;
            }

            RectTransform accountCard = cashText.transform.parent?.parent as RectTransform;
            if (accountCard != null)
            {
                accountCard.anchorMin = new Vector2(0f, 1f);
                accountCard.anchorMax = new Vector2(1f, 1f);
                accountCard.pivot = new Vector2(0.5f, 1f);
                accountCard.anchoredPosition = new Vector2(0f, -48f);
                accountCard.sizeDelta = new Vector2(-140f, 230f);
            }

            RectTransform transactionArea = accountCard != null && accountCard.parent != null && accountCard.parent.childCount > 1
                ? accountCard.parent.GetChild(1) as RectTransform
                : null;
            if (transactionArea != null)
            {
                transactionArea.anchorMin = new Vector2(0f, 1f);
                transactionArea.anchorMax = new Vector2(1f, 1f);
                transactionArea.pivot = new Vector2(0.5f, 1f);
                transactionArea.anchoredPosition = new Vector2(0f, -305f);
                transactionArea.sizeDelta = new Vector2(-140f, 665f);
                Image transactionBackground = transactionArea.GetComponent<Image>();
                if (transactionBackground != null)
                    transactionBackground.color = new Color(1f, 1f, 1f, 0.96f);
            }

            Transform current = entryContainer;
            while (current != null && current != transform)
            {
                Image background = current.GetComponent<Image>();
                if (background != null && Mathf.Max(background.color.r, background.color.g, background.color.b) < 0.25f)
                    background.color = Color.white;
                current = current.parent;
            }

            ScrollRect scroll = entryContainer.GetComponentInParent<ScrollRect>(true);
            if (scroll != null)
            {
                RectTransform content = entryContainer as RectTransform;
                if (content != null)
                {
                    content.anchorMin = new Vector2(0f, 1f);
                    content.anchorMax = new Vector2(1f, 1f);
                    content.pivot = new Vector2(0.5f, 1f);
                    content.anchoredPosition = Vector2.zero;
                    scroll.content = content;
                }

                VerticalLayoutGroup layout = entryContainer.GetComponent<VerticalLayoutGroup>();
                if (layout == null)
                    layout = entryContainer.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(12, 12, 10, 18);
                layout.spacing = 10f;
                layout.childAlignment = TextAnchor.UpperCenter;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;

                ContentSizeFitter fitter = entryContainer.GetComponent<ContentSizeFitter>();
                if (fitter == null)
                    fitter = entryContainer.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 36f;
                scroll.inertia = true;
                scroll.decelerationRate = 0.12f;
                if (scroll.viewport != null)
                {
                    Image viewportImage = scroll.viewport.GetComponent<Image>();
                    if (viewportImage != null)
                        viewportImage.raycastTarget = true;
                }
            }
        }

        public void ApplyVisualDesign()
        {
            ApplyKoreanStyle();
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
            text.color = new Color(0.48f, 0.53f, 0.61f);
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

