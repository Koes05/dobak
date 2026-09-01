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
    private enum Route { Recovery, Prevention, NoGamble, NoHelp, NoFunds, MinjaeDebt, SeojunDebt, MixedA, MixedB, MixedC }

    private static readonly Route[] AllRoutes =
    {
        Route.Recovery,
        Route.Prevention,
        Route.NoGamble,
        Route.NoHelp,
        Route.NoFunds,
        Route.MinjaeDebt,
        Route.SeojunDebt,
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

    private const double UiSettleDelay = 0.12d;
    private const double SceneSettleDelay = 0.2d;
    private const double ChoiceSettleDelay = 0.45d;

    public static void RunRecovery() => Run(Route.Recovery);
    public static void RunPrevention() => Run(Route.Prevention);
    public static void RunNoHelp() => Run(Route.NoHelp);
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
        pendingMapTarget = string.Empty;
        lastLine = string.Empty;
        lastCapturedScene = string.Empty;
        observedDay = 0;
        capturedDays.Clear();
        capturedQuizDays.Clear();
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
            if (runAllRoutes && !failed && routeIndex >= 0 && routeIndex < AllRoutes.Length - 1)
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
            Debug.Log($"[SCENARIO V4 {route}] day={flow.CurrentDay} hour={flow.CurrentHour} " +
                      $"scene={director.ActiveSceneId}/{director.ActiveLineId} choices={director.CurrentChoices.Count} " +
                      $"location={flow.CurrentLocation} school={flow.IsSchoolDone} study={flow.IsHomeworkDone} " +
                      $"job={flow.IsJobDone} project={director.GetState("project.progress")} " +
                      $"app={apps.CurrentAppType} pendingMap={pendingMapTarget} quiz={quizOpen}");
            nextDebugAt = EditorApplication.timeSinceStartup + 5d;
        }

        if (flow.IsGameEnded)
        {
            Capture($"ending-{route.ToString().ToLowerInvariant()}.png");
            Expect(replyBubbleVerified, "No outgoing reply bubble was observed during the run.");
            Expect(director.ChoiceHistory.Select(choice => choice.choiceId).Distinct().Count() ==
                   director.ChoiceHistory.Count,
                "A scenario choice was submitted more than once.");
            Expect(int.Parse(director.GetState("project.progress")) >= 4,
                $"Group project progress only reached {director.GetState("project.progress")}.");
            if (route == Route.Recovery)
            {
                Expect(director.GetState("ending") == "recovery", $"Expected recovery ending, got {director.GetState("ending")}.");
                Expect(flow.CurrentDebt > 0, "Recovery route did not retain the intended debt consequence.");
                Expect(director.GetState("flag.help_requested") == "true", "Teacher counseling was not requested.");
            }
            else if (route == Route.Prevention || route == Route.NoGamble || route == Route.NoHelp ||
                     route == Route.MinjaeDebt || route == Route.SeojunDebt)
            {
                if (route == Route.Prevention)
                {
                    Expect(director.GetState("ending") == "prevented", $"Expected prevention ending, got {director.GetState("ending")}.");
                    Expect(flow.CurrentDebt == 0, "Prevention route unexpectedly created debt.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) < 3, "Prevention route accumulated too many gambling sessions.");
                }
                else if (route == Route.NoGamble)
                {
                    Expect(director.GetState("ending") == "prevented", $"Expected prevented ending, got {director.GetState("ending")}.");
                    Expect(flow.CurrentDebt == 0, "No-gamble route unexpectedly created debt.");
                    Expect(int.Parse(director.GetState("counter.gamble_sessions")) == 0,
                        "No-gamble route incorrectly recorded a gambling session.");
                    Expect(director.GetState("flag.first_day_refused") == "true",
                        "The first-day refusal state was not retained.");
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
                else
                {
                    Expect(director.GetState("debt_owner") == "seojun", "Seojun debt route lost its lender state.");
                    Expect(flow.CurrentDebt > 0, "Seojun debt route did not retain its debt consequence.");
                    Expect(director.GetState("ending") == "recovery",
                        $"Expected recovery after disclosing Seojun debt, got {director.GetState("ending")}.");
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
            FindActiveButton("Narration Continue Button")?.onClick.Invoke();
            nextActionAt = EditorApplication.timeSinceStartup + UiSettleDelay;
            return;
        }

        if (!string.IsNullOrEmpty(director.ActiveSceneId))
        {
            HandleStory(director, apps);
            return;
        }

        if (!string.IsNullOrEmpty(pendingMapTarget))
        {
            CompleteMapAction();
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
                if (flow.CurrentLocation != "집")
                {
                    BeginMapAction(apps, "집");
                    return;
                }

                apps.OpenStudy();
                quizOpen = true;
                nextActionAt = EditorApplication.timeSinceStartup + SceneSettleDelay;
                return;
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
            "d1_open_link", "d2_reply_gamble", "d3_reply_gamble", "g3_chase", "g4_continue",
            "g5_borrow", "borrow_mom", "d5_reply_job", "d6_reply_project", "d9_reply_school",
            "d10_reply_wait", "d13_tell_teacher"
        };
        string[] preventionChoices =
        {
            "d1_open_link", "d2_reply_study", "d3_reply_ignore", "g3_stop", "g4_stop", "g5_stop",
            "d5_reply_job", "d6_reply_project", "d9_reply_school", "d10_reply_wait"
        };
        string[] noGambleChoices =
        {
            "d1_decline_link", "d2_reply_study", "d3_reply_ignore", "d5_reply_job",
            "d6_reply_project", "d9_reply_school", "d10_reply_wait"
        };
        string[] noFundsChoices =
        {
            "d1_open_link", "d2_reply_gamble", "d3_reply_gamble", "g3_chase", "g4_continue",
            "g5_stop", "d5_reply_gamble", "d6_reply_project", "d9_reply_school", "d10_reply_wait"
        };
        string[] minjaeDebtChoices =
        {
            "d1_open_link", "d2_reply_gamble", "d3_reply_gamble", "g3_chase", "g4_continue",
            "g5_stop", "minjae_loan_accept", "d5_reply_job", "d6_reply_project", "d9_reply_school",
            "d10_debt_reply_wait", "d13_tell_teacher"
        };
        string[] seojunDebtChoices =
        {
            "d1_open_link", "d2_reply_gamble", "d3_reply_gamble", "g3_chase", "g4_continue",
            "g5_borrow", "borrow_friend", "d5_reply_job", "d6_reply_project", "d9_reply_school",
            "d10_reply_wait", "d13_tell_teacher"
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
                    : route == Route.SeojunDebt ? seojunDebtChoices : recoveryChoices;
        foreach (string id in preferred)
        {
            ScenarioV3Choice match = choices.FirstOrDefault(choice => choice.id == id);
            if (match != null)
                return match;
        }
        return choices[0];
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
