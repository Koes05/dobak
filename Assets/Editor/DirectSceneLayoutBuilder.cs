using System.Reflection;
using System.IO;
using System.Linq;
using Dobak.App.Bank;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

[InitializeOnLoad]
public static class DirectSceneLayoutBuilder
{
    private const string TabletScenePath = "Assets/Tablet/TabletUI.unity";
    private const string SleepIconPath = "Assets/Resources/AppIcons/sleep.png";
    private const string SleepScreenPath = "Assets/Resources/SleepUI/sleep_screen.png";
    private const string SleepButtonPath = "Assets/Resources/SleepUI/sleep_button.png";
    private const string BankCardPath = "Assets/Resources/BankUI/account_card.png";
    private const string BankRowPath = "Assets/Resources/BankUI/transaction_row.png";

    static DirectSceneLayoutBuilder()
    {
        EditorApplication.delayCall += AutoBuildOnce;
    }

    [MenuItem("Tools/Dobak/Build Direct Scenes")]
    public static void BuildAll()
    {
        if (HasDirectLayout())
            return;

        IntroSceneBuilder.Build();
        EditorSceneManager.OpenScene(TabletScenePath, OpenSceneMode.Single);

        AppWindow appWindow = Object.FindAnyObjectByType<AppWindow>(FindObjectsInactive.Include);
        GameObject appManager = Find("AppManager");
        GameObject appArea = Find("AppUi");
        if (appWindow == null || appManager == null || appArea == null)
            throw new MissingReferenceException("TabletUI의 AppWindow/AppManager/AppUi를 찾지 못했습니다.");

        EnsureSprite(SleepIconPath);
        EnsureSprite(SleepScreenPath);
        EnsureSprite(SleepButtonPath);
        EnsureSprite(BankCardPath);
        EnsureSprite(BankRowPath);
        ArrangeLaunchers(appManager.transform, appWindow);
        GameObject sleepApp = BuildSleepApp(appArea.transform, appWindow);
        RegisterApp(appWindow, AppType.Sleep, sleepApp);
        RemoveLegacyActionBar();

        GameObject flowObject = Find("Game Flow Manager") ?? new GameObject("Game Flow Manager");
        GameFlowManager flow = flowObject.GetComponent<GameFlowManager>();
        if (flow == null)
            flow = flowObject.AddComponent<GameFlowManager>();
        ScenarioV3Director director = flowObject.GetComponent<ScenarioV3Director>();
        if (director == null)
            director = flowObject.AddComponent<ScenarioV3Director>();

        if (Find("Tutorial Hint") == null)
            InvokePrivate(flow, "CreateRuntimeUI");
        if (Find("Scenario V3 Novel") == null)
            InvokePrivate(director, "CreateNovelUI");

        GameFlowManager.StyleHomeAppLabels();
        foreach (BankUI bank in Object.FindObjectsByType<BankUI>(FindObjectsInactive.Include))
            bank.ApplyVisualDesign();

        GameObject oldMarker = Find("Direct Layout v4");
        if (oldMarker != null)
            Object.DestroyImmediate(oldMarker);
        GameObject marker = Find("Direct Layout v5") ?? new GameObject("Direct Layout v5");
        marker.transform.SetAsLastSibling();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), TabletScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[DirectSceneLayoutBuilder] Intro와 TabletUI 직접 배치를 저장했습니다.");
    }

    private static void AutoBuildOnce()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.delayCall += AutoBuildOnce;
            return;
        }
        if (HasDirectLayout())
            return;

        try
        {
            BuildAll();
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private static bool HasDirectLayout()
    {
        return File.Exists(TabletScenePath) && File.ReadAllText(TabletScenePath).Contains("m_Name: Direct Layout v5") &&
               File.Exists("Assets/Tablet/Intro.unity") && File.ReadAllText("Assets/Tablet/Intro.unity").Contains("m_Name: Intro Canvas");
    }

    private static void ArrangeLaunchers(Transform appManager, AppWindow appWindow)
    {
        Transform mapLauncher = appManager.Find("MapApp") ?? appManager.Find("Map Launcher");
        Transform browserLauncher = appManager.Find("BrowserApp");
        Transform unusedLauncher = appManager.Find("Button (6)");
        if (mapLauncher == null)
            throw new MissingReferenceException("지도 앱 런처를 찾지 못했습니다.");

        RectTransform mapRect = (RectTransform)mapLauncher;
        Transform oldSleep = appManager.Find("Sleep Launcher");
        Vector2 sleepPosition = oldSleep != null
            ? ((RectTransform)oldSleep).anchoredPosition
            : mapRect.anchoredPosition;
        if (browserLauncher != null)
        {
            RectTransform browserRect = (RectTransform)browserLauncher;
            sleepPosition = mapRect.anchoredPosition;
            mapRect.anchoredPosition = browserRect.anchoredPosition;
            Object.DestroyImmediate(browserLauncher.gameObject);
        }
        mapLauncher.name = "Map Launcher";
        SetLauncherLabel(mapLauncher, "지도");

        if (unusedLauncher != null)
            Object.DestroyImmediate(unusedLauncher.gameObject);

        if (oldSleep != null)
            Object.DestroyImmediate(oldSleep.gameObject);
        BuildSleepLauncher(appManager, appWindow, sleepPosition);
    }

    private static void BuildSleepLauncher(Transform parent, AppWindow appWindow, Vector2 position)
    {
        Sprite iconSprite = LoadSleepIcon();
        GameObject launcher = new GameObject("Sleep Launcher", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        launcher.layer = 5;
        launcher.transform.SetParent(parent, false);
        RectTransform rect = launcher.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(150f, 150f);
        Image image = launcher.GetComponent<Image>();
        image.sprite = iconSprite;
        image.preserveAspect = true;
        image.color = Color.white;
        Button button = launcher.GetComponent<Button>();
        button.targetGraphic = image;
        UnityEventTools.AddPersistentListener(button.onClick, appWindow.OpenSleep);

        TMP_Text label = Text("Sleep Label", launcher.transform, "취침", 25f, FontStyles.Bold, Color.white);
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0f);
        labelRect.pivot = new Vector2(0.5f, 1f);
        labelRect.anchoredPosition = new Vector2(0f, -9f);
        labelRect.sizeDelta = new Vector2(210f, 48f);
        Shadow shadow = label.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.72f);
        shadow.effectDistance = new Vector2(2f, -2f);
    }

    private static GameObject BuildSleepApp(Transform parent, AppWindow appWindow)
    {
        GameObject old = Find("Sleep App");
        if (old != null)
            Object.DestroyImmediate(old);

        TMP_FontAsset font = FindFont();
        GameObject root = Panel("Sleep App", parent, new Color(0.025f, 0.04f, 0.09f, 1f));
        Stretch(root.GetComponent<RectTransform>());

        GameObject artworkObject = new GameObject("Sleep Screen Artwork", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        artworkObject.layer = 5;
        artworkObject.transform.SetParent(root.transform, false);
        Image artwork = artworkObject.GetComponent<Image>();
        artwork.sprite = LoadSprite(SleepScreenPath);
        artwork.color = Color.white;
        artwork.raycastTarget = false;
        SetRect(artworkObject.GetComponent<RectTransform>(), new Vector2(0.015f, 0.01f), new Vector2(0.985f, 0.99f));

        TMP_Text status = Text("Sleep Status", root.transform, "주무시겠습니까?", 30f, FontStyles.Bold,
            new Color(0.91f, 0.93f, 1f), font);
        status.alignment = TextAlignmentOptions.Center;
        status.textWrappingMode = TextWrappingModes.Normal;
        SetRect(status.rectTransform, new Vector2(0.22f, 0.30f), new Vector2(0.78f, 0.42f));

        GameObject buttonObject = new GameObject("Sleep Now Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.layer = 5;
        buttonObject.transform.SetParent(root.transform, false);
        Image buttonImage = buttonObject.GetComponent<Image>();
        buttonImage.sprite = LoadSprite(SleepButtonPath);
        buttonImage.color = Color.white;
        Button sleepButton = buttonObject.GetComponent<Button>();
        sleepButton.targetGraphic = buttonImage;
        RectTransform buttonRect = sleepButton.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.30f, 0.12f);
        buttonRect.anchorMax = new Vector2(0.70f, 0.23f);
        buttonRect.offsetMin = buttonRect.offsetMax = Vector2.zero;
        TMP_Text buttonLabel = Text("Label", buttonObject.transform, "취침하기", 30f, FontStyles.Bold, Color.white, font);
        buttonLabel.alignment = TextAlignmentOptions.Center;
        Stretch(buttonLabel.rectTransform);

        SleepAppController controller = root.AddComponent<SleepAppController>();
        SerializedObject serialized = new SerializedObject(controller);
        serialized.FindProperty("sleepButton").objectReferenceValue = sleepButton;
        serialized.FindProperty("statusText").objectReferenceValue = status;
        serialized.FindProperty("appWindow").objectReferenceValue = appWindow;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        root.SetActive(false);
        return root;
    }

    private static Sprite LoadSleepIcon()
    {
        Sprite direct = AssetDatabase.LoadAssetAtPath<Sprite>(SleepIconPath);
        if (direct != null)
            return direct;
        return AssetDatabase.LoadAllAssetsAtPath(SleepIconPath).OfType<Sprite>().FirstOrDefault();
    }

    private static Sprite LoadSprite(string path)
    {
        Sprite direct = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        return direct != null ? direct : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
    }

    private static void EnsureSprite(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;
        if (importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Single)
            return;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
    }

    private static void RemoveLegacyActionBar()
    {
        GameObject legacy = Find("Daily Action Bar");
        if (legacy != null)
            Object.DestroyImmediate(legacy);
    }

    private static void SetLauncherLabel(Transform launcher, string labelText)
    {
        TMP_Text label = launcher.GetComponentInChildren<TMP_Text>(true);
        if (label == null)
            return;
        label.text = labelText;
        label.color = Color.white;
        label.fontStyle = FontStyles.Bold;
        Shadow shadow = label.GetComponent<Shadow>();
        if (shadow == null)
            shadow = label.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.72f);
        shadow.effectDistance = new Vector2(2f, -2f);
    }

    private static void RegisterApp(AppWindow appWindow, AppType type, GameObject app)
    {
        SerializedObject serialized = new SerializedObject(appWindow);
        SerializedProperty apps = serialized.FindProperty("apps");
        for (int i = 0; i < apps.arraySize; i++)
        {
            SerializedProperty item = apps.GetArrayElementAtIndex(i);
            if (item.FindPropertyRelative("appType").enumValueIndex != (int)type)
                continue;
            item.FindPropertyRelative("appUI").objectReferenceValue = app;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return;
        }
        int index = apps.arraySize;
        apps.InsertArrayElementAtIndex(index);
        SerializedProperty added = apps.GetArrayElementAtIndex(index);
        added.FindPropertyRelative("appType").enumValueIndex = (int)type;
        added.FindPropertyRelative("appUI").objectReferenceValue = app;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void InvokePrivate(object target, string method)
    {
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);
    }

    private static GameObject Find(string name)
    {
        foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            if (candidate.scene.IsValid() && candidate.name == name)
                return candidate;
        return null;
    }

    private static TMP_FontAsset FindFont()
    {
        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            if (font != null && font.name.Contains("NotoSansKR-Regular"))
                return font;
        return null;
    }

    private static GameObject Panel(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    private static TMP_Text Text(string name, Transform parent, string value, float size, FontStyles style, Color color,
        TMP_FontAsset font = null)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        TMP_Text text = go.GetComponent<TMP_Text>();
        text.font = font ?? FindFont();
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.raycastTarget = false;
        return text;
    }

    private static Button Button(string name, Transform parent, string label, TMP_FontAsset font, Color color)
    {
        GameObject go = Panel(name, parent, color);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        TMP_Text text = Text("Label", go.transform, label, 28f, FontStyles.Bold, Color.white, font);
        text.alignment = TextAlignmentOptions.Center;
        Stretch(text.rectTransform);
        return button;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private static void SetRect(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
