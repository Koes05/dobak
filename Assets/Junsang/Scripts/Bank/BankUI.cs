using System.Collections;
using UnityEngine;
using Dobak.Manager;
using TMPro;
using UnityEngine.UI;

namespace Dobak.App.Bank
{
    // BankPanel에 부착.
    // 최신 거래를 항상 최상단에 두고, 메시지 대화창과 같은
    // RectMask2D + ScrollRect 구조로 터치/마우스 스크롤을 지원한다.
    public class BankUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text cashText;
        [SerializeField] private Transform entryContainer;
        [SerializeField] private TransactionEntryUI entryPrefab;

        private ScrollRect historyScroll;
        private Coroutine scrollToNewestCoroutine;

        private void OnEnable()
        {
            if (CoinManager.Instance == null)
                return;

            ApplyKoreanStyle();
            CoinManager.Instance.OnBankCashChanged += UpdateCash;
            CoinManager.Instance.OnTransactionAdded += HandleTransactionAdded;

            UpdateCash(CoinManager.Instance.BankCash);
            RefreshFullList();
        }

        private void OnDisable()
        {
            if (CoinManager.Instance != null)
            {
                CoinManager.Instance.OnBankCashChanged -= UpdateCash;
                CoinManager.Instance.OnTransactionAdded -= HandleTransactionAdded;
            }

            if (scrollToNewestCoroutine != null)
            {
                StopCoroutine(scrollToNewestCoroutine);
                scrollToNewestCoroutine = null;
            }
        }

        private void UpdateCash(int bankCash)
        {
            if (cashText == null)
                return;
            cashText.text = $"<size=27><b>생활 통장</b></size>\n<size=21><color=#B8CBE4>현재 잔액</color></size>\n<size=56><b>{bankCash:N0}원</b></size>";
        }

        private void RefreshFullList()
        {
            if (entryContainer == null || CoinManager.Instance == null)
                return;

            foreach (Transform child in entryContainer)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            int visibleCount = 0;
            // History는 오래된 순서로 저장된다. 끝에서 앞으로 읽어
            // 첫 번째 자식부터 최신 거래가 되도록 만든다.
            for (int index = CoinManager.Instance.History.Count - 1; index >= 0; index--)
            {
                TransactionRecord record = CoinManager.Instance.History[index];
                if (!IsVisibleBankRecord(record.scope))
                    continue;

                CreateEntry(record, false);
                visibleCount++;
            }

            if (visibleCount == 0)
                CreateEmptyState();

            RequestScrollToNewest();
        }

        private void HandleTransactionAdded(TransactionRecord record)
        {
            if (!IsVisibleBankRecord(record.scope))
                return;

            RemoveEmptyState();
            CreateEntry(record, true);
            RequestScrollToNewest();
        }

        private void CreateEntry(TransactionRecord record, bool newestLive)
        {
            if (entryPrefab == null || entryContainer == null)
                return;

            TransactionEntryUI entry = Instantiate(entryPrefab, entryContainer);
            if (cashText != null)
            {
                foreach (TMP_Text text in entry.GetComponentsInChildren<TMP_Text>(true))
                    text.font = cashText.font;
            }
            entry.Set(record);

            if (newestLive)
                entry.transform.SetAsFirstSibling();
            else
                entry.transform.SetAsLastSibling();
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
            if (entryContainer == null)
                return;

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

            if (cashText != null)
            {
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
                    // The blue card is rendered by the inner accountPanel above. The outer Cash Image
                    // caused the white rectangle seen in QA, so keep its layout but make only it transparent.
                    Image outerCashImage = accountCard.GetComponent<Image>();
                    if (outerCashImage != null)
                    {
                        Color outerColor = outerCashImage.color;
                        outerColor.a = 0f;
                        outerCashImage.color = outerColor;
                        outerCashImage.raycastTarget = false;
                    }

                    accountCard.anchorMin = new Vector2(0f, 1f);
                    accountCard.anchorMax = new Vector2(1f, 1f);
                    accountCard.pivot = new Vector2(0.5f, 1f);
                    accountCard.anchoredPosition = new Vector2(0f, -38f);
                    accountCard.sizeDelta = new Vector2(-72f, 230f);

                    RectTransform transactionArea = accountCard.parent != null && accountCard.parent.childCount > 1
                        ? accountCard.parent.GetChild(1) as RectTransform
                        : null;
                    if (transactionArea != null)
                    {
                        transactionArea.anchorMin = new Vector2(0f, 1f);
                        transactionArea.anchorMax = new Vector2(1f, 1f);
                        transactionArea.pivot = new Vector2(0.5f, 1f);
                        transactionArea.anchoredPosition = new Vector2(0f, -292f);
                        transactionArea.sizeDelta = new Vector2(-72f, 680f);
                        Image transactionBackground = transactionArea.GetComponent<Image>();
                        if (transactionBackground != null)
                            transactionBackground.color = new Color(1f, 1f, 1f, 0.96f);
                    }
                }
            }

            Transform current = entryContainer;
            while (current != null && current != transform)
            {
                Image background = current.GetComponent<Image>();
                if (background != null &&
                    Mathf.Max(background.color.r, background.color.g, background.color.b) < 0.25f)
                {
                    background.color = Color.white;
                }
                current = current.parent;
            }

            historyScroll = entryContainer.GetComponentInParent<ScrollRect>(true);
            if (historyScroll == null)
                return;

            RectTransform content = entryContainer as RectTransform;
            if (content != null)
            {
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.anchoredPosition = Vector2.zero;
                historyScroll.content = content;
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

            historyScroll.horizontal = false;
            historyScroll.vertical = true;
            historyScroll.movementType = ScrollRect.MovementType.Clamped;
            historyScroll.scrollSensitivity = 45f;
            historyScroll.inertia = true;
            historyScroll.decelerationRate = 0.12f;

            if (historyScroll.viewport != null)
            {
                Mask legacyMask = historyScroll.viewport.GetComponent<Mask>();
                if (legacyMask != null)
                    legacyMask.enabled = false;

                RectMask2D rectMask = historyScroll.viewport.GetComponent<RectMask2D>();
                if (rectMask == null)
                    rectMask = historyScroll.viewport.gameObject.AddComponent<RectMask2D>();
                rectMask.enabled = true;
                rectMask.padding = Vector4.zero;

                Image viewportImage = historyScroll.viewport.GetComponent<Image>();
                if (viewportImage == null)
                    viewportImage = historyScroll.viewport.gameObject.AddComponent<Image>();
                if (viewportImage.color.a <= 0.001f)
                    viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
                viewportImage.raycastTarget = true;
            }
        }

        public void ApplyVisualDesign()
        {
            ApplyKoreanStyle();
            RequestScrollToNewest();
        }

        private void RequestScrollToNewest()
        {
            if (!isActiveAndEnabled || historyScroll == null)
                return;
            if (scrollToNewestCoroutine != null)
                StopCoroutine(scrollToNewestCoroutine);
            scrollToNewestCoroutine = StartCoroutine(ScrollToNewestAfterLayout());
        }

        private IEnumerator ScrollToNewestAfterLayout()
        {
            yield return null;
            RebuildTransactionLayout();
            historyScroll.StopMovement();
            historyScroll.velocity = Vector2.zero;
            historyScroll.verticalNormalizedPosition = 1f;

            // ContentSizeFitter가 한 프레임 늦게 확정되는 경우까지 보정한다.
            yield return null;
            RebuildTransactionLayout();
            historyScroll.StopMovement();
            historyScroll.velocity = Vector2.zero;
            historyScroll.verticalNormalizedPosition = 1f;
            scrollToNewestCoroutine = null;
        }

        private void RebuildTransactionLayout()
        {
            Canvas.ForceUpdateCanvases();
            if (entryContainer is RectTransform content)
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            if (historyScroll != null && historyScroll.viewport != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(historyScroll.viewport);
            Canvas.ForceUpdateCanvases();
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
            GameObject empty = new GameObject("Empty History", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(LayoutElement));
            empty.layer = entryContainer.gameObject.layer;
            empty.transform.SetParent(entryContainer, false);

            TMP_Text text = empty.GetComponent<TextMeshProUGUI>();
            if (cashText != null)
                text.font = cashText.font;
            text.fontSize = 28f;
            text.color = new Color(0.48f, 0.53f, 0.61f);
            text.alignment = TextAlignmentOptions.Center;
            text.text = "아직 거래 내역이 없습니다.";
            text.raycastTarget = false;

            LayoutElement element = empty.GetComponent<LayoutElement>();
            element.preferredHeight = 120f;
            element.minHeight = 120f;
        }

        private void RemoveEmptyState()
        {
            Transform empty = entryContainer != null ? entryContainer.Find("Empty History") : null;
            if (empty != null)
                Destroy(empty.gameObject);
        }
    }
}
