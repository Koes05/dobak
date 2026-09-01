#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.Editor
{
    [InitializeOnLoad]
    public static class ScenarioV3PlayQa
    {
        private const string MainScene = "Assets/Tablet/TabletUI.unity";
        private const string SessionKey = "Dobak.ScenarioV3Qa";
        private const string StrategyKey = "Dobak.ScenarioV3Qa.Strategy";
        private const string ProcessKey = "Dobak.ScenarioV3Qa.Process";
        private static bool chooseRisky;
        private static double startedAt;
        private static double sceneChangedAt;
        private static string lastScene = string.Empty;
        private static string lastLine = string.Empty;
        private static int captureIndex;
        private static bool failed;
        private static EnterPlayModeOptions previousOptions;
        private static bool previousOptionsEnabled;

        static ScenarioV3PlayQa()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void RunGood()
        {
            Start(false);
        }

        public static void RunRisky()
        {
            Start(true);
        }

        private static void Start(bool risky)
        {
            int process = System.Diagnostics.Process.GetCurrentProcess().Id;
            if (SessionState.GetBool(SessionKey, false) || EditorPrefs.GetInt(ProcessKey, -1) == process)
                return;

            chooseRisky = risky;
            failed = false;
            captureIndex = 0;
            lastScene = lastLine = string.Empty;
            EditorPrefs.SetBool(StrategyKey, risky);
            EditorPrefs.SetInt(ProcessKey, process);
            SessionState.SetBool(SessionKey, true);
            previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Directory.CreateDirectory(OutputDirectory(risky));
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false))
                return;

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                chooseRisky = EditorPrefs.GetBool(StrategyKey, false);
                startedAt = EditorApplication.timeSinceStartup;
                sceneChangedAt = startedAt;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
                SessionState.EraseBool(SessionKey);
                EditorPrefs.DeleteKey(ProcessKey);
                Debug.Log(failed
                    ? "[SCENARIO V3 QA] FAIL"
                    : $"[SCENARIO V3 QA] PASS ({(chooseRisky ? "risky" : "good")}) - {captureIndex} screenshots");
                EditorApplication.Exit(failed ? 2 : 0);
            }
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > 150d)
            {
                Fail("Timed out before reaching an ending.");
                EditorApplication.ExitPlaymode();
                return;
            }

            ScenarioV3Director director = UnityEngine.Object.FindAnyObjectByType<ScenarioV3Director>();
            GameFlowManager flow = GameFlowManager.Instance;
            if (director == null || flow == null || !director.IsReady)
                return;

            if (GameObject.Find("BrowserApp") != null)
                Fail("Removed gambling app became visible.");
            if (GameObject.Find("Runtime SNS App") != null || GameObject.Find("SNSApp") != null)
                Fail("Removed SNS app was created.");

            if (flow.IsGameEnded)
            {
                Capture($"{captureIndex + 1:00}-ending.png");
                Expect(flow.CurrentDay == 7, $"Ending occurred on day {flow.CurrentDay}, not day 7.");
                Expect(director.ChoiceHistory.Count >= 12,
                    $"Only {director.ChoiceHistory.Count} choices were persisted.");
                string savePath = Path.Combine(Application.persistentDataPath, "scenario_v3_history.json");
                Expect(File.Exists(savePath) && new FileInfo(savePath).Length > 500,
                    "Choice history save file was not written.");
                EditorApplication.ExitPlaymode();
                return;
            }

            string scene = director.ActiveSceneId;
            string line = director.ActiveLineId;
            if (string.IsNullOrEmpty(scene))
                return;
            if (scene != lastScene || line != lastLine)
            {
                lastScene = scene;
                lastLine = line;
                sceneChangedAt = EditorApplication.timeSinceStartup;
                return;
            }
            if (EditorApplication.timeSinceStartup - sceneChangedAt < 0.72d)
                return;

            if (captureIndex == 0 || !File.Exists(Path.Combine(OutputDirectory(chooseRisky), $"{captureIndex:00}-{Safe(scene)}.png")))
            {
                captureIndex++;
                Capture($"{captureIndex:00}-{Safe(scene)}.png");
            }

            var choices = director.CurrentChoices;
            if (choices.Count > 0)
            {
                ScenarioV3Choice selected = chooseRisky ? choices[choices.Count - 1] : choices[0];
                if (!ClickButtonWithText(selected.text))
                    return;
                sceneChangedAt = EditorApplication.timeSinceStartup;
                return;
            }

            Button continueButton = FindActiveButton("Continue");
            if (continueButton != null)
            {
                continueButton.onClick.Invoke();
                sceneChangedAt = EditorApplication.timeSinceStartup;
            }
        }

        private static bool ClickButtonWithText(string expected)
        {
            foreach (Button button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include))
            {
                if (!button.gameObject.activeInHierarchy || !button.interactable)
                    continue;
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                if (label != null && label.text == expected)
                {
                    button.onClick.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static Button FindActiveButton(string name)
        {
            return UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include)
                .FirstOrDefault(button => button.gameObject.activeInHierarchy && button.gameObject.name == name);
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                Fail(message);
        }

        private static void Fail(string message)
        {
            if (!failed)
                Debug.LogError("[SCENARIO V3 QA] " + message);
            failed = true;
        }

        private static string OutputDirectory(bool risky)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ScenarioV3Qa", risky ? "risky" : "good"));
        }

        private static string Safe(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        private static void Capture(string filename)
        {
            string path = Path.Combine(OutputDirectory(chooseRisky), filename);
            Camera camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
            Canvas canvas = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include)
                .FirstOrDefault(candidate => candidate.transform.parent == null) ?? UnityEngine.Object.FindAnyObjectByType<Canvas>();
            if (camera == null || canvas == null)
            {
                Fail("Camera or canvas was missing during capture.");
                return;
            }

            const int width = 1600;
            const int height = 1000;
            RenderMode oldMode = canvas.renderMode;
            Camera oldCanvasCamera = canvas.worldCamera;
            RenderTexture oldTarget = camera.targetTexture;
            RenderTexture oldActive = RenderTexture.active;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
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
                camera.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                canvas.renderMode = oldMode;
                canvas.worldCamera = oldCanvasCamera;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
#endif
