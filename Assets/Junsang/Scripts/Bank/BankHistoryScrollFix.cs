using System;
using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Dobak.Manager;

namespace Dobak.App.Bank
{
    /// <summary>
    /// Builds one standard ScrollRect for the bank transaction list and fixes the two layout issues
    /// seen in QA: the white image behind the blue account card and the narrow/clipped transaction
    /// viewport. Bank balances, automatic cash-out and transaction ordering stay in BankUI/CoinManager.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public sealed class BankHistoryScrollFix : MonoBehaviour
    {
        private const string TabletSceneName = "TabletUI";
        private const string ScrollRootName = "Transaction Scroll View V21";
        private static readonly BindingFlags Fields =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo EntryContainerField = typeof(BankUI).GetField("entryContainer", Fields);
        private static readonly FieldInfo CashTextField = typeof(BankUI).GetField("cashText", Fields);
        private static readonly MethodInfo RefreshFullListMethod = typeof(BankUI).GetMethod("RefreshFullList", Fields);

        private BankUI bankUI;
        private TMP_Text cashText;
        private RectTransform transactionArea;
        private RectTransform scrollRoot;
        private RectTransform viewport;
        private RectTransform content;
        private ScrollRect scrollRect;
        private Coroutine repairRoutine;
        private bool applyingLayout;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            AttachToBankPanels(SceneManager.GetActiveScene());
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            AttachToBankPanels(scene);
        }

        private static void AttachToBankPanels(Scene scene)
        {
            if (!string.Equals(scene.name, TabletSceneName, StringComparison.Ordinal))
                return;

            BankUI[] banks = FindObjectsByType<BankUI>(FindObjectsInactive.Include);
            foreach (BankUI bank in banks)
            {
                if (bank == null || bank.GetComponent<BankHistoryScrollFix>() != null)
                    continue;
                bank.gameObject.AddComponent<BankHistoryScrollFix>();
            }
        }

        private void Awake()
        {
            bankUI = GetComponent<BankUI>();
        }

        private void OnEnable()
        {
            if (CoinManager.Instance != null)
                CoinManager.Instance.OnTransactionAdded += HandleTransactionAdded;
            QueueRepair(true);
        }

        private void OnDisable()
        {
            if (CoinManager.Instance != null)
                CoinManager.Instance.OnTransactionAdded -= HandleTransactionAdded;
            if (repairRoutine != null)
            {
                StopCoroutine(repairRoutine);
                repairRoutine = null;
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            if (isActiveAndEnabled && !applyingLayout)
                QueueRepair(false);
        }

        private void HandleTransactionAdded(TransactionRecord record)
        {
            QueueRepair(false);
        }

        private void QueueRepair(bool rebuildEntries)
        {
            if (!isActiveAndEnabled)
                return;
            if (repairRoutine != null)
                StopCoroutine(repairRoutine);
            repairRoutine = StartCoroutine(RepairNextFrames(rebuildEntries));
        }

        private IEnumerator RepairNextFrames(bool rebuildEntries)
        {
            yield return null;
            if (!ResolveReferences())
            {
                repairRoutine = null;
                yield break;
            }

            applyingLayout = true;
            BuildStandardHierarchy();
            ApplyCardAndPanelLayout();
            ConfigureScrollRect();

            if (rebuildEntries && RefreshFullListMethod != null)
                RefreshFullListMethod.Invoke(bankUI, null);
            applyingLayout = false;

            yield return null;
            applyingLayout = true;
            RebuildAndSnapTop();
            applyingLayout = false;
            yield return new WaitForEndOfFrame();
            applyingLayout = true;
            RebuildAndSnapTop();
            applyingLayout = false;
            repairRoutine = null;
        }

        private bool ResolveReferences()
        {
            if (bankUI == null)
                bankUI = GetComponent<BankUI>();
            if (bankUI == null || EntryContainerField == null)
                return false;

            content = EntryContainerField.GetValue(bankUI) as RectTransform;
            cashText = CashTextField?.GetValue(bankUI) as TMP_Text;
            if (content == null)
                return false;

            transactionArea = FindTransactionArea();
            return transactionArea != null;
        }

        private RectTransform FindTransactionArea()
        {
            if (cashText != null)
            {
                RectTransform accountCard = cashText.transform.parent?.parent as RectTransform;
                Transform commonParent = accountCard != null ? accountCard.parent : null;
                if (commonParent != null)
                {
                    foreach (RectTransform child in commonParent)
                    {
                        if (child == null || child == accountCard)
                            continue;
                        if (ContainsTransactionTitle(child) || content.IsChildOf(child))
                            return child;
                    }
                }
            }

            Transform current = content.parent;
            while (current != null && current != transform)
            {
                if (ContainsTransactionTitle(current))
                    return current as RectTransform;
                current = current.parent;
            }
            return transform as RectTransform;
        }

        private static bool ContainsTransactionTitle(Transform root)
        {
            if (root == null)
                return false;
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                string value = (text.text ?? string.Empty).Trim();
                if (value == "거래 내역" || value == "Transaction History")
                    return true;
            }
            return false;
        }

        private void BuildStandardHierarchy()
        {
            Transform existing = transactionArea.Find(ScrollRootName);
            if (existing != null)
            {
                scrollRoot = existing as RectTransform;
                viewport = scrollRoot != null ? scrollRoot.Find("Viewport") as RectTransform : null;
                scrollRect = scrollRoot != null ? scrollRoot.GetComponent<ScrollRect>() : null;
            }

            Transform oldParent = content.parent;
            DisableLegacyScrollComponents(oldParent);

            if (scrollRoot == null)
            {
                GameObject rootObject = new GameObject(ScrollRootName, typeof(RectTransform), typeof(ScrollRect));
                rootObject.layer = transactionArea.gameObject.layer;
                rootObject.transform.SetParent(transactionArea, false);
                scrollRoot = rootObject.GetComponent<RectTransform>();
                scrollRect = rootObject.GetComponent<ScrollRect>();
            }

            // Leave room for the grey "거래 내역" header, while using almost the full panel width.
            scrollRoot.anchorMin = Vector2.zero;
            scrollRoot.anchorMax = Vector2.one;
            scrollRoot.pivot = new Vector2(0.5f, 0.5f);
            scrollRoot.offsetMin = new Vector2(24f, 20f);
            scrollRoot.offsetMax = new Vector2(-24f, -92f);
            scrollRoot.SetAsLastSibling();

            if (viewport == null)
            {
                GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
                viewportObject.layer = transactionArea.gameObject.layer;
                viewportObject.transform.SetParent(scrollRoot, false);
                viewport = viewportObject.GetComponent<RectTransform>();
            }
            Stretch(viewport);

            Image viewportImage = viewport.GetComponent<Image>();
            if (viewportImage == null)
                viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
            viewportImage.raycastTarget = true;

            Mask legacyMask = viewport.GetComponent<Mask>();
            if (legacyMask != null)
                legacyMask.enabled = false;
            RectMask2D rectMask = viewport.GetComponent<RectMask2D>();
            if (rectMask == null)
                rectMask = viewport.gameObject.AddComponent<RectMask2D>();
            rectMask.enabled = true;
            rectMask.padding = Vector4.zero;

            if (content.parent != viewport)
                content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            content.localScale = Vector3.one;
        }

        private void DisableLegacyScrollComponents(Transform from)
        {
            Transform current = from;
            while (current != null && current != transactionArea)
            {
                ScrollRect oldScroll = current.GetComponent<ScrollRect>();
                if (oldScroll != null && oldScroll != scrollRect)
                    oldScroll.enabled = false;
                Mask oldMask = current.GetComponent<Mask>();
                if (oldMask != null)
                    oldMask.enabled = false;
                RectMask2D oldRectMask = current.GetComponent<RectMask2D>();
                if (oldRectMask != null)
                    oldRectMask.enabled = false;
                current = current.parent;
            }
        }

        private void ApplyCardAndPanelLayout()
        {
            if (cashText != null)
            {
                // The blue account-card image is on the inner object. The outer "Cash" Image caused
                // the visible white rectangle around it, so only that outer image becomes transparent.
                Transform innerCard = cashText.transform.parent;
                RectTransform outerCash = innerCard?.parent as RectTransform;
                Image outerImage = outerCash != null ? outerCash.GetComponent<Image>() : null;
                if (outerImage != null)
                {
                    Color color = outerImage.color;
                    color.a = 0f;
                    outerImage.color = color;
                    outerImage.raycastTarget = false;
                }

                Image innerImage = innerCard != null ? innerCard.GetComponent<Image>() : null;
                if (innerImage != null)
                {
                    innerImage.color = Color.white;
                    innerImage.raycastTarget = false;
                }

                if (outerCash != null)
                {
                    outerCash.anchorMin = new Vector2(0f, 1f);
                    outerCash.anchorMax = new Vector2(1f, 1f);
                    outerCash.pivot = new Vector2(0.5f, 1f);
                    outerCash.anchoredPosition = new Vector2(0f, -38f);
                    outerCash.sizeDelta = new Vector2(-72f, 230f);
                }
            }

            transactionArea.anchorMin = new Vector2(0f, 1f);
            transactionArea.anchorMax = new Vector2(1f, 1f);
            transactionArea.pivot = new Vector2(0.5f, 1f);
            transactionArea.anchoredPosition = new Vector2(0f, -292f);
            transactionArea.sizeDelta = new Vector2(-72f, 680f);

            foreach (TMP_Text title in transactionArea.GetComponentsInChildren<TMP_Text>(true))
            {
                string value = (title.text ?? string.Empty).Trim();
                if (value != "거래 내역" && value != "Transaction History")
                    continue;
                title.text = "거래 내역";
                RectTransform rect = title.rectTransform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, 86f);
            }
        }

        private void ConfigureScrollRect()
        {
            if (content == null || viewport == null || scrollRect == null)
                return;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.12f;
            scrollRect.scrollSensitivity = 45f;
            scrollRect.enabled = true;

            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
                layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 8, 20);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.reverseArrangement = false;

            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null)
                fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void RebuildAndSnapTop()
        {
            if (content == null || viewport == null || scrollRect == null)
                return;

            foreach (RectTransform child in content)
            {
                if (child == null)
                    continue;
                child.anchorMin = new Vector2(0f, child.anchorMin.y);
                child.anchorMax = new Vector2(1f, child.anchorMax.y);
                child.sizeDelta = new Vector2(0f, child.sizeDelta.y);
                child.localScale = Vector3.one;

                LayoutElement element = child.GetComponent<LayoutElement>();
                if (element == null)
                    element = child.gameObject.AddComponent<LayoutElement>();
                float height = Mathf.Max(82f, child.rect.height, child.sizeDelta.y);
                element.minHeight = height;
                element.preferredHeight = height;
                element.flexibleWidth = 1f;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRoot);
            Canvas.ForceUpdateCanvases();

            scrollRect.StopMovement();
            scrollRect.velocity = Vector2.zero;
            scrollRect.verticalNormalizedPosition = 1f;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
