#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
        private static int pointsBeforeInterruptedSpin;
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
                    GameObject snsIcon = GameObject.Find("SNSApp");
                    Expect(snsIcon != null, "SNS home icon was not created.");
                    Expect(snsIcon != null && snsIcon.transform.parent?.name == "AppManager",
                        "SNS did not reuse the existing home app slot.");
                    RectTransform snsIconRect = snsIcon?.GetComponent<RectTransform>();
                    Expect(snsIconRect != null && Vector2.Distance(snsIconRect.anchoredPosition, new Vector2(-600f, -100f)) < 1f,
                        $"SNS icon is outside the extra app slot: {snsIconRect?.anchoredPosition}.");
                    Capture("feature-00-home-sns-slot.png");
                    apps?.OpenSNS();
                    Next(1, 0.7d);
                    break;

                case 1:
                    GameObject snsApp = GameObject.Find("Runtime SNS App");
                    Expect(snsApp?.activeInHierarchy == true, "SNS app did not open.");
                    Expect(snsApp != null && snsApp.transform.parent?.name == "AppUi",
                        "SNS app is not inside the system app viewport.");
                    Expect(GameObject.Find("StatusBar")?.activeInHierarchy == true && GameObject.Find("Home_Btn")?.activeInHierarchy == true,
                        "SNS app covered the system status or navigation bar.");
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
                    Expect(VisibleTextContains("환전"), "Casino cash-out menu is missing.");
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
                    Expect(SlotMachineManager.CalculatePayout(100, 1f) == 200,
                        "Slot payout can still return only the original stake.");
                    Expect(SlotMachineManager.CalculatePayout(100, 5f) == 500,
                        "Five-times slot payout is calculated incorrectly.");
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
                    pointsBeforeInterruptedSpin = coins != null ? coins.CasinoCash : 0;
                    Button interruptedSpin = GetPrivate<Button>(finishedSlot, "spinButton");
                    interruptedSpin?.onClick.Invoke();
                    Expect(coins != null && coins.CasinoCash == pointsBeforeInterruptedSpin - finishedSlot.CurrentBetAmount,
                        "Interrupted spin did not commit its stake before starting.");
                    apps?.CloseCurrentApp();
                    Expect(coins != null && coins.CasinoCash == pointsBeforeInterruptedSpin,
                        "Closing the app during a spin did not refund the stake.");
                    apps?.OpenBrowser();
                    Next(9, 0.8d);
                    break;

                case 9:
                    CasinoUIManager reopenedCasino = UnityEngine.Object.FindAnyObjectByType<CasinoUIManager>(FindObjectsInactive.Include);
                    InvokePrivate(reopenedCasino, "OnSlotMachineButtonClicked");
                    Next(10, 0.4d);
                    break;

                case 10:
                    SlotMachineManager resumedSlot = UnityEngine.Object.FindAnyObjectByType<SlotMachineManager>(FindObjectsInactive.Include);
                    Button resumedSpin = GetPrivate<Button>(resumedSlot, "spinButton");
                    Expect(resumedSlot != null && resumedSlot.CurrentRound == 1,
                        "Interrupted spin was incorrectly counted as a completed round.");
                    Expect(resumedSpin != null && resumedSpin.interactable,
                        "Spin button stayed disabled after reopening the gambling app.");
                    resumedSpin?.onClick.Invoke();
                    Next(11, 4.0d);
                    break;

                case 11:
                    SlotMachineManager resumedFinishedSlot = UnityEngine.Object.FindAnyObjectByType<SlotMachineManager>(FindObjectsInactive.Include);
                    Button resumedFinishedSpin = GetPrivate<Button>(resumedFinishedSlot, "spinButton");
                    Expect(resumedFinishedSlot != null && resumedFinishedSlot.CurrentRound == 2,
                        "A spin did not complete after reopening the gambling app.");
                    Expect(resumedFinishedSpin != null && resumedFinishedSpin.interactable,
                        "Spin button did not recover after the resumed spin.");
                    apps?.CloseCurrentApp();
                    SetPrivate(flow, "currentDay", 6);
                    SetPrivate(flow, "currentHour", 7);
                    SetPrivate(flow, "currentLocation", "집");
                    SetPrivate(flow, "jobDone", false);
                    InvokePrivate(flow, "RefreshUI");
                    bankBeforeJob = coins != null ? coins.BankCash : 0;
                    flow?.TravelTo("2");
                    Next(12, 1.5d);
                    break;

                case 12:
                    Expect(flow != null && flow.CurrentDay == 6 && flow.CurrentHour == 16 && flow.CurrentLocation == "집",
                        $"Weekend job schedule is wrong: day {flow?.CurrentDay}, hour {flow?.CurrentHour}, place {flow?.CurrentLocation}.");
                    Expect(coins != null && coins.BankCash == bankBeforeJob + 80000,
                        $"Job wage is wrong: before {bankBeforeJob}, after {coins?.BankCash}.");
                    DismissAllNarration();
                    Capture("feature-08-weekend-job.png");
                    Expect(flow != null && flow.CanAttemptCashOut, "Cash-out is unavailable despite a positive point balance.");
                    SetPrivate(flow, "cashOutAttempts", 2);
                    Expect(flow != null && flow.WillCashOutScam, "The third cash-out attempt does not trigger the scam branch.");
                    SetPrivate(flow, "cashOutAttempts", 0);
                    if (coins != null && coins.CasinoCash < 10000)
                        coins.AddCasinoCredit(10000 - coins.CasinoCash);
                    Expect(flow != null && flow.WillCashOutScam, "A 10,000P cash-out does not trigger the scam branch.");
                    int bankBeforeLoan = coins != null ? coins.BankCash : 0;
                    flow?.ResolveMomLoan(true);
                    Expect(flow != null && flow.CurrentDebt == 15000 && flow.CanRepayDebt,
                        "Borrowed money did not create a repayable debt.");
                    flow?.RepayDebt();
                    Expect(flow != null && flow.CurrentDebt == 0,
                        "Debt repayment did not clear the debt.");
                    Expect(coins != null && coins.BankCash == bankBeforeLoan,
                        "Debt repayment did not return the borrowed amount from the bank balance.");
                    bool repaymentRecorded = false;
                    if (coins != null)
                    {
                        foreach (TransactionRecord record in coins.History)
                            if (record.scope == TransactionScope.DebtRepayment)
                                repaymentRecorded = true;
                    }
                    Expect(repaymentRecorded, "Debt repayment was not added to the bank transaction history.");
                    apps?.OpenMessage();
                    Next(13, 0.8d);
                    break;

                case 13:
                    DialogueManager dialogue = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                    ScrollRect contacts = FindScrollRect("Contact Viewport");
                    Expect(contacts != null && contacts.verticalNormalizedPosition >= 0.99f,
                        $"Contact list is not aligned to the top: {contacts?.verticalNormalizedPosition}.");
                    RectTransform firstContact = FindFirstActiveContact(contacts);
                    Expect(firstContact != null && firstContact.anchorMin.y >= 0.99f && firstContact.anchoredPosition.y > -100f,
                        $"First contact is not anchored near the top: anchor {firstContact?.anchorMin}, position {firstContact?.anchoredPosition}.");
                    Capture("feature-09-message-contacts.png");
                    FireCsvEvent(flow, "seoyeon_homework");
                    dialogue?.OpenDialogue(SpeakerType.Seoyeon);
                    Next(14, 0.8d);
                    break;

                case 14:
                    DialogueManager openDialogue = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                    RectTransform chatViewport = openDialogue?.scrollRect?.viewport;
                    RectTransform choices = openDialogue?.choiceButtonContainer as RectTransform;
                    float choiceTop = choices != null ? choices.anchoredPosition.y + choices.rect.height : 0f;
                    Expect(chatViewport != null && chatViewport.offsetMin.y >= choiceTop + 30f,
                        $"Chat viewport overlaps the choice/input area: viewport {chatViewport?.offsetMin.y}, choices {choiceTop}.");
                    Expect(chatViewport != null && chatViewport.offsetMax.y <= -140f,
                        $"Chat viewport extends into the fixed header: {chatViewport?.offsetMax.y}.");
                    Expect(chatViewport != null && chatViewport.GetComponent<RectMask2D>() != null,
                        "Chat viewport does not have a rectangular clipping mask.");
                    Expect(GameObject.Find("Chat Window Header") != null && GameObject.Find("Chat Window Middle") != null &&
                           GameObject.Find("Chat Window Footer") != null,
                        "The supplied three-part message window art was not created.");
                    Expect(VisibleTextContains("오늘 숙제 3번 조건"),
                        "Seoyeon's CSV event was not rendered in her chat room.");
                    Expect(ClickVisibleChoice("나도 조건이 헷갈렸다고 답한다"),
                        "Seoyeon's reply choice was not available.");
                    Capture("feature-10-message-chat-safe-area.png");
                    Next(15, 0.7d);
                    break;

                case 15:
                    DialogueManager repliedDialogue = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                    Expect(ChannelContains(repliedDialogue, SpeakerType.Seoyeon, "잠깐 같이 정리해 볼래"),
                        "Selecting a CSV reply did not trigger Seoyeon's follow-up message.");
                    Expect(VisibleTextContains("잠깐 같이 정리해 볼래"),
                        "Seoyeon's follow-up was stored but not rendered as a chat bubble.");
                    Expect(ClickVisibleChoice("같이 조건을 정리한다"),
                        "Seoyeon's second reply choice was not available.");
                    Next(16, 0.7d);
                    break;

                case 16:
                    DialogueManager completedSeoyeonDialogue = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                    Expect(ChannelContains(completedSeoyeonDialogue, SpeakerType.Seoyeon, "각자 숙제 마저 하자"),
                        "Seoyeon's second CSV reply did not trigger the closing message.");
                    Expect(VisibleTextContains("각자 숙제 마저 하자"),
                        "Seoyeon's closing message was stored but not rendered.");
                    FireCsvEvent(flow, "joonho_casual");
                    completedSeoyeonDialogue?.OpenDialogue(SpeakerType.Joonho);
                    Next(17, 0.7d);
                    break;

                case 17:
                    Expect(VisibleTextContains("주말 일정 확인했어?"),
                        "Joonho's CSV event was not rendered in his chat room.");
                    Expect(ClickVisibleChoice("알바 일정을 확인했다고 답한다"),
                        "Joonho's reply choice was not available.");
                    Next(18, 0.7d);
                    break;

                case 18:
                    DialogueManager joonhoDialogue = UnityEngine.Object.FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
                    Expect(ChannelContains(joonhoDialogue, SpeakerType.Joonho, "오전 8시 출근"),
                        "Selecting a CSV reply did not trigger Joonho's follow-up message.");
                    Expect(VisibleTextContains("오전 8시 출근"),
                        "Joonho's follow-up was stored but not rendered as a chat bubble.");
                    apps?.CloseCurrentApp();
                    SetPrivate(flow, "currentLocation", "학교");
                    SetPrivate(flow, "currentHour", 19);
                    flow?.SpendTime(1, "학교에 남아 있기");
                    Next(19, 0.6d);
                    break;

                case 19:
                    Expect(flow != null && flow.CurrentHour == 20 && flow.CurrentLocation == "집",
                        $"School closing did not send the player home: {flow?.CurrentHour}:00 at {flow?.CurrentLocation}.");
                    Capture("feature-11-school-closing.png");
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

        private static ScrollRect FindScrollRect(string name)
        {
            foreach (ScrollRect scroll in Resources.FindObjectsOfTypeAll<ScrollRect>())
                if (scroll.gameObject.scene.IsValid() && scroll.gameObject.name == name)
                    return scroll;
            return null;
        }

        private static RectTransform FindFirstActiveContact(ScrollRect contacts)
        {
            if (contacts?.content == null)
                return null;

            foreach (RectTransform child in contacts.content)
                if (child.gameObject.activeInHierarchy && child.GetComponent<ProfileSlot>() != null)
                    return child;
            return null;
        }

        private static bool VisibleTextContains(string expected)
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (text != null && text.gameObject.scene.IsValid() && text.gameObject.activeInHierarchy && text.text?.Contains(expected) == true)
                    return true;
            return false;
        }

        private static bool ClickVisibleChoice(string expected)
        {
            foreach (Button button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude))
            {
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                if (label != null && label.text.Contains(expected))
                {
                    button.onClick.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static bool ChannelContains(DialogueManager dialogue, SpeakerType speaker, string expected)
        {
            Dictionary<SpeakerType, ChatChannel> channels = GetPrivate<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
            return channels != null && channels.TryGetValue(speaker, out ChatChannel channel) &&
                   channel.receivedMessages.Exists(message => message.Contains(expected));
        }

        private static void FireCsvEvent(GameFlowManager flow, string eventId)
        {
            ScenarioMessageTable table = GetPrivate<ScenarioMessageTable>(flow, "scenarioMessages");
            if (table == null)
            {
                Fail($"Scenario table was unavailable for {eventId}.");
                return;
            }

            foreach (ScenarioEventDefinition definition in table.Events)
            {
                if (definition.id != eventId)
                    continue;
                InvokePrivate(flow, "FireScenario", definition, null);
                return;
            }
            Fail($"CSV event was not found: {eventId}.");
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
