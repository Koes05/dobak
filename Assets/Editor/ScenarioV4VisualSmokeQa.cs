#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ScenarioV4VisualSmokeQa
{
    private const string IntroScene = "Assets/Tablet/Intro.unity";
    private static int phase;
    private static double nextActionAt;
    private static bool failed;
    private static bool previousOptionsEnabled;
    private static EnterPlayModeOptions previousOptions;
    private static double startedAt;

    public static void Run()
    {
        phase = 0;
        failed = false;
        previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        previousOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene(IntroScene, OpenSceneMode.Single);
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            startedAt = EditorApplication.timeSinceStartup;
            nextActionAt = EditorApplication.timeSinceStartup + 1.2d;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousOptions;
            Debug.Log(failed ? "[SCENARIO V4 VISUAL QA] FAIL" : "[SCENARIO V4 VISUAL QA] PASS");
            EditorApplication.Exit(failed ? 2 : 0);
        }
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup - startedAt > 90d)
        {
            Debug.LogError("[SCENARIO V4 VISUAL QA] Timed out.");
            failed = true;
            EditorApplication.ExitPlaymode();
            return;
        }
        if (EditorApplication.timeSinceStartup < nextActionAt)
            return;

        switch (phase)
        {
            case 0:
                Capture("00-intro.png");
                ClickNamedButton("Start Game");
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 2d;
                break;
            case 1:
            {
                ScenarioV3Director director = Object.FindAnyObjectByType<ScenarioV3Director>();
                if (director == null || !director.IsReady)
                    return;
                Capture("01-day1-first-dialogue.png");
                ClickNovelContinue(director);
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.8d;
                break;
            }
            case 2:
            {
                ScenarioV3Director director = Object.FindAnyObjectByType<ScenarioV3Director>();
                if (director != null && !string.IsNullOrEmpty(director.ActiveSceneId))
                {
                    ClickNovelContinue(director);
                    nextActionAt = EditorApplication.timeSinceStartup + 0.45d;
                    return;
                }
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.5d;
                break;
            }
            case 3:
            {
                ScenarioV3Director director = Object.FindAnyObjectByType<ScenarioV3Director>();
                if (director != null && !string.IsNullOrEmpty(director.ActiveSceneId))
                    return;
                Capture("02-tablet-home.png");
                GameFlowManager flow = Object.FindAnyObjectByType<GameFlowManager>();
                flow?.V3AddCash(20000, "카페 아르바이트 급여");
                flow?.V3AddCash(-4800, "편의점 결제");
                Object.FindAnyObjectByType<AppWindow>()?.OpenBank();
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.8d;
                break;
            }
            case 4:
                Capture("03-bank.png");
                Object.FindAnyObjectByType<AppWindow>()?.OpenMap();
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.8d;
                break;
            case 5:
                Capture("04-map.png");
                Object.FindAnyObjectByType<AppWindow>()?.OpenMessage();
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.8d;
                break;
            case 6:
                Capture("05-message.png");
                Object.FindAnyObjectByType<AppWindow>()?.OpenSleep();
                phase++;
                nextActionAt = EditorApplication.timeSinceStartup + 0.8d;
                break;
            case 7:
                Capture("06-sleep.png");
                EditorApplication.ExitPlaymode();
                break;
        }
    }

    private static void ClickNamedButton(string name)
    {
        Button button = Object.FindObjectsByType<Button>(FindObjectsInactive.Include)
            .FirstOrDefault(candidate => candidate.gameObject.activeInHierarchy && candidate.gameObject.name == name);
        if (button == null)
        {
            Debug.LogError($"[SCENARIO V4 VISUAL QA] Button missing: {name}");
            failed = true;
            return;
        }
        button.onClick.Invoke();
    }

    private static void ClickNovelContinue(ScenarioV3Director director)
    {
        Button button = director?.GetType()
            .GetField("continueButton", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(director) as Button;
        if (button == null)
        {
            Debug.LogError("[SCENARIO V4 VISUAL QA] Novel continue button missing.");
            failed = true;
            return;
        }
        button.onClick.Invoke();
    }

    private static void Capture(string filename)
    {
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ScenarioV4VisualQa"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename);
        Camera camera = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        Canvas canvas = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include)
            .FirstOrDefault(candidate => candidate.gameObject.activeInHierarchy && candidate.transform.parent == null);
        if (camera == null || canvas == null)
        {
            Debug.LogError("[SCENARIO V4 VISUAL QA] Camera or root canvas missing.");
            failed = true;
            return;
        }

        const int width = 1920;
        const int height = 1080;
        RenderMode previousMode = canvas.renderMode;
        Camera previousCamera = canvas.worldCamera;
        RenderTexture previousTarget = camera.targetTexture;
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            canvas.renderMode = previousMode;
            canvas.worldCamera = previousCamera;
            Object.DestroyImmediate(texture);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
