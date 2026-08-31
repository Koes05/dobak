#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using System.Reflection;

namespace Dobak.Editor
{
    [InitializeOnLoad]
    public static class PlayModeCapture
    {
        private const string SessionKey = "Dobak.CodexCapture";
        private const string MainScene = "Assets/Tablet/TabletUI.unity";
        private static double enteredPlayAt;
        private static int captureStep;
        private static bool qaFailed;

        static PlayModeCapture()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void StartCapture()
        {
            SessionState.SetBool(SessionKey, true);
            captureStep = 0;
            qaFailed = false;
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false))
                return;

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                enteredPlayAt = EditorApplication.timeSinceStartup;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                SessionState.EraseBool(SessionKey);

                string screenshot = GetScreenshotPath("codex-play.png");
                string messageScreenshot = GetScreenshotPath("codex-message.png");
                string studyLockedScreenshot = GetScreenshotPath("codex-study-locked.png");
                string mapScreenshot = GetScreenshotPath("codex-map.png");
                string fadeScreenshot = GetScreenshotPath("codex-fade.png");
                string travelScreenshot = GetScreenshotPath("codex-after-travel.png");
                string studyScreenshot = GetScreenshotPath("codex-study.png");
                string casinoScreenshot = GetScreenshotPath("codex-casino.png");
                string slotScreenshot = GetScreenshotPath("codex-slot.png");
                string winScreenshot = GetScreenshotPath("codex-win.png");
                string sleepFadeScreenshot = GetScreenshotPath("codex-sleep-fade.png");
                string nextDayScreenshot = GetScreenshotPath("codex-next-day.png");
                string endingScreenshot = GetScreenshotPath("codex-ending.png");
                if (!qaFailed && File.Exists(screenshot) && File.Exists(messageScreenshot) && File.Exists(studyLockedScreenshot) &&
                    File.Exists(mapScreenshot) && File.Exists(fadeScreenshot) &&
                    File.Exists(travelScreenshot) && File.Exists(studyScreenshot) &&
                    File.Exists(casinoScreenshot) && File.Exists(slotScreenshot) && File.Exists(winScreenshot) &&
                    File.Exists(sleepFadeScreenshot) && File.Exists(nextDayScreenshot) && File.Exists(endingScreenshot))
                {
                    Debug.Log($"[PLAY QA] PASS - stored message, study lock, map art, travel, study, login-free casino, sleep, ending, and restart captured in {Path.GetDirectoryName(screenshot)}");
                    EditorApplication.Exit(0);
                }
                else
                {
                    Debug.LogError("[PLAY QA] Screenshot was not created.");
                    EditorApplication.Exit(2);
                }
            }
        }

        private static void Tick()
        {
            double elapsed = EditorApplication.timeSinceStartup - enteredPlayAt;
            if (captureStep == 0 && elapsed >= 1.5)
            {
                int activeContacts = 0;
                foreach (ProfileSlot slot in UnityEngine.Object.FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (slot.gameObject.activeSelf)
                        activeContacts++;
                }
                if (activeContacts != 2)
                {
                    qaFailed = true;
                    Debug.LogError($"[PLAY QA] Expected exactly two initial contacts, found {activeContacts}.");
                }
                if (GameObject.Find("BrowserApp") != null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Gambling app icon is visible before Minjae's link is accepted.");
                }
                CaptureGameView(GetScreenshotPath("codex-play.png"));
                captureStep = 1;
            }

            if (captureStep == 1 && elapsed >= 2.0)
            {
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenMessage();
                captureStep = 2;
            }

            if (captureStep == 2 && elapsed >= 2.65)
            {
                UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include)?.OpenDialogue(SpeakerType.Friend);
                captureStep = 21;
            }

            if (captureStep == 21 && elapsed >= 3.05)
            {
                CaptureGameView(GetScreenshotPath("codex-message.png"));
                DialogueManager dialogueManager = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                dialogueManager?.CloseDialogue();
                VerifyContactInsertionAndReordering(dialogueManager);
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenStudy();
                captureStep = 3;
            }

            if (captureStep == 3 && elapsed >= 3.45)
            {
                CaptureGameView(GetScreenshotPath("codex-study-locked.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenMap();
                captureStep = 4;
            }

            if (captureStep == 4 && elapsed >= 4.3)
            {
                CaptureGameView(GetScreenshotPath("codex-map.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                GameFlowManager.Instance?.TravelTo("학교");
                captureStep = 5;
            }

            if (captureStep == 5 && elapsed >= 4.48)
            {
                CaptureGameView(GetScreenshotPath("codex-fade.png"));
                captureStep = 6;
            }

            if (captureStep == 6 && elapsed >= 5.55)
            {
                CaptureGameView(GetScreenshotPath("codex-after-travel.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenStudy();
                captureStep = 7;
            }

            if (captureStep == 7 && elapsed >= 6.45)
            {
                CaptureGameView(GetScreenshotPath("codex-study.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                GameFlowManager.Instance?.ResolveInvitation(true);
                if (Dobak.Manager.CoinManager.Instance == null || Dobak.Manager.CoinManager.Instance.CasinoCash != 5000)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Accepting Minjae's link did not grant 5,000P.");
                }
                if (GameObject.Find("BrowserApp") == null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Gambling app icon did not appear after Minjae's link was accepted.");
                }
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenBrowser();
                captureStep = 8;
            }

            if (captureStep == 8 && elapsed >= 7.4)
            {
                CaptureGameView(GetScreenshotPath("codex-casino.png"));
                object casino = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Casino.CasinoUIManager>();
                MethodInfo openSlot = casino?.GetType().GetMethod("OnSlotMachineButtonClicked", BindingFlags.Instance | BindingFlags.NonPublic);
                openSlot?.Invoke(casino, null);
                captureStep = 9;
            }

            if (captureStep == 9 && elapsed >= 7.75)
            {
                var slot = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Casino.SlotMachine.SlotMachineManager>();
                GameObject increaseBet = GameObject.Find("Increase Bet");
                increaseBet?.GetComponent<Button>()?.onClick.Invoke();
                if (slot == null || slot.CurrentBetAmount != 500)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Bet amount did not change from 100P to 500P.");
                }
                FieldInfo spinButtonField = typeof(Dobak.App.Casino.SlotMachine.SlotMachineManager).GetField("spinButton", BindingFlags.Instance | BindingFlags.NonPublic);
                Button spinButton = spinButtonField?.GetValue(slot) as Button;
                if (spinButton == null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Slot spin button was not available.");
                }
                else
                {
                    spinButton.onClick.Invoke();
                }
                captureStep = 10;
            }

            if (captureStep == 10 && elapsed >= 9.95)
            {
                var slot = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Casino.SlotMachine.SlotMachineManager>();
                if (slot == null || slot.CurrentRound != 1)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] A complete slot spin did not resolve.");
                }
                CaptureGameView(GetScreenshotPath("codex-slot.png"));
                MethodInfo celebration = typeof(Dobak.App.Casino.SlotMachine.SlotMachineManager)
                    .GetMethod("ShowWinCelebration", BindingFlags.Instance | BindingFlags.NonPublic);
                slot?.StartCoroutine((System.Collections.IEnumerator)celebration?.Invoke(slot, new object[] { 1500 }));
                captureStep = 101;
            }

            if (captureStep == 101 && elapsed >= 10.25)
            {
                CaptureGameView(GetScreenshotPath("codex-win.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                GameObject.Find("Sleep Button")?.GetComponent<Button>()?.onClick.Invoke();
                captureStep = 11;
            }

            if (captureStep == 11 && elapsed >= 10.43)
            {
                CaptureGameView(GetScreenshotPath("codex-sleep-fade.png"));
                captureStep = 12;
            }

            if (captureStep == 12 && elapsed >= 11.65)
            {
                CaptureGameView(GetScreenshotPath("codex-next-day.png"));
                MethodInfo endGame = typeof(GameFlowManager).GetMethod("EndGame", BindingFlags.Instance | BindingFlags.NonPublic);
                endGame?.Invoke(GameFlowManager.Instance, new object[] { "QA 엔딩", "재시작 버튼 표시를 확인합니다." });
                captureStep = 13;
            }

            if (captureStep == 13 && elapsed >= 12.0)
            {
                CaptureGameView(GetScreenshotPath("codex-ending.png"));
                if (GameObject.Find("Restart Button") == null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Restart button was not created.");
                }
                captureStep = 14;
            }

            if (captureStep == 14 && elapsed >= 12.35)
                EditorApplication.ExitPlaymode();
        }

        private static void VerifyContactInsertionAndReordering(DialogueManager dialogueManager)
        {
            if (dialogueManager == null)
            {
                qaFailed = true;
                Debug.LogError("[PLAY QA] Dialogue manager was not available for contact ordering QA.");
                return;
            }

            dialogueManager.ReceiveNotificationMessage(SpeakerType.Teacher, "담임 선생님", "오늘 안내 사항을 확인해 주세요.");
            ProfileSlot teacherSlot = FindProfileSlot(SpeakerType.Teacher);
            ProfileSlot friendSlot = FindProfileSlot(SpeakerType.Friend);
            ProfileSlot momSlot = FindProfileSlot(SpeakerType.Mom);
            if (teacherSlot == null || friendSlot == null || momSlot == null ||
                teacherSlot.GetComponent<RectTransform>().anchoredPosition.y >=
                Mathf.Min(friendSlot.GetComponent<RectTransform>().anchoredPosition.y,
                    momSlot.GetComponent<RectTransform>().anchoredPosition.y))
            {
                qaFailed = true;
                Debug.LogError("[PLAY QA] A new contact was not appended below the existing contacts.");
                return;
            }

            dialogueManager.ReceiveNotificationMessage(SpeakerType.Teacher, "담임 선생님", "새 안내가 도착했습니다.");
            if (teacherSlot.GetComponent<RectTransform>().anchoredPosition.y <=
                Mathf.Max(friendSlot.GetComponent<RectTransform>().anchoredPosition.y,
                    momSlot.GetComponent<RectTransform>().anchoredPosition.y))
            {
                qaFailed = true;
                Debug.LogError("[PLAY QA] A contact with a new alert did not move to the top.");
            }
        }

        private static ProfileSlot FindProfileSlot(SpeakerType speaker)
        {
            foreach (ProfileSlot slot in UnityEngine.Object.FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (slot.gameObject.activeSelf && slot.speakerType == speaker)
                    return slot;
            }

            return null;
        }

        private static string GetScreenshotPath(string filename)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs", filename));
        }

        private static void CaptureGameView(string path)
        {
            Camera camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
            Canvas canvas = FindRootCanvas();
            if (camera == null || canvas == null)
                return;

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
                File.WriteAllBytes(path, texture.EncodeToPNG());
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
            foreach (Canvas candidate in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                fallback ??= candidate;
                if (candidate.transform.parent == null && candidate.renderMode == RenderMode.ScreenSpaceOverlay)
                    return candidate;
            }

            return fallback;
        }
    }
}
#endif
