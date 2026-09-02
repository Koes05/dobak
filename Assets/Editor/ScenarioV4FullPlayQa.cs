#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dobak.App.Map;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ScenarioV4FullPlayQa
{
    private enum Route
    {
        Recovery, Prevention, NoGamble, NoHelp, NoFunds, MinjaeDebt, SeojunDebt,
        LoanHeld, RepeatLoss, ProjectFail, MixedA, MixedB, MixedC
    }

    private static readonly Route[] AllRoutes =
    {
        Route.Recovery,
        Route.Prevention,
        Route.NoGamble,
        Route.NoHelp,
        Route.NoFunds,
        Route.MinjaeDebt,
        Route.SeojunDebt,
        Route.LoanHeld,
        Route.RepeatLoss,
        Route.ProjectFail,
        Route.MixedA,
        Route.MixedB,
        Route.MixedC
    };

    private const string MainScene = "Assets/Tablet/TabletUI.unity";
    private static Route route;
    private static double startedAt;
    private static double nextActionAt;
    private static double nextDebugAt;
    private static string pendingMapTarget = string.Empty;
    private static string lastLine = string.Empty;
    private static string lastCapturedScene = string.Empty;
    private static readonly HashSet<int> capturedDays = new HashSet<int>();
    private static readonly HashSet<int> capturedQuizDays = new HashSet<int>();
    private static readonly HashSet<int> capturedOfferDays = new HashSet<int>();
    private static bool quizOpen;
    private static bool testedWrongAnswer;
    private static bool replyBubbleVerified;
    private static bool capturedChoiceDebug;
    private static double choiceSubmittedAt;
    private static bool failed;
    private static bool previousOptionsEnabled;
    private static EnterPlayModeOptions previousOptions;
    private static bool runAllRoutes;
    private static bool anyRouteFailed;
    private static int observedDay;
    private static bool routeCompleted;
    private static bool repeatLossObserved;
    private static bool cafeSceneLocationChecked;
    private static bool projectFailureObserved;
    private static bool seoyeonRepairObserved;
    private static bool preventedReturnHomeObserved;
    private static string expectedConsecutiveGambleScene = string.Empty;
    private static string lastBlockingDialogue = string.Empty;

    private const double UiSettleDelay = 0.12d;
    private const double SceneSettleDelay = 0.2d;
    private const double ChoiceSettleDelay = 0.45d;

    public static void RunRecovery() => Run(Route.Recovery);
    public static void RunPrevention() => Run(Route.Prevention);
    public static void RunNoGamble() => Run(Route.NoGamble);
    public static void RunNoHelp() => Run(Route.NoHelp);
    public static void RunNoFunds() => Run(Route.NoFunds);
    public static void RunProjectFail() => Run(Route.ProjectFail);
    public static void RunAll()
    {
        runAllRoutes = true;
        anyRouteFailed = false;
        Run(Route.Recovery);
    }

    public static void RunRemainingFromNoHelp()
    {
        runAllRoutes = true;
        anyRouteFailed = false;
        Run(Route.NoHelp);
    }

    public static void RunRemainingFromSeojunDebt()
    {
        runAllRoutes = true;
        anyRouteFailed = false;
        Run(Route.SeojunDebt);
    }

    public static void AbortForRestart()
    {
        runAllRoutes = false;
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.ExitPlaymode();
    }

    private static void Run(Route selectedRoute)
    {
        route = selectedRoute;
        failed = false;
        quizOpen = false;
        testedWrongAnswer = false;
        replyBubbleVerified = false;
        capturedChoiceDebug = false;
        choiceSubmittedAt = 0d;
        routeCompleted = false;
        repeatLossObserved = false;
        cafeSceneLocationChecked = false;
        projectFailureObserved = false;
        seoyeonRepairObserved = false;
        preventedReturnHomeObserved = false;
        expectedConsecutiveGambleScene = string.Empty;
        lastBlockingDialogue = string.Empty;
        pendingMapTarget = string.Empty;
        lastLine = string.Empty;
        lastCapturedScene = string.Empty;
        observedDay = 0;
        capturedDays.Clear();
        capturedQuizDays.Clear();
        capturedOfferDays.Clear();
        string save = Path.Combine(Application.persistentDataPath, "scenario_v3_history.json");
        if (File.Exists(save))
            File.Delete(save);

        previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        previousOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            startedAt = EditorApplication.timeSinceStartup;
            nextActionAt = startedAt + 1d;
            nextDebugAt = startedAt + 5d;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousOptions;
            if (!routeCompleted && !failed)
            {
                failed = true;
                Debug.LogError($"[SCENARIO V4 {route.ToString().ToUpperInvariant()} QA] Play mode ended before the route reached an ending.");
            }
            Debug.Log(failed
                ? $"[SCENARIO V4 {route.ToString().ToUpperInvariant()} QA] FAIL"
                : $"[SCENARIO V4 {route.ToString().ToUpperInvariant()} QA] PASS");
            anyRouteFailed |= failed;
            int routeIndex = Array.IndexOf(AllRoutes, route);
            if (runAllRoutes && routeIndex >= 0 && routeIndex < AllRoutes.Length - 1)
            {
                Route nextRoute = AllRoutes[routeIndex + 1];
                EditorApplication.delayCall += () => Run(nextRoute);
                return;
            }

            if (runAllRoutes)
            {
                Debug.Log(anyRouteFailed
                    ? "[SCENARIO V4 ALL ROUTES QA] FAIL"
                    : "[SCENARIO V4 ALL ROUTES QA] PASS");
                runAllRoutes = false;
            }
            if (Application.isBatchMode)
                EditorApplication.Exit(anyRouteFailed ? 2 : 0);
        }
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup - startedAt > 420d)
        {
            Fail("Timed out before reaching an ending.");
            EditorApplication.ExitPlaymode();
            return;
        }
        if (EditorApplication.timeSinceStartup < nextActionAt)
            return;

        ScenarioV3Director director = UnityEngine.Object.FindAnyObjectByType<ScenarioV3Director>();
        GameFlowManager flow = GameFlowManager.Instance;
        AppWindow apps = UnityEngine.Object.FindAnyObjectByType<AppWindow>();
        if (director == null || flow == null || apps == null || !director.IsReady)
            return;

        if (observedDay != flow.CurrentDay)
        {
            observedDay = flow.CurrentDay;
            quizOpen = false;
            pendingMapTarget = string.Empty;
        }

        if (EditorApplication.timeSinceStartup >= nextDebugAt)
        {
            FieldInfo transitionField = typeof(ScenarioV3Director).GetField("sceneTransitionInProgress",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool sceneTransition = transitionField != null && (bool)transitionField.GetValue(director);
            ScenarioV3Line pendingOutgoing = GetPrivate<ScenarioV3Line>(director, "pendingOutgoingLine");
            TMP_Text narrationBody = GetPrivate<TMP_Text>(flow, "narrationBodyText");
            CanvasGroup fade = GetPrivate<CanvasGroup>(flow, "fadeGroup");
            Debug.Log($"[SCENARIO V4 {route}] day={flow.CurrentDay} hour={flow.CurrentHour} " +
                      $"scene={director.ActiveSceneId}/{director.ActiveLineId} choices={director.CurrentChoices.Count} " +
                      $"location={flow.CurrentLocation} school={flow.IsSchoolDone} study={flow.IsHomeworkDone} " +
                      $"job={flow.IsJobDone} project={director.GetState("project.progress")} " +
                      $"app={apps.CurrentAppType} pendingMap={pendingMapTarget} quiz={quizOpen} " +
                      $"sceneTransition={sceneTransition} pendingOutgoing={pendingOutgoing?.id} " +
                      $"narration='{narrationBody?.text}' fade={fade?.alpha:0.00}/{fade?.blocksRaycasts}");
            nextDebugAt = EditorApplication.timeSinceStartup + 5d;
        }

        if ((route == Route.Prevention || route == Route.NoGamble) &&
            flow.CurrentDay == 14 && flow.IsSchoolDone && flow.CurrentLocation == "집")
        {
            preventedReturnHomeObserved = true;
        }

        if (flow.IsGameEnded)
        {
            Capture($"ending-{route.ToString().ToLowerInvariant()}.png");
            Expect(replyBubbleVerified, "No outgoing reply bubble was observed during the run.");
            Expect(director.ChoiceHistory.Select(choice => choice.choiceId).Distinct().Count() ==
                   director.ChoiceHistory.Count,
                "A scenario choice was submitted more than once.");
            if (route != Route.ProjectFail)
            {
                Expect(int.Parse(director.GetState("project.progress")) >= 4,
                    $"Group project progress only reached {director.GetState("project.progress")}.");
            }
            if (route == Route.Recovery)
            {
                Expect(director.GetState("ending") == "recovery", $"Expected recovery ending, got {director.GetState("ending")}.");
                Expect(flow.CurrentDebt > 0, "Recovery route did not retain the intended debt consequence.");
                Expect(director.GetState("flag.help_requested") == "true", "Teacher counseling was not requested.");
            }
            else if (route == Route.Prevention || route == Route.NoGamble || route == Route.NoHelp ||
                     route == Route.MinjaeDebt || route == Route.SeojunDebt || route == Route.LoanHeld ||
                     route == Route.RepeatLoss || route == Route.ProjectFail)
            {
                if (route == Route.Prevention)
                {
                    Expect(director.GetState("ending") == "prevented", $"Expected prevention ending, got {director.GetState("ending")}.");
                    Expect(flow.CurrentDebt == 0, "Prevention route unexpectedly created debt.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) < 3, "Prevention route accumulated too many gambling sessions.");
                    Expect(preventedReturnHomeObserved, "The prevention ending skipped the explicit return-home transition.");
                }
                else if (route == Route.NoGamble)
                {
                    Expect(director.GetState("ending") == "prevented", $"Expected prevented ending, got {director.GetState("ending")}.");
                    Expect(flow.CurrentDebt == 0, "No-gamble route unexpectedly created debt.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) == 0,
                        "No-gamble route incorrectly recorded a gambling session.");
                    Expect(director.IsGamblingAppUnlocked,
                        "The no-gamble route should still have been free to open the gambling app.");
                    Expect(preventedReturnHomeObserved, "The no-gamble ending skipped the explicit return-home transition.");
                }
                else if (route == Route.NoHelp)
                {
                    Expect(director.GetState("ending") == "no_help", $"Expected no-help ending, got {director.GetState("ending")}.");
                    Expect(director.GetState("flag.manager_advice") != "true", "Manager advice should remain locked after repeated absences.");
                    Expect(director.GetState("flag.help_requested") != "true", "No-help route unexpectedly requested counseling.");
                }
                else if (route == Route.MinjaeDebt)
                {
                    Expect(director.GetState("debt_owner") == "minjae", "Minjae debt route lost its lender state.");
                    Expect(flow.CurrentDebt > 0, "Minjae debt route did not retain its debt consequence.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after disclosing Minjae debt, got {director.GetState("ending")}.");
                }
                else if (route == Route.SeojunDebt)
                {
                    Expect(director.GetState("debt_owner") == "seojun", "Seojun debt route lost its lender state.");
                    Expect(flow.CurrentDebt > 0, "Seojun debt route did not retain its debt consequence.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after disclosing Seojun debt, got {director.GetState("ending")}.");
                }
                else if (route == Route.LoanHeld)
                {
                    Expect(director.GetState("debt_owner") == "seojun", "Held-loan route lost its lender state.");
                    Expect(flow.CurrentDebt > 0, "Held-loan route did not retain its debt consequence.");
                    Expect(flow.V3BankCash > 0, "Held-loan route spent every borrowed and earned won despite stopping gambling.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) == 5,
                        "Held-loan route gambled again after borrowing.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after disclosing held debt, got {director.GetState("ending")}.");
                }
                else if (route == Route.RepeatLoss)
                {
                    Expect(repeatLossObserved, "The fixed seventh-session repeat-loss scene was never shown.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) >= 7,
                        "Repeat-loss route did not reach the seventh gambling session.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after repeated loss, got {director.GetState("ending")}.");
                }
                else
                {
                    Expect(projectFailureObserved, "The incomplete-project scene was never shown.");
                    Expect(seoyeonRepairObserved, "The day-14 Seoyeon repair scene was never shown.");
                    Expect(int.Parse(director.GetState("project.progress")) < 4,
                        "Project-failure route unexpectedly completed enough project work.");
                    Expect(director.GetState("flag.project_result") == "bad",
                        "Project-failure route did not retain the failed project result.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after the project failure route, got {director.GetState("ending")}.");
                }
            }
            else
            {
                string ending = director.GetState("ending");
                Expect(ending == "recovery" || ending == "prevented" || ending == "no_help",
                    $"Mixed route reached an invalid ending: {ending}.");
                if (route == Route.NoFunds)
                {
                    Expect(int.Parse(director.GetState("counter.no_funds_attempts")) >= 1,
                        "The zero-balance gambling attempt was not blocked.");
                    Expect(director.ChoiceHistory.Any(choice => choice.choiceId == "g5_stop"),
                        "The no-funds route did not refuse borrowing after the fifth session.");
                }
            }
            routeCompleted = true;
            EditorApplication.ExitPlaymode();
            return;
        }

        GameObject blockingNarration = GameObject.Find("Narration Dialogue");
        if (blockingNarration != null && blockingNarration.activeInHierarchy)
        {
            TMP_Text title = GetPrivate<TMP_Text>(flow, "narrationTitleText");
            TMP_Text body = GetPrivate<TMP_Text>(flow, "narrationBodyText");
            string dialogueKey = $"{title?.text}\n{body?.text}";
            if (dialogueKey != lastBlockingDialogue)
            {
                lastBlockingDialogue = dialogueKey;
                Debug.Log($"[SCENARIO V4 DIALOGUE] {dialogueKey.Replace('\n', ' ')}");
                Capture($"dialogue-{Safe(director.ActiveSceneId + "-" + director.ActiveLineId + "-" + flow.CurrentDay)}.png");
            }

            Button continueButton = GetPrivate<Button>(flow, "narrationContinueButton");
            if (continueButton == null || !continueButton.gameObject.activeInHierarchy)
            {
                Fail("A blocking narration was visible without an active Continue button.");
                EditorApplication.ExitPlaymode();
                return;
            }
            continueButton.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        if (!string.IsNullOrEmpty(director.ActiveSceneId))
        {
            if (!string.IsNullOrEmpty(expectedConsecutiveGambleScene))
            {
                Expect(director.ActiveSceneId == expectedConsecutiveGambleScene,
                    $"Consecutive gambling returned to {director.ActiveSceneId} instead of {expectedConsecutiveGambleScene}.");
                expectedConsecutiveGambleScene = string.Empty;
            }
            if (director.ActiveSceneId == "gamble_repeat_loss")
                repeatLossObserved = true;
            if (director.ActiveSceneId == "d8_project_bad")
                projectFailureObserved = true;
            if (director.ActiveSceneId == "d14_seoyeon_bad")
                seoyeonRepairObserved = true;
            if (director.ActiveSceneId == "d14_prevented_return_home")
                preventedReturnHomeObserved = true;
            if (!cafeSceneLocationChecked &&
                (director.ActiveSceneId == "d11_minjae_cafe" || director.ActiveSceneId == "d11_minjae_debt_cafe"))
            {
                cafeSceneLocationChecked = true;
                Expect(flow.CurrentLocation == "카페",
                    $"{director.ActiveSceneId} started at {flow.CurrentLocation} instead of 카페.");
            }
            HandleStory(director, apps);
            return;
        }

        GameObject transientNovel = GetPrivate<GameObject>(director, "novelPanel");
        if (transientNovel != null && transientNovel.activeInHierarchy)
        {
            GetPrivate<Button>(director, "continueButton")?.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }

        if (!string.IsNullOrEmpty(pendingMapTarget))
        {
            CompleteMapAction();
            return;
        }

        if (director.HasPendingMessageAction)
        {
            if (apps.CurrentAppType != AppType.Message)
                apps.OpenMessage();
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }

        if (ShouldStartGamble(director, flow.CurrentDay))
        {
            quizOpen = false;
            apps.CloseCurrentApp();
            Button launcher = FindActiveButton("Gambling Launcher");
            if (launcher == null)
            {
                Fail("The gambling app is unlocked, but its home launcher is missing.");
                return;
            }
            if (capturedOfferDays.Add(flow.CurrentDay))
                Capture($"gambling-icon-day-{flow.CurrentDay:00}.png");
            launcher.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }
        if (quizOpen)
        {
            HandleQuiz(flow, apps);
            return;
        }

        if (capturedDays.Add(flow.CurrentDay))
            Capture($"day-{flow.CurrentDay:00}-tablet.png");

        if (flow.IsWeekend)
        {
            if (!flow.IsJobDone)
            {
                bool skipShift = (route == Route.NoHelp && (flow.CurrentDay == 5 || flow.CurrentDay == 11)) ||
                                 (route == Route.NoFunds && flow.CurrentDay == 4);
                if (skipShift)
                    flow.V3SetClock("14:00");
                BeginMapAction(apps, "카페");
                return;
            }
        }
        else
        {
            if (!flow.IsSchoolDone)
            {
                BeginMapAction(apps, "학교");
                return;
            }
            if (flow.V3HasStudyToday && !flow.IsHomeworkDone)
            {
                bool skipProjectWork = route == Route.ProjectFail && flow.CurrentDay == 6;
                if (skipProjectWork)
                {
                    if (flow.CurrentHour < 21)
                    {
                        flow.V3SetClock("21:00");
                        nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
                        return;
                    }
                }
                else if (flow.CurrentLocation != "집")
                {
                    BeginMapAction(apps, "집");
                    return;
                }
                else
                {
                    apps.OpenStudy();
                    quizOpen = true;
                    nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
                    return;
                }
            }
        }

        if (!flow.CanSleepNow)
        {
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        flow.Sleep();
        nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
    }

    private static void HandleStory(ScenarioV3Director director, AppWindow apps)
    {
        if (choiceSubmittedAt > 0d)
        {
            if (EditorApplication.timeSinceStartup - choiceSubmittedAt < ChoiceSettleDelay)
                return;
            choiceSubmittedAt = 0d;
        }

        if (director.ActiveLineId != lastLine)
        {
            lastLine = director.ActiveLineId;
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            if (director.ActiveSceneId != lastCapturedScene && ShouldCaptureScene(director.ActiveSceneId))
            {
                lastCapturedScene = director.ActiveSceneId;
                Capture($"scene-{Safe(director.ActiveSceneId)}.png");
            }
            return;
        }

        GameObject narration = GameObject.Find("Narration Dialogue");
        if (narration != null && narration.activeInHierarchy)
        {
            FindActiveButton("Narration Continue Button")?.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        IReadOnlyList<ScenarioV3Choice> choices = director.CurrentChoices;
        if (choices.Count > 0)
        {
            ScenarioV3Choice selected = SelectChoice(choices);
            Button visible = FindButtonWithText(selected.text);
            DialogueManager activeDialogue = GetPrivate<DialogueManager>(director, "dialogue");
            if (visible == null && apps.CurrentAppType == AppType.Message && !capturedChoiceDebug &&
                activeDialogue != null && activeDialogue.dialoguePanel != null && activeDialogue.dialoguePanel.activeInHierarchy)
            {
                capturedChoiceDebug = true;
                Dictionary<SpeakerType, ChatChannel> channels = GetPrivate<Dictionary<SpeakerType, ChatChannel>>(activeDialogue, "channels");
                ChatChannel friend = channels != null && channels.TryGetValue(SpeakerType.Friend, out ChatChannel found) ? found : null;
                string buttons = string.Join(" | ", UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include)
                    .Where(button => button.gameObject.activeInHierarchy)
                    .Select(button => $"{button.gameObject.name}='{button.GetComponentInChildren<TMP_Text>()?.text}'({button.interactable})"));
                Debug.Log($"[SCENARIO V4 CHOICE DEBUG] wanted='{selected.text}' panel={activeDialogue.dialoguePanel.activeInHierarchy} " +
                          $"events={friend?.eventChoices.Count} pending={friend?.pendingChoiceSets.Count} messages={friend?.receivedMessages.Count} " +
                          $"rendered={friend?.renderedReceivedCount} choiceChildren={activeDialogue.choiceButtonContainer.childCount} buttons={buttons}");
                Capture("choice-debug.png");
            }
            if (visible == null)
            {
                GameObject novel = GetPrivate<GameObject>(director, "novelPanel");
                if (novel != null && novel.activeInHierarchy)
                {
                    GetPrivate<Button>(director, "continueButton")?.onClick.Invoke();
                }
                else
                {
                    if (apps.CurrentAppType != AppType.Message)
                    {
                        apps.OpenMessage();
                        nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
                        return;
                    }
                    DialogueManager dialogue = GetPrivate<DialogueManager>(director, "dialogue");
                    if (dialogue != null)
                    {
                        FieldInfo waitingSpeaker = typeof(ScenarioV3Director).GetField("waitingMessageSpeaker",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        SpeakerType speaker = waitingSpeaker != null
                            ? (SpeakerType)waitingSpeaker.GetValue(director)
                            : SpeakerType.Friend;
                        dialogue.OpenDialogue(speaker);
                    }
                }
                nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
                return;
            }

            if (selected.id == "g3_chase")
                expectedConsecutiveGambleScene = "gamble_4";
            else if (selected.id == "g4_continue")
                expectedConsecutiveGambleScene = "gamble_5";
            visible.onClick.Invoke();
            choiceSubmittedAt = EditorApplication.timeSinceStartup;
            if (!string.IsNullOrWhiteSpace(selected.replyText) && VisibleTextContains(selected.replyText))
                replyBubbleVerified = true;
            nextActionAt = EditorApplication.timeSinceStartup + ChoiceSettleDelay;
            return;
        }


        if (director.HasPendingMessageAction && apps.CurrentAppType != AppType.Message)
        {
            apps.OpenMessage();
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }

        if (apps.CurrentAppType == AppType.Message)
        {
            if (director.HasPendingMessageAction)
            {
                nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
                return;
            }

            apps.CloseCurrentApp();
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        GameObject novelPanel = GetPrivate<GameObject>(director, "novelPanel");
        if (novelPanel != null && novelPanel.activeInHierarchy)
        {
            GetPrivate<Button>(director, "continueButton")?.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
        }
    }

    private static ScenarioV3Choice SelectChoice(IReadOnlyList<ScenarioV3Choice> choices)
    {
        string[] recoveryChoices =
        {
            "g3_chase", "g4_continue", "g5_borrow", "borrow_mom", "d13_tell_teacher"
        };
        string[] preventionChoices =
        {
            "g3_stop", "g4_stop", "g5_stop"
        };
        string[] noGambleChoices =
        {
            "g3_stop", "g4_stop", "g5_stop"
        };
        string[] noFundsChoices =
        {
            "g3_chase", "g4_continue", "g5_stop", "minjae_loan_reject"
        };
        string[] minjaeDebtChoices =
        {
            "g3_chase", "g4_continue", "g5_stop", "minjae_loan_accept", "d13_tell_teacher"
        };
        string[] seojunDebtChoices =
        {
            "g3_chase", "g4_continue", "g5_borrow", "borrow_friend", "d13_tell_teacher"
        };
        string[] loanHeldChoices =
        {
            "g3_chase", "g4_continue", "g5_borrow", "borrow_friend", "d13_tell_teacher"
        };
        string[] repeatLossChoices =
        {
            "g3_chase", "g4_continue", "g5_borrow", "borrow_mom", "d13_tell_teacher"
        };
        string[] projectFailChoices =
        {
            "g3_stop", "d13_tell_teacher"
        };
        if (route == Route.MixedA || route == Route.MixedB || route == Route.MixedC)
        {
            int salt = route == Route.MixedA ? 17 : route == Route.MixedB ? 43 : 79;
            int hash = salt;
            foreach (ScenarioV3Choice choice in choices)
            {
                foreach (char character in choice.id ?? string.Empty)
                    hash = unchecked(hash * 31 + character);
            }
            return choices[(hash & int.MaxValue) % choices.Count];
        }

        string[] preferred = route == Route.NoGamble
            ? noGambleChoices
            : route == Route.Prevention
            ? preventionChoices
            : route == Route.NoFunds
                ? noFundsChoices
                : route == Route.MinjaeDebt
                    ? minjaeDebtChoices
                    : route == Route.SeojunDebt
                        ? seojunDebtChoices
                        : route == Route.LoanHeld
                            ? loanHeldChoices
                            : route == Route.RepeatLoss
                                ? repeatLossChoices
                                : route == Route.ProjectFail ? projectFailChoices : recoveryChoices;
        foreach (string id in preferred)
        {
            ScenarioV3Choice match = choices.FirstOrDefault(choice => choice.id == id);
            if (match != null)
                return match;
        }
        return choices[0];
    }

    private static bool ShouldStartGamble(ScenarioV3Director director, int day)
    {
        if (!director.IsGamblingAppUnlocked)
            return false;

        int sessions = int.TryParse(director.GetState("counter.gamble_sessions"), out int parsed) ? parsed : 0;
        int target = route switch
        {
            Route.Recovery or Route.MinjaeDebt or Route.SeojunDebt => day >= 3 ? 6 : day == 2 ? 2 : 1,
            Route.LoanHeld => day >= 3 ? 5 : day == 2 ? 2 : 1,
            Route.RepeatLoss => day >= 5 ? 7 : day >= 3 ? 6 : day == 2 ? 2 : 1,
            Route.ProjectFail => day >= 3 ? 3 : day == 2 ? 2 : 1,
            Route.Prevention => day >= 2 ? 2 : 1,
            Route.NoGamble => 0,
            Route.NoHelp => day >= 3 ? 3 : day == 2 ? 2 : 1,
            Route.NoFunds => day >= 3 ? 6 : day == 2 ? 2 : 1,
            Route.MixedA => day >= 10 ? 3 : day >= 6 ? 2 : day >= 2 ? 1 : 0,
            Route.MixedB => day >= 9 ? 3 : day >= 5 ? 2 : day >= 3 ? 1 : 0,
            Route.MixedC => day >= 9 ? 5 : day >= 6 ? 4 : day >= 5 ? 3 : day >= 2 ? 2 : 1,
            _ => 0
        };

        if (route == Route.NoFunds && int.TryParse(director.GetState("counter.no_funds_attempts"), out int attempts) && attempts > 0)
            return false;
        return sessions < target;
    }

    private static void BeginMapAction(AppWindow apps, string target)
    {
        apps.CloseCurrentApp();
        apps.OpenMap();
        pendingMapTarget = target;
        nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
    }

    private static void CompleteMapAction()
    {
        string target = pendingMapTarget;
        Button button = FindMapButton(target);
        if (button == null)
        {
            Fail($"Map destination button missing: {target}.");
            return;
        }
        pendingMapTarget = string.Empty;
        button.onClick.Invoke();
        nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
    }

    private static void HandleQuiz(GameFlowManager flow, AppWindow apps)
    {
        if (flow.IsHomeworkDone)
        {
            apps.CloseCurrentApp();
            quizOpen = false;
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        if (apps.CurrentAppType != AppType.Study)
        {
            apps.OpenStudy();
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }

        QuizManager quiz = UnityEngine.Object.FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include);
        Button[] answers = GetPrivate<Button[]>(quiz, "answerButtons");
        List<Button> available = answers?.Where(button => button.gameObject.activeInHierarchy && button.interactable).ToList();
        if (available == null || available.Count == 0)
            return;

        if (capturedQuizDays.Add(flow.CurrentDay))
            Capture($"quiz-day-{flow.CurrentDay:00}.png");

        if (flow.CurrentDay == 2 && !testedWrongAnswer)
        {
            Button wrong = available.FirstOrDefault(button => button.GetComponentInChildren<TMP_Text>().text == "1333");
            Expect(wrong != null, "Day 2 wrong-answer option was missing.");
            wrong?.onClick.Invoke();
            testedWrongAnswer = true;
            nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
            return;
        }

        Button correct = available.FirstOrDefault(button => IsCorrectQuizAnswer(button.GetComponentInChildren<TMP_Text>().text));
        if (correct == null)
        {
            Fail($"Correct quiz answer was not found on day {flow.CurrentDay}.");
            return;
        }
        correct.onClick.Invoke();
        nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
    }

    private static bool IsCorrectQuizAnswer(string text)
    {
        text = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ');
        return text == "1336" ||
               text.Contains("일정에 적어 둔다") ||
               text.Contains("친구에게 돈을 빌렸다") ||
               text.Contains("레벨·출석 보상") ||
               text.Contains("돈이나 재산상 가치") ||
               text.Contains("1336 상담에 연결한다");
    }

    private static bool ShouldCaptureScene(string scene)
    {
        return scene is "gamble_1" or "gamble_3" or "gamble_5" or "d8_lecture" or
               "d12_manager_help" or "d12_manager_bond" or "d13_consult" or "d14_recovery";
    }

    private static Button FindMapButton(string displayName)
    {
        MapLocationController map = UnityEngine.Object.FindAnyObjectByType<MapLocationController>(FindObjectsInactive.Include);
        Array locations = GetPrivate<Array>(map, "locations");
        if (locations == null)
            return null;
        string code = displayName == "학교" ? "1" : displayName == "카페" ? "2" : "3";
        foreach (object location in locations)
        {
            FieldInfo nameField = location.GetType().GetField("locationName");
            FieldInfo buttonField = location.GetType().GetField("button");
            if ((string)nameField?.GetValue(location) == code)
                return buttonField?.GetValue(location) as Button;
        }
        return null;
    }

    private static Button FindActiveButton(string name) =>
        UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include)
            .FirstOrDefault(button => button.gameObject.activeInHierarchy && button.gameObject.name == name);

    private static Button FindButtonWithText(string text) =>
        UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include)
            .FirstOrDefault(button => button.gameObject.activeInHierarchy && button.interactable &&
                                      button.GetComponentInChildren<TMP_Text>()?.text == text);

    private static bool VisibleTextContains(string text) =>
        UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include)
            .Any(label => label != null && label.gameObject != null && label.gameObject.activeInHierarchy &&
                          !string.IsNullOrEmpty(label.text) && label.text.Contains(text));

    private static T GetPrivate<T>(object target, string fieldName) where T : class
    {
        return target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target) as T;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            Fail(message);
    }

    private static void Fail(string message)
    {
        if (!failed)
            Debug.LogError($"[SCENARIO V4 {route.ToString().ToUpperInvariant()} QA] {message}");
        failed = true;
    }

    private static string Safe(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    private static void Capture(string filename)
    {
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
            $"../Logs/ScenarioV4FullQa/{route}"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename);
        Camera camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
        Canvas canvas = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include)
            .FirstOrDefault(candidate => candidate.gameObject.activeInHierarchy && candidate.transform.parent == null);
        if (camera == null || canvas == null)
        {
            Fail("Camera or root canvas missing while capturing.");
            return;
        }

        const int width = 1600;
        const int height = 900;
        RenderMode oldMode = canvas.renderMode;
        Camera oldCanvasCamera = canvas.worldCamera;
        RenderTexture oldTarget = camera.targetTexture;
        RenderTexture oldActive = RenderTexture.active;
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
#endif
