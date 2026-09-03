using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Dobak.Manager;

namespace Dobak.App.Bank
{
    /// <summary>
    /// 기존 BankUI의 데이터/환전 로직은 건드리지 않고 거래내역 표시 구조만 보정한다.
    /// 씬에 ScrollRect가 없어도 실행 시 표준 ScrollRect + Viewport + RectMask2D 구조를 만든다.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class BankHistoryScrollFix : MonoBehaviour
    {
        private const string ScrollRootName = "Bank History Scroll";
        private const string ViewportName = "Viewport";

        private static readonly FieldInfo EntryContainerField = typeof(BankUI).GetField(
            "entryContainer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo RefreshFullListMethod = typeof(BankUI).GetMethod(
            "RefreshFullList",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private BankUI bankUI;
        private RectTransform content;
        private RectTransform viewport;
        private ScrollRect scrollRect;
        private Coroutine repairRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            AttachToBankPanels();
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            AttachToBankPanels();
        }

        private static void AttachToBankPanels()
        {
            BankUI[] banks = Object.FindObjectsByType<BankUI>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

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

            StartRepair();
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
            if (isActiveAndEnabled && scrollRect != null)
                StartRepair(false);
        }

        private void HandleTransactionAdded(TransactionRecord record)
        {
            StartRepair(false);
        }

        private void StartRepair(bool rebuildEntries = true)
        {
            if (!isActiveAndEnabled)
                return;

            if (repairRoutine != null)
                StopCoroutine(repairRoutine);

            repairRoutine = StartCoroutine(RepairAfterLayout(rebuildEntries));
        }

        private IEnumerator RepairAfterLayout(bool rebuildEntries)
        {
            yield return null;

            if (!ResolveContent())
            {
                repairRoutine = null;
                yield break;
            }

            EnsureScrollHierarchy();
            ConfigureLayout();

            // 첫 오픈 때 BankUI가 ScrollRect를 찾지 못하고 반환했더라도
            // 구조를 만든 뒤 다시 스타일/목록 갱신을 실행한다.
            if (bankUI != null)
            {
                bankUI.ApplyVisualDesign();

                if (rebuildEntries && RefreshFullListMethod != null)
                    RefreshFullListMethod.Invoke(bankUI, null);
            }

            yield return null;
            RebuildAndSnapTop();

            yield return new WaitForEndOfFrame();
            RebuildAndSnapTop();
            repairRoutine = null;
        }

        private bool ResolveContent()
        {
            if (content != null)
                return true;

            if (bankUI == null)
                bankUI = GetComponent<BankUI>();
            if (bankUI == null || EntryContainerField == null)
                return false;

            content = EntryContainerField.GetValue(bankUI) as RectTransform;
            return content != null;
        }

        private void EnsureScrollHierarchy()
        {
            ScrollRect existing = content.GetComponentInParent<ScrollRect>(true);
            if (existing != null && existing.content == content)
            {
                scrollRect = existing;
                viewport = existing.viewport != null
                    ? existing.viewport
                    : content.parent as RectTransform;
                return;
            }

            RectTransform originalParent = content.parent as RectTransform;
            if (originalParent == null)
                return;

            int siblingIndex = content.GetSiblingIndex();

            Vector2 anchorMin = content.anchorMin;
            Vector2 anchorMax = content.anchorMax;
            Vector2 pivotValue = content.pivot;
            Vector2 anchoredPosition = content.anchoredPosition;
            Vector2 sizeDelta = content.sizeDelta;
            Quaternion localRotation = content.localRotation;
            Vector3 localScale = content.localScale;

            GameObject rootObject = new GameObject(
                ScrollRootName,
                typeof(RectTransform),
                typeof(ScrollRect));
            rootObject.layer = content.gameObject.layer;

            RectTransform root = rootObject.GetComponent<RectTransform>();
            root.SetParent(originalParent, false);
            root.SetSiblingIndex(siblingIndex);
            root.anchorMin = anchorMin;
            root.anchorMax = anchorMax;
            root.pivot = pivotValue;
            root.anchoredPosition = anchoredPosition;
            root.sizeDelta = sizeDelta;
            root.localRotation = localRotation;
            root.localScale = localScale;

            GameObject viewportObject = new GameObject(
                ViewportName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(RectMask2D));
            viewportObject.layer = content.gameObject.layer;

            viewport = viewportObject.GetComponent<RectTransform>();
            viewport.SetParent(root, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.anchoredPosition = Vector2.zero;
            viewport.sizeDelta = Vector2.zero;

            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
            viewportImage.raycastTarget = true;

            RectMask2D mask = viewportObject.GetComponent<RectMask2D>();
            mask.padding = Vector4.zero;

            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            content.localRotation = Quaternion.identity;
            content.localScale = Vector3.one;

            scrollRect = rootObject.GetComponent<ScrollRect>();
            scrollRect.content = content;
            scrollRect.viewport = viewport;
        }

        private void ConfigureLayout()
        {
            if (content == null || scrollRect == null)
                return;

            if (viewport == null)
                viewport = scrollRect.viewport != null
                    ? scrollRect.viewport
                    : content.parent as RectTransform;

            scrollRect.content = content;
            scrollRect.viewport = viewport;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.12f;
            scrollRect.scrollSensitivity = 45f;
            scrollRect.enabled = true;

            if (viewport != null)
            {
                Mask legacyMask = viewport.GetComponent<Mask>();
                if (legacyMask != null)
                    legacyMask.enabled = false;

                RectMask2D rectMask = viewport.GetComponent<RectMask2D>();
                if (rectMask == null)
                    rectMask = viewport.gameObject.AddComponent<RectMask2D>();
                rectMask.enabled = true;
                rectMask.padding = Vector4.zero;

                Image image = viewport.GetComponent<Image>();
                if (image == null)
                    image = viewport.gameObject.AddComponent<Image>();
                if (image.color.a <= 0.001f)
                    image.color = new Color(1f, 1f, 1f, 0.001f);
                image.raycastTarget = true;
            }

            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);

            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
                layout = content.gameObject.AddComponent<VerticalLayoutGroup>();

            layout.padding = new RectOffset(12, 12, 10, 18);
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
            if (content == null || scrollRect == null)
                return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            float preferredHeight = LayoutUtility.GetPreferredHeight(content);
            if (preferredHeight > 0f)
                content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, preferredHeight);

            if (viewport != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);

            if (scrollRect.transform is RectTransform scrollTransform)
                LayoutRebuilder.ForceRebuildLayoutImmediate(scrollTransform);

            Canvas.ForceUpdateCanvases();
            scrollRect.StopMovement();
            scrollRect.velocity = Vector2.zero;
            scrollRect.verticalNormalizedPosition = 1f;

            Vector2 position = content.anchoredPosition;
            position.y = 0f;
            content.anchoredPosition = position;
        }
    }
}
