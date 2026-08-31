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
        private const string ProcessKey = "Dobak.CodexCapture.ProcessId";
        private const string MainScene = "Assets/Tablet/TabletUI.unity";
        private static double enteredPlayAt;
        private static int captureStep;
        private static bool qaFailed;
        private static int homeworkStartHour;
        private static bool previousEnterPlayModeOptionsEnabled;
        private static EnterPlayModeOptions previousEnterPlayModeOptions;

        static PlayModeCapture()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void StartCapture()
        {
            int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            Debug.Log($"[PLAY QA] StartCapture requested in process {processId}. Active process: {EditorPrefs.GetInt(ProcessKey, -1)}");

            // Unity may invoke the command-line execute method again after the
            // play-mode domain reload. Keep the existing run instead of resetting it.
            if (SessionState.GetBool(SessionKey, false) || EditorPrefs.GetInt(ProcessKey, -1) == processId)
                return;

            EditorPrefs.SetInt(ProcessKey, processId);
            SessionState.SetBool(SessionKey, true);
            previousEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            captureStep = 0;
            qaFailed = false;
            homeworkStartHour = -1;
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
                EditorPrefs.DeleteKey(ProcessKey);
                EditorSettings.enterPlayModeOptionsEnabled = previousEnterPlayModeOptionsEnabled;
                EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions;

                string screenshot = GetScreenshotPath("codex-play.png");
                string messageScreenshot = GetScreenshotPath("codex-message.png");
                string studyLockedScreenshot = GetScreenshotPath("codex-study-locked.png");
                string mapScreenshot = GetScreenshotPath("codex-map.png");
                string fadeScreenshot = GetScreenshotPath("codex-fade.png");
                string travelScreenshot = GetScreenshotPath("codex-after-travel.png");
                string studyScreenshot = GetScreenshotPath("codex-study.png");
                string studyCompleteScreenshot = GetScreenshotPath("codex-study-complete.png");
                string homeworkHomeScreenshot = GetScreenshotPath("codex-home-after-homework.png");
                string bankScreenshot = GetScreenshotPath("codex-bank.png");
                string casinoScreenshot = GetScreenshotPath("codex-casino.png");
                string slotScreenshot = GetScreenshotPath("codex-slot.png");
                string winScreenshot = GetScreenshotPath("codex-win.png");
                string sleepFadeScreenshot = GetScreenshotPath("codex-sleep-fade.png");
                string nextDayScreenshot = GetScreenshotPath("codex-next-day.png");
                string endingScreenshot = GetScreenshotPath("codex-ending.png");
                string restartScreenshot = GetScreenshotPath("codex-restart.png");
                string settingsScreenshot = GetScreenshotPath("codex-settings.png");
                string cashoutEndingScreenshot = GetScreenshotPath("codex-cashout-ending.png");
                string helpEndingScreenshot = GetScreenshotPath("codex-help-ending.png");
                string sleepEndingScreenshot = GetScreenshotPath("codex-sleep-ending.png");
                string declineEndingScreenshot = GetScreenshotPath("codex-decline-ending.png");
                string weekendJobScreenshot = GetScreenshotPath("codex-weekend-job.png");
                if (!qaFailed && File.Exists(screenshot) && File.Exists(messageScreenshot) && File.Exists(studyLockedScreenshot) &&
                    File.Exists(mapScreenshot) && File.Exists(fadeScreenshot) &&
                    File.Exists(travelScreenshot) && File.Exists(studyScreenshot) && File.Exists(studyCompleteScreenshot) &&
                    File.Exists(homeworkHomeScreenshot) && File.Exists(bankScreenshot) && File.Exists(casinoScreenshot) &&
                    File.Exists(slotScreenshot) && File.Exists(winScreenshot) &&
                    File.Exists(sleepFadeScreenshot) && File.Exists(nextDayScreenshot) && File.Exists(endingScreenshot) &&
                    File.Exists(restartScreenshot) && File.Exists(settingsScreenshot) &&
                    File.Exists(cashoutEndingScreenshot) && File.Exists(helpEndingScreenshot) &&
                    File.Exists(sleepEndingScreenshot) && File.Exists(declineEndingScreenshot) &&
                    File.Exists(weekendJobScreenshot))
                {
                    Debug.Log($"[PLAY QA] PASS - homework time/checklist, stored messages, travel, casino, sleep, ending, and full restart verified in {Path.GetDirectoryName(screenshot)}");
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
            // Static fields are reset by the play-mode domain reload. In batch
            // mode the EnteredPlayMode callback can occur before this class is
            // reinitialized, so establish a fresh clock before running steps.
            if (enteredPlayAt <= 0d)
            {
                enteredPlayAt = EditorApplication.timeSinceStartup;
                Debug.Log("[PLAY QA] Play clock initialized after domain reload.");
                return;
            }

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
                ClickMapLocation("학교");
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
                if (GameFlowManager.Instance == null || GameFlowManager.Instance.CurrentLocation != "학교" ||
                    GameFlowManager.Instance.CurrentHour != 15)
                {
                    qaFailed = true;
                    GameFlowManager flow = GameFlowManager.Instance;
                    Debug.LogError($"[PLAY QA] School travel failed. Flow: {flow != null}, location: {flow?.CurrentLocation ?? "null"}, hour: {flow?.CurrentHour.ToString() ?? "null"}.");
                }
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenStudy();
                captureStep = 7;
            }

            if (captureStep == 7 && elapsed >= 6.45)
            {
                CaptureGameView(GetScreenshotPath("codex-study.png"));
                homeworkStartHour = GameFlowManager.Instance != null ? GameFlowManager.Instance.CurrentHour : -1;
                ClickFirstQuizAnswer();
                captureStep = 70;
            }

            if (captureStep == 70 && elapsed >= 7.6)
            {
                ClickFirstQuizAnswer();
                captureStep = 71;
            }

            if (captureStep == 71 && elapsed >= 8.75)
            {
                ClickFirstQuizAnswer();
                captureStep = 72;
            }

            if (captureStep == 72 && elapsed >= 9.9)
            {
                ClickFirstQuizAnswer();
                captureStep = 73;
            }

            if (captureStep == 73 && elapsed >= 11.05)
            {
                ClickFirstQuizAnswer();
                captureStep = 74;
            }

            if (captureStep == 74 && elapsed >= 12.25)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                if (flow == null || !flow.IsHomeworkDone || flow.CurrentHour != homeworkStartHour + 2)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Completing homework did not add two hours and update game state.");
                }
                CaptureGameView(GetScreenshotPath("codex-study-complete.png"));
                if (!ChecklistContains("[완료] 숙제하기"))
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Completed homework was not reflected in the home checklist.");
                }
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                CaptureGameView(GetScreenshotPath("codex-home-after-homework.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenBank();
                captureStep = 75;
            }

            if (captureStep == 75 && elapsed >= 13.1)
            {
                CaptureGameView(GetScreenshotPath("codex-bank.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                GameFlowManager.Instance?.ResolveInvitation(true);
                if (Dobak.Manager.CoinManager.Instance == null || Dobak.Manager.CoinManager.Instance.CasinoCash != 5000)
                {
                    qaFailed = true;
                    Debug.LogError($"[PLAY QA] Invitation grant failed. Flow: {GameFlowManager.Instance != null}, coin: {Dobak.Manager.CoinManager.Instance != null}, casino cash: {Dobak.Manager.CoinManager.Instance?.CasinoCash.ToString() ?? "null"}.");
                }
                if (GameObject.Find("BrowserApp") == null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Gambling app icon did not appear after Minjae's link was accepted.");
                }
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenBrowser();
                captureStep = 8;
            }

            if (captureStep == 8 && elapsed >= 13.85)
            {
                CaptureGameView(GetScreenshotPath("codex-casino.png"));
                object casino = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Casino.CasinoUIManager>();
                MethodInfo openSlot = casino?.GetType().GetMethod("OnSlotMachineButtonClicked", BindingFlags.Instance | BindingFlags.NonPublic);
                openSlot?.Invoke(casino, null);
                captureStep = 9;
            }

            if (captureStep == 9 && elapsed >= 14.6)
            {
                var slot = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Casino.SlotMachine.SlotMachineManager>(FindObjectsInactive.Include);
                Button increaseBet = FindActiveButton("Increase Bet");
                increaseBet?.onClick.Invoke();
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

            if (captureStep == 10 && elapsed >= 16.4)
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

            if (captureStep == 101 && elapsed >= 16.7)
            {
                CaptureGameView(GetScreenshotPath("codex-win.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                GameObject.Find("Sleep Button")?.GetComponent<Button>()?.onClick.Invoke();
                captureStep = 11;
            }

            if (captureStep == 11 && elapsed >= 16.88)
            {
                CaptureGameView(GetScreenshotPath("codex-sleep-fade.png"));
                captureStep = 12;
            }

            if (captureStep == 12 && elapsed >= 18.1)
            {
                CaptureGameView(GetScreenshotPath("codex-next-day.png"));
                MethodInfo endGame = typeof(GameFlowManager).GetMethod("EndGame", BindingFlags.Instance | BindingFlags.NonPublic);
                endGame?.Invoke(GameFlowManager.Instance, new object[] { "QA 엔딩", "재시작 버튼 표시를 확인합니다." });
                captureStep = 13;
            }

            if (captureStep == 13 && elapsed >= 18.45)
            {
                CaptureGameView(GetScreenshotPath("codex-ending.png"));
                Button restart = GameObject.Find("Restart Button")?.GetComponent<Button>();
                if (restart == null)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Restart button was not created.");
                }
                else
                {
                    restart.onClick.Invoke();
                }
                captureStep = 14;
            }

            if (captureStep == 14 && elapsed >= 19.95)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                int activeContacts = CountActiveContacts();
                if (flow == null || flow.CurrentDay != 1 || flow.CurrentHour != 7 || flow.IsGamblingUnlocked ||
                    GameObject.Find("BrowserApp") != null || activeContacts != 2)
                {
                    qaFailed = true;
                    Debug.LogError($"[PLAY QA] Restart did not restore the complete initial state. Contacts: {activeContacts}.");
                }
                CaptureGameView(GetScreenshotPath("codex-restart.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenSetting();
                captureStep = 15;
            }

            if (captureStep == 15 && elapsed >= 20.8)
            {
                CaptureGameView(GetScreenshotPath("codex-settings.png"));
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.CloseCurrentApp();
                captureStep = 16;
            }

            if (captureStep == 16 && elapsed >= 21.1)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                flow?.ResolveInvitation(true);
                SetPrivateField(flow, "cashOutAttempts", 0);
                InvokePrivate(flow, "RefreshUI");
                GameObject.Find("Cashout Button")?.GetComponent<Button>()?.onClick.Invoke();
                if (flow == null || flow.IsGameEnded)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] A low-threshold cashout incorrectly ended the game.");
                }

                Dobak.Manager.CoinManager.Instance?.AddCasinoCredit(10000);
                GameObject.Find("Cashout Button")?.GetComponent<Button>()?.onClick.Invoke();
                if (flow == null || !flow.IsGameEnded)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] High-balance cashout did not trigger the scam ending.");
                }
                captureStep = 17;
            }

            if (captureStep == 17 && elapsed >= 21.45)
            {
                CaptureGameView(GetScreenshotPath("codex-cashout-ending.png"));
                ClickRestart();
                captureStep = 18;
            }

            if (captureStep == 18 && elapsed >= 22.95)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                flow?.ResolveInvitation(true);
                flow?.ResolveMomLoan(true);
                if (flow == null || !flow.CanRequestHelp)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Borrowing did not unlock the help request branch.");
                }
                GameObject.Find("Help Button")?.GetComponent<Button>()?.onClick.Invoke();
                if (flow == null || !flow.IsGameEnded)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Help request did not reach its ending.");
                }
                captureStep = 19;
            }

            if (captureStep == 19 && elapsed >= 23.3)
            {
                CaptureGameView(GetScreenshotPath("codex-help-ending.png"));
                ClickRestart();
                captureStep = 20;
            }

            if (captureStep == 20 && elapsed >= 24.8)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                SetPrivateField(flow, "currentDay", 1);
                InvokePrivate(flow, "RegisterSleep", 3);
                SetPrivateField(flow, "currentDay", 2);
                InvokePrivate(flow, "RegisterSleep", 3);
                SetPrivateField(flow, "currentDay", 3);
                InvokePrivate(flow, "RegisterSleep", 3);
                if (flow == null || !flow.IsGameEnded || flow.ConsecutiveShortSleepDays != 3)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Three short-sleep days did not trigger the sleep ending.");
                }
                captureStep = 21;
            }

            if (captureStep == 21 && elapsed >= 25.15)
            {
                CaptureGameView(GetScreenshotPath("codex-sleep-ending.png"));
                ClickRestart();
                captureStep = 22;
            }

            if (captureStep == 22 && elapsed >= 26.65)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                flow?.ResolveInvitation(false);
                if (flow == null || !flow.IsGameEnded)
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Declining the invitation did not reach the safe ending.");
                }
                captureStep = 23;
            }

            if (captureStep == 23 && elapsed >= 27.0)
            {
                CaptureGameView(GetScreenshotPath("codex-decline-ending.png"));
                ClickRestart();
                captureStep = 24;
            }

            if (captureStep == 24 && elapsed >= 28.5)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                SetPrivateField(flow, "currentDay", 6);
                SetPrivateField(flow, "currentHour", 7);
                InvokePrivate(flow, "StartNewDay", false);
                UnityEngine.Object.FindAnyObjectByType<AppWindow>()?.OpenMap();
                captureStep = 25;
            }

            if (captureStep == 25 && elapsed >= 29.35)
            {
                ClickMapLocation("카페");
                captureStep = 26;
            }

            if (captureStep == 26 && elapsed >= 30.55)
            {
                GameFlowManager flow = GameFlowManager.Instance;
                if (flow == null || flow.CurrentLocation != "카페" || flow.CurrentHour != 14 ||
                    Dobak.Manager.CoinManager.Instance == null || Dobak.Manager.CoinManager.Instance.BankCash != 1060 ||
                    !ChecklistContains("[완료] 알바 가기"))
                {
                    qaFailed = true;
                    Debug.LogError("[PLAY QA] Weekend cafe work did not complete its time, wage, and checklist flow.");
                }
                CaptureGameView(GetScreenshotPath("codex-weekend-job.png"));
                captureStep = 27;
            }

            if (captureStep == 27 && elapsed >= 30.9)
                EditorApplication.ExitPlaymode();
        }

        private static void ClickRestart()
        {
            GameObject.Find("Restart Button")?.GetComponent<Button>()?.onClick.Invoke();
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] arguments)
        {
            target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, arguments);
        }

        private static void ClickMapLocation(string displayName)
        {
            var map = UnityEngine.Object.FindAnyObjectByType<Dobak.App.Map.MapLocationController>(FindObjectsInactive.Include);
            FieldInfo locationsField = typeof(Dobak.App.Map.MapLocationController)
                .GetField("locations", BindingFlags.Instance | BindingFlags.NonPublic);
            var locations = locationsField?.GetValue(map) as Dobak.App.Map.MapLocationController.LocationPoint[];
            if (locations != null)
            {
                string rawName = displayName == "학교" ? "1" : displayName == "카페" ? "2" : "3";
                foreach (Dobak.App.Map.MapLocationController.LocationPoint location in locations)
                {
                    if (location != null && location.locationName == rawName && location.button != null)
                    {
                        Debug.Log($"[PLAY QA] Clicking map {displayName}. Listeners: {location.button.onClick.GetPersistentEventCount()}, active: {location.button.gameObject.activeInHierarchy}, flow: {GameFlowManager.Instance != null}.");
                        location.button.onClick.Invoke();
                        return;
                    }
                }
            }

            qaFailed = true;
            Debug.LogError($"[PLAY QA] Map button for {displayName} was not available.");
        }

        private static void ClickFirstQuizAnswer()
        {
            QuizManager quiz = UnityEngine.Object.FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include);
            FieldInfo answerButtonsField = typeof(QuizManager).GetField("answerButtons", BindingFlags.Instance | BindingFlags.NonPublic);
            Button[] answerButtons = answerButtonsField?.GetValue(quiz) as Button[];
            if (answerButtons != null)
            {
                foreach (Button answer in answerButtons)
                {
                    if (answer != null && answer.gameObject.activeInHierarchy && answer.interactable)
                    {
                        answer.onClick.Invoke();
                        return;
                    }
                }
            }

            qaFailed = true;
            Debug.LogError("[PLAY QA] No playable quiz answer button was available.");
        }

        private static Button FindActiveButton(string objectName)
        {
            Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();
            foreach (Button button in buttons)
            {
                if (button.gameObject.name == objectName && button.gameObject.activeInHierarchy)
                    return button;
            }

            return null;
        }

        private static int CountActiveContacts()
        {
            int count = 0;
            foreach (ProfileSlot slot in UnityEngine.Object.FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (slot.gameObject.activeSelf)
                    count++;
            }

            return count;
        }

        private static bool ChecklistContains(string expectedText)
        {
            foreach (TMPro.TMP_Text text in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>())
            {
                if (text != null && text.gameObject.scene.IsValid() &&
                    !string.IsNullOrEmpty(text.text) && text.text.Contains(expectedText))
                    return true;
            }

            return false;
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
