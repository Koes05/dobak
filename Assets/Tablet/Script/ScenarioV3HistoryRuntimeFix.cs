using System;
using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Repairs the single VN history ScrollRect already owned by ScenarioV3Director.
/// It does not create a second viewport or a second text object.
/// </summary>
[DefaultExecutionOrder(20000)]
public sealed class ScenarioV3HistoryRuntimeFix : MonoBehaviour
{
    private const string TabletSceneName = "TabletUI";
    private static readonly BindingFlags Fields =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private ScenarioV3Director director;
    private GameObject historyPanel;
    private RectTransform viewport;
    private RectTransform content;
    private TMP_Text historyText;
    private ScrollRect scrollRect;
    private string lastText = string.Empty;
    private Vector2 lastViewportSize;
    private bool wasOpen;
    private Coroutine rebuildRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        TryInstall(SceneManager.GetActiveScene());
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryInstall(scene);
    }

    private static void TryInstall(Scene scene)
    {
        if (!string.Equals(scene.name, TabletSceneName, StringComparison.Ordinal))
            return;
        if (FindAnyObjectByType<ScenarioV3HistoryRuntimeFix>(FindObjectsInactive.Include) != null)
            return;

        new GameObject("Scenario V3 History Single Viewport Fix V22")
            .AddComponent<ScenarioV3HistoryRuntimeFix>();
    }

    private IEnumerator Start()
    {
        float timeoutAt = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeoutAt)
        {
            director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
            if (director != null && director.IsReady && ResolveExistingHistoryUi())
                break;
            yield return null;
        }

        if (director == null || !ResolveExistingHistoryUi())
        {
            Debug.LogError("[V22 History] 기존 지난 대화 Scroll View를 찾지 못했습니다.");
            enabled = false;
            yield break;
        }

        ConfigureSingleViewport();
        QueueRebuild(false);
    }

    private void LateUpdate()
    {
        if (!ResolveExistingHistoryUi())
            return;

        bool open = historyPanel.activeInHierarchy;
        Vector2 size = viewport.rect.size;
        string text = historyText.text ?? string.Empty;
        bool changed = !string.Equals(lastText, text, StringComparison.Ordinal);
        bool resized = (size - lastViewportSize).sqrMagnitude > 0.5f;

        if (open && (!wasOpen || changed || resized))
            QueueRebuild(!wasOpen);

        wasOpen = open;
        lastText = text;
        lastViewportSize = size;
    }

    private bool ResolveExistingHistoryUi()
    {
        if (director == null)
            return false;

        historyPanel = GetDirectorField<GameObject>("historyPanel") ?? historyPanel;
        if (historyPanel == null)
            historyPanel = FindSceneObject("Dialogue History");
        if (historyPanel == null)
            return false;

        // Disable the second runtime hierarchy made by V21 so only the Director-owned view renders.
        foreach (Transform child in historyPanel.transform)
        {
            if (child == null)
                continue;
            if (child.name.IndexOf("History Scroll View V21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                child.name.IndexOf("History Scroll Root", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                child.gameObject.SetActive(false);
            }
        }

        viewport = GetDirectorField<RectTransform>("historyViewportRect");
        if (viewport == null)
            viewport = historyPanel.transform.Find("History Viewport") as RectTransform;
        if (viewport == null)
            return false;

        content = GetDirectorField<RectTransform>("historyContentRect");
        if (content == null)
            content = viewport.Find("History Content") as RectTransform;
        if (content == null)
            return false;

        historyText = GetDirectorField<TMP_Text>("historyText");
        if (historyText == null || historyText.transform.parent != content)
            historyText = content.Find("History Text")?.GetComponent<TMP_Text>();
        if (historyText == null)
            return false;

        scrollRect = GetDirectorField<ScrollRect>("historyScroll");
        if (scrollRect == null)
            scrollRect = viewport.GetComponent<ScrollRect>();
        if (scrollRect == null)
            scrollRect = viewport.gameObject.AddComponent<ScrollRect>();

        return true;
    }

    private void ConfigureSingleViewport()
    {
        // Match the visible, slightly brighter rectangle. The old 0.08 lower anchor extended the
        // clipping region below the visible rectangle, which looked like unmasked text.
        viewport.anchorMin = new Vector2(0.06f, 0.14f);
        viewport.anchorMax = new Vector2(0.94f, 0.86f);
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;
        viewport.pivot = new Vector2(0.5f, 0.5f);

        Image image = viewport.GetComponent<Image>();
        if (image == null)
            image = viewport.gameObject.AddComponent<Image>();
        image.color = new Color(0.04f, 0.065f, 0.10f, 1f);
        image.raycastTarget = true;
        image.maskable = true;

        Mask stencil = viewport.GetComponent<Mask>();
        if (stencil != null)
            stencil.enabled = false;
        RectMask2D mask = viewport.GetComponent<RectMask2D>();
        if (mask == null)
            mask = viewport.gameObject.AddComponent<RectMask2D>();
        mask.enabled = true;
        mask.padding = Vector4.zero;

        foreach (Canvas nestedCanvas in content.GetComponentsInChildren<Canvas>(true))
            nestedCanvas.enabled = false;

        foreach (TMP_Text candidate in historyPanel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (candidate == null || candidate == historyText)
                continue;
            if (candidate.gameObject.name.StartsWith("History Text", StringComparison.OrdinalIgnoreCase))
                candidate.gameObject.SetActive(false);
        }

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;

        VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
            layout.enabled = false;
        ContentSizeFitter contentFitter = content.GetComponent<ContentSizeFitter>();
        if (contentFitter != null)
            contentFitter.enabled = false;
        ContentSizeFitter textFitter = historyText.GetComponent<ContentSizeFitter>();
        if (textFitter != null)
            textFitter.enabled = false;

        historyText.gameObject.SetActive(true);
        historyText.alignment = TextAlignmentOptions.TopLeft;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        historyText.maskable = true;
        historyText.raycastTarget = false;

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
        SetDirectorField("historyText", historyText);
        SetDirectorField("historyScroll", scrollRect);
    }

    private void QueueRebuild(bool showLatest)
    {
        if (!isActiveAndEnabled)
            return;
        if (rebuildRoutine != null)
            StopCoroutine(rebuildRoutine);
        rebuildRoutine = StartCoroutine(RebuildAfterLayout(showLatest));
    }

    private IEnumerator RebuildAfterLayout(bool showLatest)
    {
        yield return null;
        Rebuild(showLatest);
        yield return new WaitForEndOfFrame();
        Rebuild(showLatest);
        rebuildRoutine = null;
    }

    private void Rebuild(bool showLatest)
    {
        if (!ResolveExistingHistoryUi())
            return;

        ConfigureSingleViewport();
        Canvas.ForceUpdateCanvases();

        float viewportWidth = Mathf.Max(480f, viewport.rect.width);
        float viewportHeight = Mathf.Max(320f, viewport.rect.height);
        const float sidePadding = 30f;
        const float topBottomPadding = 24f;
        float textWidth = Mathf.Max(320f, viewportWidth - sidePadding * 2f);
        float preferredHeight = Mathf.Max(80f,
            historyText.GetPreferredValues(historyText.text ?? string.Empty, textWidth, 0f).y);
        float contentHeight = Mathf.Max(viewportHeight, preferredHeight + topBottomPadding * 2f);

        content.sizeDelta = new Vector2(0f, contentHeight);
        content.anchoredPosition = Vector2.zero;

        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 1f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = new Vector2(0f, -topBottomPadding);
        textRect.sizeDelta = new Vector2(-sidePadding * 2f, preferredHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);
        Canvas.ForceUpdateCanvases();

        if (showLatest)
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
}
