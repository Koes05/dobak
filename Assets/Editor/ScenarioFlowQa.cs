#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using Dobak.App.Map;
using Dobak.Manager;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.Editor
{
    [InitializeOnLoad]
    public static class ScenarioFlowQa
    {
        private const string MainScene = "Assets/Tablet/TabletUI.unity";
        private static bool running;
        private static bool failed;
        private static int step;
        private static double nextStepAt;
        private static bool previousOptionsEnabled;
        private static EnterPlayModeOptions previousOptions;

        static ScenarioFlowQa()
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
                    ? "[SCENARIO QA] FAIL - inspect preceding errors and screenshots."
                    : $"[SCENARIO QA] PASS - full day/tutorial/message scenario verified in {GetLogDirectory()}");
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
                Fail($"Unhandled QA exception: {exception}");
                EditorApplication.ExitPlaymode();
            }
        }

        private static void RunStep()
        {
            GameFlowManager flow = GameFlowManager.Instance;
            AppWindow apps = UnityEngine.Object.FindAnyObjectByType<AppWindow>();

            switch (step)
            {
                case 0:
                    Expect(flow != null && flow.CurrentDay == 1 && flow.CurrentHour == 7, "Game did not start on day 1 at 7 AM.");
                    Expect(GameObject.Find("BrowserApp") == null, "Gambling app was visible before an invitation was accepted.");
                    Expect(TextContains("Narration Body", "아침이다"), "Opening tutorial narration was not shown.");
                    Capture("scenario-01-tutorial-start.png");
                    ClickNamedButton("Narration Continue Button");
                    apps?.OpenMap();
                    Next(1, 0.8d);
                    break;

                case 1:
                    Expect(TextContains("Narration Body", "지도 앱"), "Map tutorial narration was not shown.");
                    Capture("scenario-02-map-tutorial.png");
                    ClickNamedButton("Narration Continue Button");
                    ClickMapLocation("학교");
                    Next(2, 1.25d);
                    break;

                case 2:
                    Expect(flow != null && flow.CurrentDay == 1 && flow.CurrentHour == 15 && flow.CurrentLocation == "학교",
                        $"School flow was wrong: day {flow?.CurrentDay}, hour {flow?.CurrentHour}, location {flow?.CurrentLocation}.");
                    Capture("scenario-03-school-complete.png");
                    DismissNarration();
                    apps?.OpenStudy();
                    Next(3, 0.8d);
                    break;

                case 3:
                    ClickQuizAnswer();
                    Next(4, 1.15d);
                    break;
                case 4:
                    ClickQuizAnswer();
                    Next(5, 1.15d);
                    break;
                case 5:
                    ClickQuizAnswer();
                    Next(6, 1.15d);
                    break;
                case 6:
                    ClickQuizAnswer();
                    Next(7, 1.15d);
                    break;
                case 7:
                    ClickQuizAnswer();
                    Next(8, 1.25d);
                    break;

                case 8:
                    Expect(flow != null && flow.IsHomeworkDone && flow.CurrentHour == 17,
                        $"Homework did not add two hours: hour {flow?.CurrentHour}.");
                    Capture("scenario-04-study-complete.png");
                    DismissNarration();
                    apps?.CloseCurrentApp();
                    apps?.OpenMessage();
                    Next(9, 0.85d);
                    break;

                case 9:
                    Expect(TextContains("Narration Body", "메시지 앱"), "Message tutorial narration was not shown.");
                    Capture("scenario-05-message-tutorial.png");
                    DismissNarration();
                    AddScrollTestContacts();
                    Next(10, 0.5d);
                    break;

                case 10:
                    Canvas.ForceUpdateCanvases();
                    ScrollRect contacts = FindNamedScrollRect("Contact Viewport");
                    Expect(contacts != null && contacts.content.rect.height > contacts.viewport.rect.height,
                        "Contact list did not become vertically scrollable after new contacts were added.");
                    Capture("scenario-06-contacts-top.png");
                    if (contacts != null)
                        contacts.verticalNormalizedPosition = 1f;
                    Next(11, 0.45d);
                    break;

                case 11:
                    Canvas.ForceUpdateCanvases();
                    Capture("scenario-07-contacts-bottom.png");
                    apps?.CloseCurrentApp();
                    apps?.OpenBank();
                    Next(12, 0.85d);
                    break;

                case 12:
                    Capture("scenario-08-bank-tutorial.png");
                    DismissNarration();
                    DismissNarration();
                    apps?.CloseCurrentApp();
                    SetPrivate(flow, "currentHour", 23);
                    ClickNamedButton("Sleep Button");
                    Next(13, 1.6d);
                    break;

                case 13:
                    Expect(flow != null && flow.CurrentDay == 2 && flow.CurrentHour == 7,
                        $"Sleeping did not start day 2 at 7 AM: day {flow?.CurrentDay}, hour {flow?.CurrentHour}.");
                    Expect(TextContains("Narration Body", "8시간"), "Wake narration did not report the actual eight-hour sleep.");
                    Expect(GameObject.Find("BrowserApp") == null, "Gambling app appeared before day 2 school was completed.");
                    Capture("scenario-09-day2-wake.png");
                    DismissNarration();
                    apps?.OpenMap();
                    Next(14, 0.75d);
                    break;

                case 14:
                    ClickMapLocation("학교");
                    Next(15, 1.25d);
                    break;

                case 15:
                    Expect(flow != null && flow.CurrentDay == 2 && flow.CurrentHour == 15 && flow.CurrentLocation == "학교",
                        "Day 2 school did not complete at 3 PM.");
                    Expect(GameObject.Find("BrowserApp") == null, "Gambling app appeared before the invitation choice.");
                    apps?.OpenMessage();
                    Next(16, 0.8d);
                    break;

                case 16:
                    Capture("scenario-10-minjae-after-school.png");
                    OpenContact(SpeakerType.Friend);
                    Next(17, 0.45d);
                    break;

                case 17:
                    Capture("scenario-11-minjae-invitation.png");
                    ClickChoice("내용을 확인한다");
                    Next(18, 0.7d);
                    break;

                case 18:
                    ClickChoice("문자를 지우고 차단한다");
                    Next(19, 0.65d);
                    break;

                case 19:
                    Capture("scenario-12-retempt.png");
                    ClickChoice("호기심에 링크를 누른다");
                    Next(20, 1.0d);
                    break;

                case 20:
                    Expect(flow != null && flow.IsGamblingUnlocked, "Accepting the second invitation did not unlock gambling.");
                    Expect(CoinManager.Instance != null && CoinManager.Instance.CasinoCash == 5000,
                        $"Free point grant was not 5,000P: {CoinManager.Instance?.CasinoCash}.");
                    Expect(GameObject.Find("BrowserApp") != null, "Gambling app icon was not revealed after acceptance.");
                    Capture("scenario-13-site-unlocked.png");
                    SetPrivate(flow, "currentHour", 6);
                    flow?.SpendTime(1, "밤샘 확인");
                    Next(21, 0.35d);
                    break;

                case 21:
                    Expect(flow != null && flow.CurrentDay == 3 && flow.CurrentHour == 7,
                        "Crossing 7 AM while awake did not advance exactly one day.");
                    Expect(TextContains("Narration Body", "밤을 새어버렸다"), "All-nighter narration was not shown over the active app.");
                    Capture("scenario-14-all-nighter.png");
                    DismissNarration();
                    apps?.CloseCurrentApp();
                    SetPrivate(flow, "gambleRounds", 10);
                    InvokePrivate(flow, "RefreshUI");
                    flow?.AttemptCashOut();
                    Next(22, 0.45d);
                    break;

                case 22:
                    Expect(flow != null && flow.IsGameEnded, "Ten-round cashout did not reach the scenario ending.");
                    Expect(TextContains("Ending Title", "먹튀"), "Ten-round cashout reached the wrong ending.");
                    Capture("scenario-15-repeat-cashout-ending.png");
                    ClickNamedButton("Restart Button");
                    Next(23, 1.8d);
                    break;

                case 23:
                    flow = GameFlowManager.Instance;
                    apps = UnityEngine.Object.FindAnyObjectByType<AppWindow>();
                    Expect(flow != null && flow.CurrentDay == 1 && flow.CurrentHour == 7,
                        "Restart did not restore day 1 at 7 AM.");
                    Expect(!flow.IsGamblingUnlocked && CoinManager.Instance != null && CoinManager.Instance.CasinoCash == 0,
                        "Restart did not remove gambling access and site points.");
                    Expect(GameObject.Find("BrowserApp") == null, "Restart left the gambling app visible.");
                    DismissNarration();
                    flow.ResolveInvitation(false);
                    Next(24, 0.5d);
                    break;

                case 24:
                    Expect(flow != null && flow.IsGameEnded, "Final block did not reach the safe ending.");
                    Expect(TextContains("Ending Title", "위험 차단"), "Final block reached the wrong ending.");
                    Capture("scenario-16-final-block-ending.png");
                    ClickNamedButton("Restart Button");
                    Next(25, 1.8d);
                    break;

                case 25:
                    flow = GameFlowManager.Instance;
                    Expect(flow != null && flow.CurrentDay == 1 && flow.CurrentHour == 7,
                        "Second restart did not restore the initial clock.");
                    DismissNarration();
                    flow.ResolveInvitation(true);
                    CoinManager.Instance?.AddCasinoCredit(20000);
                    SetPrivate(flow, "gambleRounds", 10);
                    flow.AttemptCashOut();
                    Next(26, 0.5d);
                    break;

                case 26:
                    Expect(flow != null && flow.IsGameEnded, "High cashout did not reach an ending.");
                    Expect(TextContains("Ending Title", "먹튀"), "High cashout reached the wrong ending.");
                    Capture("scenario-17-high-cashout-ending.png");
                    EditorApplication.ExitPlaymode();
                    break;
            }
        }

        private static void Next(int next, double delay)
        {
            step = next;
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
            Debug.LogError($"[SCENARIO QA] {message}");
        }

        private static void DismissNarration()
        {
            Button button = FindNamedButton("Narration Continue Button");
            if (button != null && button.gameObject.activeInHierarchy)
                button.onClick.Invoke();
        }

        private static void ClickMapLocation(string displayName)
        {
            MapLocationController map = UnityEngine.Object.FindAnyObjectByType<MapLocationController>(FindObjectsInactive.Include);
            FieldInfo field = typeof(MapLocationController).GetField("locations", BindingFlags.Instance | BindingFlags.NonPublic);
            var locations = field?.GetValue(map) as MapLocationController.LocationPoint[];
            string raw = displayName == "학교" ? "1" : displayName == "카페" ? "2" : "3";
            if (locations != null)
            {
                foreach (MapLocationController.LocationPoint location in locations)
                {
                    if (location != null && location.locationName == raw && location.button != null && location.button.gameObject.activeInHierarchy)
                    {
                        location.button.onClick.Invoke();
                        return;
                    }
                }
            }
            Fail($"Active map button was not found for {displayName}.");
        }

        private static void ClickQuizAnswer()
        {
            QuizManager quiz = UnityEngine.Object.FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include);
            FieldInfo field = typeof(QuizManager).GetField("answerButtons", BindingFlags.Instance | BindingFlags.NonPublic);
            Button[] buttons = field?.GetValue(quiz) as Button[];
            if (buttons != null)
            {
                foreach (Button button in buttons)
                {
                    if (button != null && button.gameObject.activeInHierarchy && button.interactable)
                    {
                        button.onClick.Invoke();
                        return;
                    }
                }
            }
            Fail("Playable quiz answer was not available.");
        }

        private static void AddScrollTestContacts()
        {
            NotificationManager notifications = UnityEngine.Object.FindAnyObjectByType<NotificationManager>();
            AddContact(notifications, SpeakerType.Unknown, "문자", "새로운 안내 문자가 도착했다.");
            AddContact(notifications, SpeakerType.Bank, "은행 알림", "거래 내역을 확인해 주세요.");
            AddContact(notifications, SpeakerType.Site, "사이트 알림", "이용 알림이 도착했다.");
            AddContact(notifications, SpeakerType.Counselor, "상담 선생님", "필요할 때 도움을 요청할 수 있어요.");
        }

        private static void AddContact(NotificationManager manager, SpeakerType speaker, string title, string message)
        {
            manager?.SendNotification(new NotificationData
            {
                title = title,
                message = message,
                appType = AppType.Message,
                speakerType = speaker
            });
        }

        private static void OpenContact(SpeakerType speaker)
        {
            foreach (ProfileSlot slot in UnityEngine.Object.FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (slot.gameObject.activeInHierarchy && slot.speakerType == speaker)
                {
                    slot.GetComponent<Button>()?.onClick.Invoke();
                    return;
                }
            }
            Fail($"Contact was not available: {speaker}.");
        }

        private static void ClickChoice(string text)
        {
            foreach (Button button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                if (label != null && label.text.Contains(text))
                {
                    button.onClick.Invoke();
                    return;
                }
            }
            Fail($"Choice button was not found: {text}.");
        }

        private static void ClickNamedButton(string name)
        {
            Button button = FindNamedButton(name);
            if (button == null || !button.gameObject.activeInHierarchy)
            {
                Fail($"Active button was not found: {name}.");
                return;
            }
            button.onClick.Invoke();
        }

        private static Button FindNamedButton(string name)
        {
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (button.gameObject.scene.IsValid() && button.gameObject.name == name)
                    return button;
            }
            return null;
        }

        private static ScrollRect FindNamedScrollRect(string name)
        {
            foreach (ScrollRect scroll in Resources.FindObjectsOfTypeAll<ScrollRect>())
            {
                if (scroll.gameObject.scene.IsValid() && scroll.gameObject.name == name)
                    return scroll;
            }
            return null;
        }

        private static bool TextContains(string objectName, string expected)
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text.gameObject.scene.IsValid() && text.gameObject.name == objectName && text.gameObject.activeInHierarchy)
                    return text.text.Contains(expected);
            }
            return false;
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] arguments)
        {
            target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, arguments);
        }

        private static string GetLogDirectory()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
        }

        private static void Capture(string filename)
        {
            Camera camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
            Canvas canvas = FindRootCanvas();
            if (camera == null || canvas == null)
            {
                Fail($"Could not capture {filename}: camera or canvas missing.");
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
                File.WriteAllBytes(Path.Combine(GetLogDirectory(), filename), texture.EncodeToPNG());
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
