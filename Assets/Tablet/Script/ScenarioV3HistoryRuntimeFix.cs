using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Owns the VN "지난 대화" Scroll View. The original scene objects are left in place but hidden,
/// so only one TMP text is ever rendered and every glyph is clipped by a RectMask2D viewport.
/// Chat histories and bank histories are not touched.
/// </summary>
[DefaultExecutionOrder(20000)]
public sealed class ScenarioV3HistoryRuntimeFix : MonoBehaviour
{
    private const string TabletSceneName = "TabletUI";
    private const string RootName = "History Scroll View V21";
    private static readonly BindingFlags Fields =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private ScenarioV3Director director;
    private GameObject historyPanel;
    private RectTransform scrollRoot;
    private RectTransform viewport;
    private RectTransform content;
    private TextMeshProUGUI historyText;
    private ScrollRect scrollRect;
    private bool wasOpen;
    private string lastRenderedText = string.Empty;
    private Vector2 lastViewportSize = Vector2.zero;
    private Coroutine rebuildRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        TryCreateBootstrap(SceneManager.GetActiveScene());
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryCreateBootstrap(scene);
    }

    private static void TryCreateBootstrap(Scene scene)
    {
        if (!string.Equals(scene.name, TabletSceneName, StringComparison.Ordinal))
            return;
        if (FindAnyObjectByType<ScenarioV3HistoryRuntimeFix>(FindObjectsInactive.Include) != null)
            return;

        new GameObject("Scenario V3 History Scroll View V21")
            .AddComponent<ScenarioV3HistoryRuntimeFix>();
    }

    private IEnumerator Start()
    {
        float timeoutAt = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeoutAt)
        {
            director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
            if (director != null && director.IsReady && ResolvePanel())
                break;
            yield return null;
        }

        if (director == null || !ResolvePanel())
        {
            Debug.LogError("[V21 History] ScenarioV3Director 또는 지난 대화 패널을 찾지 못했습니다.");
            enabled = false;
            yield break;
        }

        BuildScrollView();
        QueueRebuild(false);
    }

    private void LateUpdate()
    {
        if (!ResolvePanel() || historyText == null)
            return;

        bool isOpen = historyPanel.activeInHierarchy;
        Vector2 viewportSize = viewport != null ? viewport.rect.size : Vector2.zero;
        bool textChanged = !string.Equals(lastRenderedText, historyText.text, StringComparison.Ordinal);
        bool sizeChanged = (viewportSize - lastViewportSize).sqrMagnitude > 0.5f;

        if (isOpen && !wasOpen)
        {
            QueueRebuild(true);
        }
        else if (isOpen && (textChanged || sizeChanged))
        {
            QueueRebuild(false);
        }

        wasOpen = isOpen;
        lastRenderedText = historyText.text ?? string.Empty;
        lastViewportSize = viewportSize;
    }

    private bool ResolvePanel()
    {
        if (director == null)
            return false;

        historyPanel = GetDirectorField<GameObject>("historyPanel") ?? historyPanel;
        if (historyPanel == null)
            historyPanel = FindSceneObject("Dialogue History");
        return historyPanel != null;
    }

    private void BuildScrollView()
    {
        TMP_Text originalText = GetDirectorField<TMP_Text>("historyText");
        TMP_FontAsset font = originalText != null ? originalText.font : FindAnyObjectByType<TMP_Text>()?.font;
        Color textColor = originalText != null
            ? originalText.color
            : new Color(0.9f, 0.93f, 0.98f, 1f);
        float fontSize = originalText != null ? Mathf.Max(24f, originalText.fontSize) : 27f;

        // Remove old runtime roots and hide all legacy viewports/texts. Keeping them active was the
        // reason a second unmasked TMP text could still be visible outside the bright rectangle.
        foreach (Transform child in historyPanel.transform)
        {
            if (child == null)
                continue;
            if (child.name == RootName)
                continue;
            if (child.name.IndexOf("History Viewport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                child.name.IndexOf("History Scroll Root", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                child.gameObject.SetActive(false);
            }
        }

        foreach (TMP_Text candidate in historyPanel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (candidate == null)
                continue;
            if (candidate.gameObject.name.IndexOf("History Text", StringComparison.OrdinalIgnoreCase) >= 0)
                candidate.gameObject.SetActive(false);
        }

        Transform existingRoot = historyPanel.transform.Find(RootName);
        if (existingRoot != null)
        {
            scrollRoot = existingRoot as RectTransform;
            viewport = scrollRoot != null ? scrollRoot.Find("Viewport") as RectTransform : null;
            content = viewport != null ? viewport.Find("Content") as RectTransform : null;
            historyText = content != null ? content.Find("History Text V21")?.GetComponent<TextMeshProUGUI>() : null;
            scrollRect = scrollRoot != null ? scrollRoot.GetComponent<ScrollRect>() : null;
        }

        if (scrollRoot == null)
        {
            GameObject rootObject = new GameObject(RootName, typeof(RectTransform), typeof(ScrollRect));
            rootObject.layer = historyPanel.layer;
            rootObject.transform.SetParent(historyPanel.transform, false);
            scrollRoot = rootObject.GetComponent<RectTransform>();
            scrollRect = rootObject.GetComponent<ScrollRect>();
        }

        // Keep the scroll area behind the title and close button.
        scrollRoot.SetSiblingIndex(0);
        scrollRoot.anchorMin = new Vector2(0.06f, 0.08f);
        scrollRoot.anchorMax = new Vector2(0.94f, 0.86f);
        scrollRoot.offsetMin = Vector2.zero;
        scrollRoot.offsetMax = Vector2.zero;
        scrollRoot.pivot = new Vector2(0.5f, 0.5f);

        if (viewport == null)
        {
            GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
            viewportObject.layer = historyPanel.layer;
            viewportObject.transform.SetParent(scrollRoot, false);
            viewport = viewportObject.GetComponent<RectTransform>();
        }
        Stretch(viewport);

        Image viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(0.04f, 0.065f, 0.10f, 1f);
        viewportImage.raycastTarget = true;

        Mask stencilMask = viewport.GetComponent<Mask>();
        if (stencilMask != null)
            stencilMask.enabled = false;
        RectMask2D rectMask = viewport.GetComponent<RectMask2D>();
        if (rectMask == null)
            rectMask = viewport.gameObject.AddComponent<RectMask2D>();
        rectMask.enabled = true;
        rectMask.padding = Vector4.zero;

        if (content == null)
        {
            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.layer = historyPanel.layer;
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
        }
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        foreach (Canvas canvas in content.GetComponentsInChildren<Canvas>(true))
            canvas.enabled = false;

        if (historyText == null)
        {
            GameObject textObject = new GameObject("History Text V21", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.layer = historyPanel.layer;
            textObject.transform.SetParent(content, false);
            historyText = textObject.GetComponent<TextMeshProUGUI>();
        }

        historyText.gameObject.SetActive(true);
        historyText.font = font;
        historyText.fontSize = fontSize;
        historyText.fontStyle = FontStyles.Bold;
        historyText.color = textColor;
        historyText.alignment = TextAlignmentOptions.TopLeft;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        historyText.maskable = true;
        historyText.raycastTarget = false;

        ContentSizeFitter fitter = historyText.GetComponent<ContentSizeFitter>();
        if (fitter != null)
            fitter.enabled = false;

        scrollRect.viewport = viewport;
        scrollRect.content = content;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.12f;
        scrollRect.scrollSensitivity = 45f;
        scrollRect.enabled = true;

        SetDirectorField("historyViewportRect", viewport);
        SetDirectorField("historyContentRect", content);
        SetDirectorField("historyScroll", scrollRect);
        SetDirectorField("historyText", historyText);

        // Restore the current day log immediately. Director.ShowHistory will update the same text later.
        List<string> log = GetDirectorField<List<string>>("dialogueLog");
        historyText.text = log == null || log.Count == 0
            ? "아직 기록된 대화가 없습니다."
            : string.Join("\n\n", log);
        lastRenderedText = historyText.text;
    }

    private void QueueRebuild(bool snapToLatest)
    {
        if (!isActiveAndEnabled)
            return;
        if (rebuildRoutine != null)
            StopCoroutine(rebuildRoutine);
        rebuildRoutine = StartCoroutine(RebuildNextFrames(snapToLatest));
    }

    private IEnumerator RebuildNextFrames(bool snapToLatest)
    {
        yield return null;
        ApplyLayout(snapToLatest);
        yield return new WaitForEndOfFrame();
        ApplyLayout(snapToLatest);
        rebuildRoutine = null;
    }

    private void ApplyLayout(bool snapToLatest)
    {
        if (historyText == null || viewport == null || content == null || scrollRect == null)
            return;

        Canvas.ForceUpdateCanvases();
        float viewportWidth = Mathf.Max(480f, viewport.rect.width);
        float viewportHeight = Mathf.Max(320f, viewport.rect.height);
        const float horizontalPadding = 28f;
        const float verticalPadding = 24f;
        float textWidth = Mathf.Max(320f, viewportWidth - horizontalPadding * 2f);
        float preferredHeight = Mathf.Max(80f,
            historyText.GetPreferredValues(historyText.text ?? string.Empty, textWidth, 0f).y);
        float contentHeight = Mathf.Max(viewportHeight, preferredHeight + verticalPadding * 2f);

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, contentHeight);

        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 1f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = new Vector2(0f, -verticalPadding);
        textRect.sizeDelta = new Vector2(-horizontalPadding * 2f, preferredHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);
        LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRoot);
        Canvas.ForceUpdateCanvases();

        if (snapToLatest)
        {
            scrollRect.StopMovement();
            scrollRect.velocity = Vector2.zero;
            scrollRect.verticalNormalizedPosition = 0f;
        }
    }

    private T GetDirectorField<T>(string name) where T : class
    {
        FieldInfo field = typeof(ScenarioV3Director).GetField(name, Fields);
        return field?.GetValue(director) as T;
    }

    private void SetDirectorField(string name, object value)
    {
        FieldInfo field = typeof(ScenarioV3Director).GetField(name, Fields);
        field?.SetValue(director, value);
    }

    private static GameObject FindSceneObject(string objectName)
    {
        foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (candidate != null && candidate.scene.IsValid() && candidate.name == objectName)
                return candidate;
        }
        return null;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
