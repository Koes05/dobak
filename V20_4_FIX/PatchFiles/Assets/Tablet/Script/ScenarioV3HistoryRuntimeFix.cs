using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Repairs only the VN "지난 대화" viewport. Chat and bank histories are untouched.
/// </summary>
[DefaultExecutionOrder(15000)]
public sealed class ScenarioV3HistoryRuntimeFix : MonoBehaviour
{
    private const string RootName = "History Scroll Root V20";
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private ScenarioV3Director director;
    private GameObject historyPanel;
    private TMP_Text historyText;
    private RectTransform root;
    private RectTransform viewport;
    private RectTransform content;
    private ScrollRect scroll;
    private bool wasOpen;
    private Coroutine layoutRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        CreateBootstrap();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => CreateBootstrap();

    private static void CreateBootstrap()
    {
        if (FindAnyObjectByType<ScenarioV3HistoryRuntimeFix>(FindObjectsInactive.Include) != null)
            return;
        new GameObject("Scenario V3 History Runtime Fix V20").AddComponent<ScenarioV3HistoryRuntimeFix>();
    }

    private IEnumerator Start()
    {
        float timeout = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeout)
        {
            director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
            if (director != null && director.IsReady && ResolveReferences())
                break;
            yield return null;
        }

        if (director == null || !ResolveReferences())
        {
            enabled = false;
            yield break;
        }

        RepairHierarchy();
        StartLayoutRepair(false);
    }

    private void LateUpdate()
    {
        if (!ResolveReferences())
            return;

        bool isOpen = historyPanel.activeInHierarchy;
        if (isOpen && !wasOpen)
        {
            RepairHierarchy();
            StartLayoutRepair(true);
        }
        else if (isOpen)
        {
            RepairHierarchy();
        }
        wasOpen = isOpen;
    }

    private bool ResolveReferences()
    {
        if (director == null)
            return false;
        historyPanel = GetField<GameObject>("historyPanel") ?? historyPanel;
        historyText = GetField<TMP_Text>("historyText") ?? historyText;
        return historyPanel != null && historyText != null;
    }

    private void RepairHierarchy()
    {
        if (historyPanel == null || historyText == null)
            return;

        Transform existingRoot = historyPanel.transform.Find(RootName);
        if (existingRoot == null)
        {
            GameObject rootObject = new GameObject(RootName, typeof(RectTransform), typeof(ScrollRect));
            rootObject.layer = historyPanel.layer;
            rootObject.transform.SetParent(historyPanel.transform, false);
            root = rootObject.GetComponent<RectTransform>();
            root.anchorMin = new Vector2(0.06f, 0.08f);
            root.anchorMax = new Vector2(0.94f, 0.86f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;
            root.pivot = new Vector2(0.5f, 0.5f);

            Transform oldViewport = historyPanel.transform.Find("History Viewport");
            if (oldViewport != null)
            {
                viewport = oldViewport as RectTransform;
                viewport.SetParent(root, false);
            }
            else
            {
                GameObject viewportObject = new GameObject("History Viewport", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
                viewportObject.layer = historyPanel.layer;
                viewportObject.transform.SetParent(root, false);
                viewport = viewportObject.GetComponent<RectTransform>();
            }
        }
        else
        {
            root = existingRoot as RectTransform;
            viewport = root.Find("History Viewport") as RectTransform;
        }

        if (root == null || viewport == null)
            return;

        root.anchorMin = new Vector2(0.06f, 0.08f);
        root.anchorMax = new Vector2(0.94f, 0.86f);
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;
        root.SetAsLastSibling();

        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;
        viewport.pivot = new Vector2(0.5f, 0.5f);

        Image viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewport.gameObject.AddComponent<Image>();
        if (viewportImage.color.a < 0.2f)
            viewportImage.color = new Color(0.04f, 0.065f, 0.10f, 1f);
        viewportImage.raycastTarget = true;

        Mask legacyMask = viewport.GetComponent<Mask>();
        if (legacyMask != null)
            legacyMask.enabled = false;
        RectMask2D rectMask = viewport.GetComponent<RectMask2D>();
        if (rectMask == null)
            rectMask = viewport.gameObject.AddComponent<RectMask2D>();
        rectMask.enabled = true;
        rectMask.padding = Vector4.zero;

        Transform contentTransform = viewport.Find("History Content");
        if (contentTransform == null)
        {
            GameObject contentObject = new GameObject("History Content", typeof(RectTransform));
            contentObject.layer = historyPanel.layer;
            contentObject.transform.SetParent(viewport, false);
            contentTransform = contentObject.transform;
        }
        content = contentTransform as RectTransform;
        if (content == null)
            return;

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;

        VerticalLayoutGroup legacyLayout = content.GetComponent<VerticalLayoutGroup>();
        if (legacyLayout != null)
            legacyLayout.enabled = false;
        ContentSizeFitter legacyFitter = content.GetComponent<ContentSizeFitter>();
        if (legacyFitter != null)
            legacyFitter.enabled = false;

        if (historyText.transform.parent != content)
            historyText.transform.SetParent(content, false);
        historyText.gameObject.SetActive(true);
        historyText.maskable = true;
        historyText.raycastTarget = false;
        historyText.alignment = TextAlignmentOptions.TopLeft;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;

        ContentSizeFitter textFitter = historyText.GetComponent<ContentSizeFitter>();
        if (textFitter != null)
            textFitter.enabled = false;

        foreach (TMP_Text candidate in historyPanel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (candidate == historyText)
                continue;
            if (candidate.gameObject.name.StartsWith("History Text", StringComparison.OrdinalIgnoreCase))
                candidate.gameObject.SetActive(false);
        }

        ScrollRect oldScroll = viewport.GetComponent<ScrollRect>();
        if (oldScroll != null)
            oldScroll.enabled = false;
        scroll = root.GetComponent<ScrollRect>();
        if (scroll == null)
            scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;
        scroll.scrollSensitivity = 45f;
        scroll.enabled = true;

        SetField("historyViewportRect", viewport);
        SetField("historyContentRect", content);
        SetField("historyScroll", scroll);
        SetField("historyText", historyText);
    }

    private void StartLayoutRepair(bool snapToBottom)
    {
        if (!isActiveAndEnabled)
            return;
        if (layoutRoutine != null)
            StopCoroutine(layoutRoutine);
        layoutRoutine = StartCoroutine(RepairLayoutNextFrames(snapToBottom));
    }

    private IEnumerator RepairLayoutNextFrames(bool snapToBottom)
    {
        yield return null;
        ApplyLayout(snapToBottom);
        yield return new WaitForEndOfFrame();
        ApplyLayout(snapToBottom);
        layoutRoutine = null;
    }

    private void ApplyLayout(bool snapToBottom)
    {
        if (historyText == null || viewport == null || content == null || scroll == null)
            return;

        Canvas.ForceUpdateCanvases();
        float viewportWidth = Mathf.Max(480f, viewport.rect.width);
        float viewportHeight = Mathf.Max(320f, viewport.rect.height);
        const float sidePadding = 32f;
        const float verticalPadding = 24f;
        float textWidth = Mathf.Max(320f, viewportWidth - sidePadding * 2f);
        float preferredHeight = Mathf.Max(80f,
            historyText.GetPreferredValues(historyText.text ?? string.Empty, textWidth, 0f).y);
        float contentHeight = Mathf.Max(viewportHeight, preferredHeight + verticalPadding * 2f);

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = new Vector2(0f, contentHeight);

        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 1f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = new Vector2(0f, -verticalPadding);
        textRect.sizeDelta = new Vector2(-sidePadding * 2f, preferredHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);
        Canvas.ForceUpdateCanvases();
        if (snapToBottom)
        {
            scroll.StopMovement();
            scroll.velocity = Vector2.zero;
            scroll.verticalNormalizedPosition = 0f;
        }
    }

    private T GetField<T>(string name) where T : class
    {
        FieldInfo field = typeof(ScenarioV3Director).GetField(name, Fields);
        return field?.GetValue(director) as T;
    }

    private void SetField(string name, object value)
    {
        FieldInfo field = typeof(ScenarioV3Director).GetField(name, Fields);
        field?.SetValue(director, value);
    }
}
