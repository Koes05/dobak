#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using Dobak.App.Bank;
using Dobak.App.Casino;
using Dobak.App.Casino.SlotMachine;
using Dobak.Manager;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.Editor
{
    [InitializeOnLoad]
    public static class FeatureRegressionQa
    {
        private const string MainScene = "Assets/Tablet/TabletUI.unity";
        private static bool running;
        private static bool failed;
        private static int step;
        private static double nextStepAt;
        private static int bankBeforeJob;
        private static bool previousOptionsEnabled;
        private static EnterPlayModeOptions previousOptions;

        static FeatureRegressionQa()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void Start()
        {
            if (running)
                return;

            running = true;
            failed = false;
            step = 0;
            previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!running)
                return;

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                nextStepAt = EditorApplication.timeSinceStartup + 1.7d;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
                running = false;
                Debug.Log(failed
                    ? "[FEATURE QA] FAIL - inspect preceding errors and screenshots."
                    : $"[FEATURE QA] PASS - SNS, quiz, charge, bet, bank font, and job flow verified in {LogDirectory}");
                EditorApplication.Exit(failed ? 2 : 0);
            }
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextStepAt)
                return;

            try
            {
                RunStep();
            }
            catch (Exception exception)
            {
                Fail($"Unhandled exception: {exception}");
                EditorApplication.ExitPlaymode();
            }
        }

        private static void RunStep()
        {
            GameFlowManager flow = GameFlowManager.Instance;
            AppWindow apps = UnityEngine.Object.FindAnyObjectByType<AppWindow>();
            CoinManager coins = CoinManager.Instance;

            switch (step)
            {
                case 0:
                    DismissNarration();
                    Expect(GameObject.Find("SNSApp") != null, "SNS home icon was not created.");
                    apps?.OpenSNS();
                    Next(1, 0.7d);
                    break;

                case 1:
                    Expect(GameObject.Find("Runtime SNS App")?.activeInHierarchy == true, "SNS app did not open.");
                    Expect(FindButton("SNS 2 Hour Button") != null, "SNS two-hour option is missing.");
                    DismissAllNarration();
                    Capture("feature-01-sns.png");
                    FindButton("SNS 2 Hour Button")?.onClick.Invoke();
                    Next(2, 0.5d);
                    break;

                case 2:
                    Expect(flow != null && flow.CurrentHour == 9, $"SNS did not add two hours: {flow?.CurrentHour}.");
                    DismissNarration();
                    SetPrivate(flow, "currentDay", 2);
                    SetPrivate(flow, "schoolDone", true);
                    SetPrivate(flow, "currentHour", 15);
                    InvokePrivate(flow, "RefreshUI");
                    UnityEngine.Object.FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include)?.ConfigureForDay(2, true);
                    apps?.OpenStudy();
                    Next(3, 0.7d);
                    break;

                case 3:
                    QuizManager quiz = UnityEngine.Object.FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include);
                    Button[] answers = GetPrivate<Button[]>(quiz, "answerButtons");
                    int activeAnswers = 0;
                    if (answers != null)
                    {
                        foreach (Button answer in answers)
                            if (answer != null && answer.gameObject.activeInHierarchy)
                                activeAnswers++;
                    }
                    Expect(activeAnswers >= 4, $"Day-two answer choices were hidden: {activeAnswers} active.");
                    Capture("feature-02-day2-quiz.png");
                    flow?.ResolveInvitation(true);
                    DismissAllNarration();
                    Expect(coins != null && coins.CasinoCash == 5000, $"Free points were not granted: {coins?.CasinoCash}P.");
                    Expect(coins != null && coins.TryChargeToCasino(5000, out _), "First 5,000 won charge failed.");
                    Expect(coins != null && coins.TryChargeToCasino(5000, out _), "Second 5,000 won charge failed.");
                    Expect(coins != null && coins.TryChargeToCasino(5000, out _), "Third 5,000 won charge failed.");
                    Expect(coins != null && coins.BankCash == 85000 && coins.CasinoCash == 6500,
                        $"Won-point conversion is wrong: bank {coins?.BankCash}, points {coins?.CasinoCash}.");
                    apps?.OpenBank();
                    Next(4, 0.7d);
                    break;

                case 4:
                    BankUI bank = UnityEngine.Object.FindAnyObjectByType<BankUI>(FindObjectsInactive.Include);
                    Expect(bank != null && HasKoreanFont(bank.transform), "Bank text is not using the Korean font asset.");
                    DismissAllNarration();
                    Capture("feature-03-bank-after-charges.png");
                    apps?.OpenBrowser();
                    Next(5, 0.7d);
                    break;

                case 5:
                    CasinoUIManager casino = UnityEngine.Object.FindAnyObjectByType<CasinoUIManager>(FindObjectsInactive.Include);
                    Expect(casino != null, "Casino UI manager is missing.");
                    DismissAllNarration();
                    Capture("feature-04-casino-home.png");
                    InvokePrivate(casino, "OnRechargeButtonClicked");
                    Next(6, 0.5d);
                    break;

                case 6:
                    DismissAllNarration();
                    Expect(VisibleTextContains("5,000원"), "5,000 won charge label is missing.");
                    Expect(VisibleTextContains("100,000원"), "100,000 won charge label is missing.");
                    Expect(!VisibleTextContains("$"), "Legacy dollar text is still visible.");
                    Capture("feature-05-charge-options.png");
                    InvokePrivate(UnityEngine.Object.FindAnyObjectByType<CasinoUIManager>(FindObjectsInactive.Include), "OnSlotMachineButtonClicked");
                    Next(7, 0.6d);
                    break;

                case 7:
                    SlotMachineManager slot = UnityEngine.Object.FindAnyObjectByType<SlotMachineManager>(FindObjectsInactive.Include);
                    Expect(slot != null && slot.CurrentBetAmount == 100, $"Initial bet is not 100P: {slot?.CurrentBetAmount}.");
                    FindButton("Increase Bet")?.onClick.Invoke();
                    Expect(slot != null && slot.CurrentBetAmount == 500, $"Bet did not change to 500P: {slot?.CurrentBetAmount}.");
                    Capture("feature-06-bet-500p.png");
                    Button spin = GetPrivate<Button>(slot, "spinButton");
                    spin?.onClick.Invoke();
                    Next(8, 4.0d);
                    break;

                case 8:
                    SlotMachineManager finishedSlot = UnityEngine.Object.FindAnyObjectByType<SlotMachineManager>(FindObjectsInactive.Include);
                    Button increase = FindButton("Increase Bet");
                    Expect(finishedSlot != null && finishedSlot.CurrentRound == 1, "Slot spin did not finish.");
                    Expect(increase != null && increase.interactable, "Bet control stayed disabled after the spin.");
                    Capture("feature-07-after-spin.png");
                    apps?.CloseCurrentApp();
                    SetPrivate(flow, "currentDay", 6);
                    SetPrivate(flow, "currentHour", 7);
                    SetPrivate(flow, "currentLocation", "집");
                    SetPrivate(flow, "jobDone", false);
                    InvokePrivate(flow, "RefreshUI");
                    bankBeforeJob = coins != null ? coins.BankCash : 0;
                    flow?.TravelTo("2");
                    Next(9, 1.5d);
                    break;

                case 9:
                    Expect(flow != null && flow.CurrentDay == 6 && flow.CurrentHour == 16 && flow.CurrentLocation == "카페",
                        $"Weekend job schedule is wrong: day {flow?.CurrentDay}, hour {flow?.CurrentHour}, place {flow?.CurrentLocation}.");
                    Expect(coins != null && coins.BankCash == bankBeforeJob + 80000,
                        $"Job wage is wrong: before {bankBeforeJob}, after {coins?.BankCash}.");
                    DismissAllNarration();
                    Capture("feature-08-weekend-job.png");
                    EditorApplication.ExitPlaymode();
                    break;
            }
        }

        private static void Next(int value, double delay)
        {
            step = value;
            nextStepAt = EditorApplication.timeSinceStartup + delay;
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                Fail(message);
        }

        private static void Fail(string message)
        {
            failed = true;
            Debug.LogError($"[FEATURE QA] {message}");
        }

        private static void DismissNarration()
        {
            Button button = FindButton("Narration Continue Button");
            if (button != null && button.gameObject.activeInHierarchy)
                button.onClick.Invoke();
        }

        private static void DismissAllNarration()
        {
            for (int i = 0; i < 12; i++)
            {
                Button button = FindButton("Narration Continue Button");
                if (button == null || !button.gameObject.activeInHierarchy)
                    return;
                button.onClick.Invoke();
            }
        }

        private static Button FindButton(string name)
        {
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
                if (button.gameObject.scene.IsValid() && button.gameObject.name == name)
                    return button;
            return null;
        }

        private static bool VisibleTextContains(string expected)
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (text != null && text.gameObject.scene.IsValid() && text.gameObject.activeInHierarchy && text.text?.Contains(expected) == true)
                    return true;
            return false;
        }

        private static bool HasKoreanFont(Transform root)
        {
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
                if (text.font == null || !text.font.name.Contains("NotoSansKR"))
                    return false;
            return true;
        }

        private static T GetPrivate<T>(object target, string fieldName) where T : class
        {
            return target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as T;
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] arguments)
        {
            target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, arguments);
        }

        private static string LogDirectory => Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));

        private static void Capture(string filename)
        {
            Camera camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
            Canvas canvas = FindRootCanvas();
            if (camera == null || canvas == null)
            {
                Fail($"Could not capture {filename}.");
                return;
            }

            const int width = 1920;
            const int height = 1200;
            RenderMode oldMode = canvas.renderMode;
            Camera oldCanvasCamera = canvas.worldCamera;
            RenderTexture oldTarget = camera.targetTexture;
            RenderTexture oldActive = RenderTexture.active;
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            try
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllBytes(Path.Combine(LogDirectory, filename), texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                canvas.renderMode = oldMode;
                canvas.worldCamera = oldCanvasCamera;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static Canvas FindRootCanvas()
        {
            Canvas fallback = null;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                fallback ??= canvas;
                if (canvas.transform.parent == null)
                    return canvas;
            }
            return fallback;
        }
    }
}
#endif
