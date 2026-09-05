using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Owns the visible VN "지난 대화" list as individual TMP entries inside one ScrollRect.
/// ScenarioV3Director's legacy single History Text stays hidden and is used only as a compatibility
/// target for its old ShowHistory method. Each dialogue entry gets its own layout height, so the
/// viewport mask and ScrollRect can calculate clipping/scrolling deterministically.
/// </summary>
[DefaultExecutionOrder(20000)]
public sealed class ScenarioV3HistoryRuntimeFix : MonoBehaviour
{
    private const string TabletSceneName = "TabletUI";
    private const string EntryContentName = "History Entries Content V23";
    private const string EntryPrefix = "History Entry V23 ";
    private static readonly BindingFlags Fields =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private ScenarioV3Director director;
    private GameObject historyPanel;
    private RectTransform viewport;
    private RectTransform legacyContent;
    private TMP_Text legacyText;
    private RectTransform entryContent;
    private ScrollRect scrollRect;
    private TMP_FontAsset font;
    private Color textColor = new Color(0.9f, 0.93f, 0.98f, 1f);
    private float fontSize = 25f;

    private bool wasOpen;
    private string lastSignature = string.Empty;
    private Vector2 lastViewportSize;
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

        new GameObject("Scenario V3 History Entry Scroll V23")
            .AddComponent<ScenarioV3HistoryRuntimeFix>();
    }

    private IEnumerator Start()
    {
        float timeoutAt = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeoutAt)
        {
            director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
            if (director != null && director.IsReady && ResolveUi())
                break;
            yield return null;
        }

        if (director == null || !ResolveUi())
        {
            Debug.LogError("[V23 History] 지난 대화 UI를 찾지 못했습니다.");
            enabled = false;
            yield break;
        }

        ConfigureViewportAndContent();
        RebuildEntries(false);
    }

    private void LateUpdate()
    {
        if (!ResolveUi())
            return;

        bool open = historyPanel.activeInHierarchy;
        List<string> log = GetDialogueLog();
        string signature = BuildSignature(log);
        Vector2 size = viewport.rect.size;

        bool changed = !string.Equals(signature, lastSignature, StringComparison.Ordinal);
        bool resized = (size - lastViewportSize).sqrMagnitude > 0.5f;

        if (open && (!wasOpen || changed || resized))
            QueueRebuild(!wasOpen);

        wasOpen = open;
        lastSignature = signature;
        lastViewportSize = size;
    }

    private bool ResolveUi()
    {
        if (director == null)
            return false;

        historyPanel = GetDirectorField<GameObject>("historyPanel") ?? historyPanel;
        if (historyPanel == null)
            historyPanel = FindSceneObject("Dialogue History");
        if (historyPanel == null)
            return false;

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

        legacyContent = GetDirectorField<RectTransform>("historyContentRect");
        if (legacyContent == null)
            legacyContent = viewport.Find("History Content") as RectTransform;

        legacyText = GetDirectorField<TMP_Text>("historyText");
        if (legacyText == null && legacyContent != null)
            legacyText = legacyContent.Find("History Text")?.GetComponent<TMP_Text>();

        if (legacyText != null)
        {
            font = legacyText.font;
            textColor = legacyText.color;
            fontSize = Mathf.Max(24f, legacyText.fontSize);
        }

        scrollRect = viewport.GetComponent<ScrollRect>();
        if (scrollRect == null)
            scrollRect = viewport.gameObject.AddComponent<ScrollRect>();

        Transform existing = viewport.Find(EntryContentName);
        if (existing != null)
            entryContent = existing as RectTransform;

        return true;
    }

    private void ConfigureViewportAndContent()
    {
        if (viewport == null)
            return;

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

        // The old single TMP text is kept only because ScenarioV3Director.ShowHistory writes to it.
        // It must never render on top of the V23 entries.
        if (legacyContent != null)
            legacyContent.gameObject.SetActive(false);

        foreach (TMP_Text candidate in historyPanel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (candidate == null)
                continue;
            if (candidate.gameObject.name.StartsWith("History Text", StringComparison.OrdinalIgnoreCase))
                candidate.gameObject.SetActive(false);
        }

        if (entryContent == null)
        {
            GameObject contentObject = new GameObject(
                EntryContentName,
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            contentObject.layer = historyPanel.layer;
            contentObject.transform.SetParent(viewport, false);
            entryContent = contentObject.GetComponent<RectTransform>();
        }

        entryContent.gameObject.SetActive(true);
        entryContent.anchorMin = new Vector2(0f, 1f);
        entryContent.anchorMax = new Vector2(1f, 1f);
        entryContent.pivot = new Vector2(0.5f, 1f);
        entryContent.anchoredPosition = Vector2.zero;
        entryContent.sizeDelta = Vector2.zero;

        VerticalLayoutGroup layout = entryContent.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(30, 30, 26, 30);
        layout.spacing = 22f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = entryContent.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        foreach (Canvas nested in entryContent.GetComponentsInChildren<Canvas>(true))
            nested.enabled = false;

        scrollRect.viewport = viewport;
        scrollRect.content = entryContent;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.12f;
        scrollRect.scrollSensitivity = 45f;
        scrollRect.enabled = true;

        // Prevent the Director's old RefreshHistoryLayout from replacing our ScrollRect.content
        // with the hidden legacy single-text content.
        SetDirectorField("historyScroll", null);
    }

    private void QueueRebuild(bool snapLatest)
    {
        if (!isActiveAndEnabled)
            return;
        if (rebuildRoutine != null)
            StopCoroutine(rebuildRoutine);
        rebuildRoutine = StartCoroutine(RebuildAfterLayout(snapLatest));
    }

    private IEnumerator RebuildAfterLayout(bool snapLatest)
    {
        yield return null;
        RebuildEntries(snapLatest);
        yield return new WaitForEndOfFrame();
        RebuildEntryHeights();
        if (snapLatest)
            SnapToLatest();
        rebuildRoutine = null;
    }

    private void RebuildEntries(bool snapLatest)
    {
        if (!ResolveUi())
            return;

        ConfigureViewportAndContent();
        List<string> log = GetDialogueLog();

        for (int index = entryContent.childCount - 1; index >= 0; index--)
            Destroy(entryContent.GetChild(index).gameObject);

        if (log.Count == 0)
        {
            CreateEntry("아직 기록된 대화가 없습니다.", 0);
        }
        else
        {
            for (int index = 0; index < log.Count; index++)
                CreateEntry(log[index], index);
        }

        Canvas.ForceUpdateCanvases();
        RebuildEntryHeights();
        LayoutRebuilder.ForceRebuildLayoutImmediate(entryContent);
        Canvas.ForceUpdateCanvases();

        if (snapLatest)
            SnapToLatest();

        lastSignature = BuildSignature(log);
    }

    private void CreateEntry(string text, int index)
    {
        GameObject entry = new GameObject(
            EntryPrefix + index.ToString("000"),
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI),
            typeof(LayoutElement));
        entry.layer = historyPanel.layer;
        entry.transform.SetParent(entryContent, false);

        TextMeshProUGUI tmp = entry.GetComponent<TextMeshProUGUI>();
        tmp.font = font;
        tmp.fontSize = fontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.maskable = true;
        tmp.raycastTarget = false;
        tmp.text = text ?? string.Empty;

        RectTransform rect = tmp.rectTransform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = Vector2.zero;
    }

    private void RebuildEntryHeights()
    {
        if (entryContent == null || viewport == null)
            return;

        Canvas.ForceUpdateCanvases();
        float availableWidth = Mathf.Max(360f, viewport.rect.width - 60f);

        for (int index = 0; index < entryContent.childCount; index++)
        {
            RectTransform child = entryContent.GetChild(index) as RectTransform;
            if (child == null)
                continue;

            TMP_Text tmp = child.GetComponent<TMP_Text>();
            LayoutElement element = child.GetComponent<LayoutElement>();
            if (tmp == null || element == null)
                continue;

            float preferred = Mathf.Max(48f,
                tmp.GetPreferredValues(tmp.text ?? string.Empty, availableWidth, 0f).y + 6f);
            element.minHeight = preferred;
            element.preferredHeight = preferred;
            element.flexibleHeight = 0f;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(entryContent);
        Canvas.ForceUpdateCanvases();
    }

    private void SnapToLatest()
    {
        if (scrollRect == null)
            return;

        scrollRect.StopMovement();
        scrollRect.velocity = Vector2.zero;
        scrollRect.verticalNormalizedPosition = 0f;
    }

    private List<string> GetDialogueLog()
    {
        FieldInfo field = typeof(ScenarioV3Director).GetField("dialogueLog", Fields);
        return field?.GetValue(director) as List<string> ?? new List<string>();
    }

    private static string BuildSignature(List<string> log)
    {
        if (log == null || log.Count == 0)
            return "0";
        string last = log[log.Count - 1] ?? string.Empty;
        return log.Count + "|" + last.GetHashCode() + "|" + last.Length;
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
