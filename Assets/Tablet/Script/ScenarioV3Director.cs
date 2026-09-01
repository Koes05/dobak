using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public sealed class ScenarioV3StateEntry
{
    public string key;
    public string value;
}

[Serializable]
public sealed class ScenarioV3ChoiceRecord
{
    public string choiceId;
    public string sceneId;
    public string lineId;
    public int day;
    public string time;
    public string choiceText;
    public string stateBefore;
    public string stateAfter;
    public string recordedAt;
}

[Serializable]
public sealed class ScenarioV3SaveData
{
    public int version = 3;
    public List<ScenarioV3StateEntry> state = new List<ScenarioV3StateEntry>();
    public List<ScenarioV3ChoiceRecord> choices = new List<ScenarioV3ChoiceRecord>();
    public List<string> seenScenes = new List<string>();
}

public sealed class ScenarioV3Director : MonoBehaviour
{
    private const int FinalDay = 7;

    private readonly Dictionary<string, string> state =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScenarioV3ChoiceRecord> choiceHistory = new List<ScenarioV3ChoiceRecord>();
    private readonly HashSet<string> seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ScenarioV3Scene> sceneQueue = new Queue<ScenarioV3Scene>();

    private GameFlowManager flow;
    private DialogueManager dialogue;
    private NotificationManager notifications;
    private AppWindow appWindow;
    private ScenarioV3Database database;
    private ScenarioV3Scene activeScene;
    private int activeLineIndex;
    private Action queueCompleted;
    private string reactiveTrigger;
    private string immediateRoute;
    private bool pendingDayAdvance;
    private bool waitingForMessageChoice;

    private GameObject novelPanel;
    private RawImage novelBackground;
    private TMP_Text chapterText;
    private TMP_Text speakerText;
    private TMP_Text bodyText;
    private Button continueButton;
    private Button choiceAButton;
    private Button choiceBButton;
    private AudioSource sfxSource;
    private AudioSource bgmSource;
    private AudioClip popupClip;
    private AudioClip buttonClip;
    private AudioClip temptationBgm;

    public bool IsReady { get; private set; }
    public IReadOnlyList<ScenarioV3ChoiceRecord> ChoiceHistory => choiceHistory;
    public string ActiveSceneId => activeScene?.id ?? string.Empty;
    public string ActiveLineId => activeScene != null && activeLineIndex >= 0 && activeLineIndex < activeScene.lines.Count
        ? activeScene.lines[activeLineIndex].id
        : string.Empty;
    public IReadOnlyList<ScenarioV3Choice> CurrentChoices =>
        activeScene != null && activeLineIndex >= 0 && activeLineIndex < activeScene.lines.Count
            ? activeScene.lines[activeLineIndex].Choices.ToList()
            : Array.Empty<ScenarioV3Choice>();

    private string SavePath => Path.Combine(Application.persistentDataPath, "scenario_v3_history.json");

    public void Initialize(GameFlowManager owner)
    {
        flow = owner;
        dialogue = FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
        notifications = FindAnyObjectByType<NotificationManager>(FindObjectsInactive.Include);
        appWindow = FindAnyObjectByType<AppWindow>(FindObjectsInactive.Include);
        database = ScenarioV3Database.Load();
        CreateNovelUI();
        CreateAudio();
        IsReady = true;
    }

    public void BeginNewGame()
    {
        ResetRun();
        PlayTrigger("new_game");
    }

    public void HandleExternalAction(string trigger)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(trigger))
            return;

        if (trigger == "homework_complete")
            SetState("schedule.homework", "complete");
        else if (trigger == "school_complete")
            SetState("schedule.school", "complete");
        else if (trigger == "job_complete")
            SetState("schedule.job", "complete");

        PlayTrigger(trigger);
        Save();
    }

    public void HandleChoice(string choiceId)
    {
        if (activeScene == null || activeLineIndex < 0 || activeLineIndex >= activeScene.lines.Count)
            return;

        ScenarioV3Line line = activeScene.lines[activeLineIndex];
        ScenarioV3Choice choice = line.Choices.FirstOrDefault(candidate =>
            string.Equals(candidate.id, choiceId, StringComparison.OrdinalIgnoreCase));
        if (choice == null)
        {
            Debug.LogError($"[Scenario V3] 현재 장면에서 선택지 {choiceId}을 찾을 수 없습니다.");
            return;
        }

        PlaySfx(buttonClip, 0.42f);

        waitingForMessageChoice = false;
        string before = SnapshotState();
        reactiveTrigger = string.Empty;
        pendingDayAdvance = false;
        ApplyEffects(choice.effects);
        choiceHistory.Add(new ScenarioV3ChoiceRecord
        {
            choiceId = choice.id,
            sceneId = activeScene.id,
            lineId = line.id,
            day = flow.CurrentDay,
            time = flow.V3ClockText,
            choiceText = choice.text,
            stateBefore = before,
            stateAfter = SnapshotState(),
            recordedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
        });
        Save();

        string nextScene = choice.nextSceneId;
        if (pendingDayAdvance)
        {
            activeScene = null;
            HideNovel();
            QueueTrigger("day_end", AdvanceToNextDay);
            StartQueuedScene();
            return;
        }

        if (!string.IsNullOrWhiteSpace(reactiveTrigger))
        {
            activeScene = null;
            HideNovel();
            QueueTrigger(reactiveTrigger, () => ContinueAfterChoice(nextScene));
            StartQueuedScene();
            return;
        }

        ContinueAfterChoice(nextScene);
    }

    public void CompleteDayFromSleep(int sleepHours)
    {
        if (!IsReady || flow.IsGameEnded)
            return;

        state["sleep_hours"] = Mathf.Max(0, sleepHours).ToString(CultureInfo.InvariantCulture);
        SetState("schedule.sleep", sleepHours >= 5 ? "complete" : "short");
        QueueTrigger("day_end", AdvanceToNextDay);
        StartQueuedScene();
    }

    public void ClearSavedRun()
    {
        if (File.Exists(SavePath))
            File.Delete(SavePath);
    }

    public string GetState(string key)
    {
        if (string.Equals(key, "day", StringComparison.OrdinalIgnoreCase))
            return flow.CurrentDay.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(key, "cash", StringComparison.OrdinalIgnoreCase))
            return flow.V3BankCash.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(key, "debt", StringComparison.OrdinalIgnoreCase))
            return flow.CurrentDebt.ToString(CultureInfo.InvariantCulture);
        return state.TryGetValue(key ?? string.Empty, out string value) ? value : "0";
    }

    private void ResetRun()
    {
        sceneQueue.Clear();
        seenScenes.Clear();
        choiceHistory.Clear();
        state.Clear();
        state["schedule.school"] = "pending";
        state["schedule.homework"] = "pending";
        state["schedule.job"] = "pending";
        state["schedule.project"] = "pending";
        state["schedule.sleep"] = "pending";
        state["sleep_hours"] = "0";
        state["schedule_failures"] = "0";
        state["cash_delta_today"] = "0";
        state["unread_count"] = "0";
        state["day_cash_start"] = "50000";
        flow.V3ResetRun(50000);
        ClearSavedRun();
        Save();
    }

    private void ContinueAfterChoice(string nextScene)
    {
        HideNovel();
        if (!string.IsNullOrWhiteSpace(nextScene))
            PlayScene(nextScene);
        else
            FinishScene();
    }

    private void PlayTrigger(string trigger)
    {
        QueueTrigger(trigger, null);
        StartQueuedScene();
    }

    private void QueueTrigger(string trigger, Action completed)
    {
        foreach (ScenarioV3Scene scene in database.GetByTrigger(trigger))
        {
            if (!MatchesDay(scene.day) || !EvaluateCondition(scene.condition) || WasSeen(scene))
                continue;
            sceneQueue.Enqueue(scene);
        }
        queueCompleted = Combine(queueCompleted, completed);
    }

    private static Action Combine(Action first, Action second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return () => { first(); second(); };
    }

    private void PlayScene(string sceneId)
    {
        ScenarioV3Scene scene = database.GetScene(sceneId);
        if (scene == null)
        {
            Debug.LogError($"[Scenario V3] 장면을 찾을 수 없습니다: {sceneId}");
            return;
        }
        sceneQueue.Clear();
        queueCompleted = null;
        BeginScene(scene);
    }

    private void StartQueuedScene()
    {
        if (activeScene != null || waitingForMessageChoice)
            return;

        if (sceneQueue.Count > 0)
        {
            BeginScene(sceneQueue.Dequeue());
            return;
        }

        Action completed = queueCompleted;
        queueCompleted = null;
        completed?.Invoke();
    }

    private void BeginScene(ScenarioV3Scene scene)
    {
        activeScene = scene;
        activeLineIndex = 0;
        pendingDayAdvance = false;
        seenScenes.Add(SceneSeenKey(scene));
        if (scene.lines.Count == 0)
        {
            FinishScene();
            return;
        }
        PresentLine();
    }

    private void PresentLine()
    {
        if (activeScene == null || activeLineIndex >= activeScene.lines.Count)
        {
            FinishScene();
            return;
        }

        ScenarioV3Line line = activeScene.lines[activeLineIndex];
        immediateRoute = string.Empty;
        ApplyEffects(line.enterEffects);
        if (!string.IsNullOrWhiteSpace(immediateRoute))
        {
            string target = immediateRoute;
            immediateRoute = string.Empty;
            activeScene = null;
            PlayScene(target);
            return;
        }
        string delivery = (line.delivery ?? string.Empty).ToLowerInvariant();
        if (delivery == "router")
        {
            FinishLine(line);
            return;
        }
        if (delivery == "ending")
        {
            ShowEnding(activeScene);
            return;
        }
        if (delivery == "message")
        {
            PresentMessage(line);
            return;
        }

        ShowNovelLine(line);
    }

    private void PresentMessage(ScenarioV3Line line)
    {
        SpeakerType speaker = MapSpeaker(line.speaker);
        string contact = string.IsNullOrWhiteSpace(line.contact) ? ContactName(line.speaker) : line.contact;
        string text = ExpandText(line.text);
        var data = new NotificationData
        {
            title = contact,
            message = text,
            appType = AppType.Message,
            speakerType = speaker
        };
        bool announce = line.sequence == 1;
        if (announce && notifications != null)
            notifications.SendNotification(data);
        else
            dialogue?.ReceiveNotificationMessage(speaker, contact, text);
        if (announce)
            PlaySfx(popupClip, 0.48f);
        AddInt("unread_count", 1);

        List<ScenarioV3Choice> choices = line.Choices.ToList();
        if (choices.Count > 0)
        {
            waitingForMessageChoice = true;
            var chatChoices = new List<Choice>();
            foreach (ScenarioV3Choice choice in choices)
            {
                chatChoices.Add(new Choice
                {
                    choiceText = choice.text,
                    nextDialogueID = -1,
                    scenarioAction = "v3-choice:" + choice.id
                });
            }
            dialogue?.SetEventChoices(speaker, chatChoices);
            StartCoroutine(OpenMessageConversation(speaker));
            return;
        }

        StartCoroutine(AdvanceMessageLine(line));
    }

    private IEnumerator OpenMessageConversation(SpeakerType speaker)
    {
        appWindow?.OpenMessage();
        yield return new WaitForSeconds(0.55f);
        dialogue?.OpenDialogue(speaker);
    }

    private IEnumerator AdvanceMessageLine(ScenarioV3Line line)
    {
        yield return new WaitForSeconds(0.28f);
        FinishLine(line);
    }

    private void ShowNovelLine(ScenarioV3Line line)
    {
        if (novelPanel == null)
            return;

        notifications?.HidePopup();
        novelPanel.SetActive(true);
        novelPanel.transform.SetAsLastSibling();
        chapterText.text = $"{flow.CurrentDay}일차  {flow.V3ClockText}";
        speakerText.text = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ContactName(line.speaker);
        bodyText.text = ExpandText(line.text);
        ApplyArcVisual(activeScene.arc, line.delivery);

        List<ScenarioV3Choice> choices = line.Choices.ToList();
        continueButton.gameObject.SetActive(choices.Count == 0);
        ConfigureChoiceButton(choiceAButton, choices.Count > 0 ? choices[0] : null);
        ConfigureChoiceButton(choiceBButton, choices.Count > 1 ? choices[1] : null);
        continueButton.onClick.RemoveAllListeners();
        continueButton.onClick.AddListener(() => FinishLine(line));
    }

    private void ConfigureChoiceButton(Button button, ScenarioV3Choice choice)
    {
        button.gameObject.SetActive(choice != null);
        if (choice == null)
            return;
        button.GetComponentInChildren<TMP_Text>().text = choice.text;
        button.onClick.RemoveAllListeners();
        string choiceId = choice.id;
        button.onClick.AddListener(() => HandleChoice(choiceId));
    }

    private void FinishLine(ScenarioV3Line line)
    {
        HideNovel();
        string next = line.autoNext;
        activeLineIndex++;
        if (!string.IsNullOrWhiteSpace(next))
        {
            activeScene = null;
            PlayScene(next);
            return;
        }
        PresentLine();
    }

    private void FinishScene()
    {
        activeScene = null;
        activeLineIndex = 0;
        HideNovel();
        Save();
        if (pendingDayAdvance)
        {
            pendingDayAdvance = false;
            QueueTrigger("day_end", AdvanceToNextDay);
            StartQueuedScene();
            return;
        }
        StartQueuedScene();
    }

    private void AdvanceToNextDay()
    {
        int previousCash = GetInt("day_cash_start", 50000);
        state["cash_delta_today"] = (flow.V3BankCash - previousCash).ToString(CultureInfo.InvariantCulture);
        state["previous.homework_status"] = GetState("schedule.homework");
        bool requiredDone = flow.CurrentDay == 6
            ? GetState("schedule.job") == "complete"
            : GetState("schedule.school") == "complete" && GetState("schedule.homework") == "complete";
        if (!requiredDone && flow.CurrentDay < FinalDay)
            AddInt("schedule_failures", 1);

        if (flow.CurrentDay >= FinalDay)
        {
            PlayTrigger("d7_main");
            return;
        }

        flow.V3BeginNextDay();
        state["schedule.school"] = "pending";
        state["schedule.homework"] = "pending";
        state["schedule.job"] = "pending";
        state["schedule.sleep"] = "pending";
        state["unread_count"] = "0";
        state["day_cash_start"] = flow.V3BankCash.ToString(CultureInfo.InvariantCulture);
        Save();

        QueueTrigger("day_start", () =>
        {
            if (flow.CurrentDay == FinalDay)
                PlayTrigger("d7_main");
            else
                PlayTrigger("morning_schedule");
        });
        StartQueuedScene();
    }

    private void ApplyEffects(string effects)
    {
        if (string.IsNullOrWhiteSpace(effects))
            return;

        foreach (string raw in effects.Split('|'))
        {
            string directive = raw.Trim();
            if (directive.Length == 0)
                continue;
            ApplyEffect(directive);
        }
        flow.V3Refresh();
    }

    private void ApplyEffect(string directive)
    {
        int colon = directive.IndexOf(':');
        if (colon < 0)
            return;
        string key = directive.Substring(0, colon).Trim();
        string operation = directive.Substring(colon + 1).Trim();

        if (key.Equals("route", StringComparison.OrdinalIgnoreCase))
        {
            immediateRoute = EvaluateRoute(operation);
            return;
        }
        if (key.Equals("ending", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.StartsWith("route(", StringComparison.OrdinalIgnoreCase))
            {
                string target = EvaluateRoute(operation.Substring(6, operation.Length - 7));
                immediateRoute = "ending_" + target;
            }
            else if (operation.StartsWith("set=", StringComparison.OrdinalIgnoreCase))
            {
                SetState("ending", operation.Substring(4));
            }
            return;
        }
        if (key.Equals("clock", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.StartsWith("set=")) flow.V3SetClock(operation.Substring(4));
            else if (operation.StartsWith("add=")) flow.V3AddMinutes(ResolveInt(operation.Substring(4)));
            else if (operation.StartsWith("advance_to_next_day=")) pendingDayAdvance = true;
            return;
        }
        if (key.Equals("cash", StringComparison.OrdinalIgnoreCase))
        {
            int before = flow.V3BankCash;
            if (operation.StartsWith("set=")) flow.V3SetCash(ResolveInt(operation.Substring(4)));
            else if (operation.StartsWith("add=")) flow.V3AddCash(ResolveInt(operation.Substring(4)));
            AddInt("cash_delta_today", flow.V3BankCash - before);
            return;
        }
        if (key.Equals("debt", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.StartsWith("set=")) flow.V3SetDebt(ResolveInt(operation.Substring(4)));
            else if (operation.StartsWith("add=")) flow.V3AddDebt(ResolveInt(operation.Substring(4)));
            return;
        }
        if (key.Equals("location", StringComparison.OrdinalIgnoreCase) && operation.StartsWith("set="))
        {
            flow.V3SetLocation(operation.Substring(4));
            return;
        }
        if (key.Equals("sleep", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.StartsWith("set=")) state["sleep_hours"] = ResolveInt(operation.Substring(4)).ToString();
            else if (operation.StartsWith("calculate_until=")) state["sleep_hours"] = GameFlowManager.GetSleepHoursUntilSeven(flow.CurrentHour).ToString();
            return;
        }
        if (key.Equals("history", StringComparison.OrdinalIgnoreCase) || key.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            return;
        if (key.Equals("schedule.project", StringComparison.OrdinalIgnoreCase) && operation == "resolve_deadline")
        {
            SetState(key, flow.CurrentHour <= 21 && GetState(key) == "complete" ? "complete" : "missed");
            return;
        }

        if (operation.StartsWith("set=", StringComparison.OrdinalIgnoreCase))
        {
            string value = operation.Substring(4);
            if (key.Equals("schedule.project", StringComparison.OrdinalIgnoreCase) && value == "complete_if_before_deadline")
                value = flow.CurrentHour <= 21 ? "complete" : "missed";
            if (key.Equals("schedule.school", StringComparison.OrdinalIgnoreCase) && value == "complete")
                reactiveTrigger = "school_complete";
            SetState(key, value);
        }
        else if (operation.StartsWith("add=", StringComparison.OrdinalIgnoreCase))
        {
            AddInt(key, ResolveInt(operation.Substring(4)));
        }
    }

    private string EvaluateRoute(string expression)
    {
        Match match = Regex.Match(expression,
            @"^(.+?)\s+if\s+(.+?)\s+else\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return expression.Trim();
        return EvaluateCondition(match.Groups[2].Value)
            ? match.Groups[1].Value.Trim()
            : match.Groups[3].Value.Trim();
    }

    private bool EvaluateCondition(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        string normalized = Regex.Replace(expression, @"\s+and\s+", ";", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+or\s+", "|", RegexOptions.IgnoreCase);
        foreach (string andPart in normalized.Split(';'))
        {
            bool any = false;
            foreach (string orPart in andPart.Split('|'))
            {
                if (EvaluateComparison(orPart.Trim()))
                {
                    any = true;
                    break;
                }
            }
            if (!any) return false;
        }
        return true;
    }

    private bool EvaluateComparison(string expression)
    {
        Match match = Regex.Match(expression, @"^([\w.]+)\s*(<=|>=|!=|=|<|>)\s*(.+)$");
        if (!match.Success)
            return false;
        string actual = GetState(match.Groups[1].Value);
        string expected = match.Groups[3].Value.Trim();
        string op = match.Groups[2].Value;
        if (int.TryParse(actual, out int actualInt) && int.TryParse(expected, out int expectedInt))
        {
            return op switch
            {
                "=" => actualInt == expectedInt,
                "!=" => actualInt != expectedInt,
                ">" => actualInt > expectedInt,
                "<" => actualInt < expectedInt,
                ">=" => actualInt >= expectedInt,
                "<=" => actualInt <= expectedInt,
                _ => false
            };
        }
        bool equal = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return op == "!=" ? !equal : equal;
    }

    private bool MatchesDay(string dayExpression)
    {
        if (string.IsNullOrWhiteSpace(dayExpression))
            return true;
        if (dayExpression.Contains(".."))
        {
            string[] range = dayExpression.Split(new[] { ".." }, StringSplitOptions.None);
            return range.Length == 2 && int.TryParse(range[0], out int min) &&
                   int.TryParse(range[1], out int max) && flow.CurrentDay >= min && flow.CurrentDay <= max;
        }
        return !int.TryParse(dayExpression, out int day) || day == flow.CurrentDay;
    }

    private bool WasSeen(ScenarioV3Scene scene)
    {
        return seenScenes.Contains(SceneSeenKey(scene));
    }

    private string SceneSeenKey(ScenarioV3Scene scene)
    {
        return string.Equals(scene.onceScope, "day", StringComparison.OrdinalIgnoreCase)
            ? $"{scene.id}@{flow.CurrentDay}"
            : scene.id;
    }

    private int ResolveInt(string raw)
    {
        string value = raw.Trim();
        if (value.StartsWith("{") && value.EndsWith("}"))
        {
            string key = value.Substring(1, value.Length - 2);
            if (key == "study_minutes") return 120 + GetInt("modifier.study_minutes", 0);
            if (key == "project_minutes") return GetInt("modifier.project_minutes", 120);
            value = GetState(key);
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
    }

    private void SetState(string key, string value)
    {
        state[key] = value;
        if (key == "schedule.school" || key == "schedule.homework" || key == "schedule.job" || key == "schedule.sleep")
            flow.V3SetSchedule(key.Substring("schedule.".Length), value);
    }

    private int GetInt(string key, int fallback = 0)
    {
        return int.TryParse(GetState(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private void AddInt(string key, int amount)
    {
        state[key] = (GetInt(key) + amount).ToString(CultureInfo.InvariantCulture);
    }

    private string ExpandText(string text)
    {
        return Regex.Replace(text ?? string.Empty, @"\{([\w.]+)\}", match =>
        {
            string key = match.Groups[1].Value;
            string value = key switch
            {
                "cash" => flow.V3BankCash.ToString("N0"),
                "debt" => flow.CurrentDebt.ToString("N0"),
                "school_status" => StatusText(GetState("schedule.school")),
                "homework_status" => StatusText(GetState("schedule.homework")),
                "job_status" => StatusText(GetState("schedule.job")),
                "planned_sleep_hours" => GetState("sleep_hours"),
                "cash_delta_today" => GetInt("cash_delta_today").ToString("+#,0;-#,0;0"),
                "help_source_name" => GetState("help_source") == "teacher" ? "담임 선생님" : "엄마",
                "study_minutes" => ResolveInt("{study_minutes}").ToString(),
                "project_minutes" => ResolveInt("{project_minutes}").ToString(),
                _ => GetState(key)
            };
            return value;
        });
    }

    private static string StatusText(string value)
    {
        return value switch
        {
            "complete" => "완료",
            "missed" => "놓침",
            "short" => "부족",
            _ => "미완료"
        };
    }

    private void ShowEnding(ScenarioV3Scene scene)
    {
        string endingId = GetState("ending");
        string title = endingId switch
        {
            "risk_blocked" => "멈추기로 한 선택",
            "help" => "도움을 요청한 선택",
            "collapse" => "무너진 일상",
            _ => "남겨진 문제"
        };
        string body = string.Join("\n\n", scene.lines.Select(line => ExpandText(line.text)));
        activeScene = null;
        Save();
        flow.V3EndGame(title, body);
    }

    private void CreateNovelUI()
    {
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsInactive.Include)
            .FirstOrDefault(candidate => candidate.transform.parent == null) ?? FindAnyObjectByType<Canvas>();
        if (canvas == null)
            return;
        TMP_FontAsset font = FindObjectsByType<TMP_Text>(FindObjectsInactive.Include)
            .Select(text => text.font).FirstOrDefault(candidate => candidate != null && candidate.name.Contains("NotoSansKR"));

        novelPanel = Panel("Scenario V3 Novel", canvas.transform, new Color(0.03f, 0.05f, 0.08f, 1f));
        Stretch(novelPanel.GetComponent<RectTransform>());
        novelBackground = new GameObject("Illustrated Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
        novelBackground.transform.SetParent(novelPanel.transform, false);
        Stretch(novelBackground.rectTransform);
        novelBackground.color = new Color(0.72f, 0.79f, 0.86f, 1f);

        GameObject shade = Panel("Readability Shade", novelPanel.transform, new Color(0.02f, 0.025f, 0.04f, 0.28f));
        Stretch(shade.GetComponent<RectTransform>());
        chapterText = Text("Chapter", novelPanel.transform, font, 23, FontStyles.Bold, Color.white);
        SetRect(chapterText.rectTransform, new Vector2(42f, -42f), new Vector2(420f, 46f));

        GameObject dialogueBox = Panel("Dialogue Box", novelPanel.transform, new Color(0.025f, 0.045f, 0.075f, 0.96f));
        RectTransform boxRect = dialogueBox.GetComponent<RectTransform>();
        boxRect.anchorMin = new Vector2(0.06f, 0.06f);
        boxRect.anchorMax = new Vector2(0.94f, 0.39f);
        boxRect.offsetMin = boxRect.offsetMax = Vector2.zero;
        speakerText = Text("Speaker", dialogueBox.transform, font, 27, FontStyles.Bold, new Color(0.44f, 0.72f, 1f));
        SetRect(speakerText.rectTransform, new Vector2(34f, -22f), new Vector2(420f, 42f));
        bodyText = Text("Body", dialogueBox.transform, font, 29, FontStyles.Normal, Color.white);
        bodyText.alignment = TextAlignmentOptions.TopLeft;
        bodyText.textWrappingMode = TextWrappingModes.Normal;
        RectTransform bodyRect = bodyText.rectTransform;
        bodyRect.anchorMin = new Vector2(0f, 0.34f);
        bodyRect.anchorMax = Vector2.one;
        bodyRect.offsetMin = new Vector2(34f, 12f);
        bodyRect.offsetMax = new Vector2(-34f, -72f);

        continueButton = Button("Continue", dialogueBox.transform, font, "계속", new Color(0.17f, 0.46f, 0.78f));
        PlaceBottomButton(continueButton, 0.5f, 0.38f);
        choiceAButton = Button("Choice A", dialogueBox.transform, font, "선택지 A", new Color(0.14f, 0.39f, 0.68f));
        PlaceBottomButton(choiceAButton, 0.28f, 0.42f);
        choiceBButton = Button("Choice B", dialogueBox.transform, font, "선택지 B", new Color(0.47f, 0.25f, 0.31f));
        PlaceBottomButton(choiceBButton, 0.72f, 0.42f);
        novelPanel.SetActive(false);
    }

    private void CreateAudio()
    {
        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        popupClip = Resources.Load<AudioClip>("Audio/SFX/popup");
        buttonClip = Resources.Load<AudioClip>("Audio/SFX/button_click");
        bgmSource = gameObject.AddComponent<AudioSource>();
        bgmSource.loop = true;
        bgmSource.volume = 0.16f;
        temptationBgm = Resources.Load<AudioClip>("Audio/BGM/temptation");
    }

    private void ApplyArcVisual(string arc, string delivery)
    {
        string resource = arc switch
        {
            "school" or "homework" => "ScenarioArt/classroom",
            "job" => "ScenarioArt/cafe",
            "gambling" or "debt" => "ScenarioArt/temptation",
            "sleep" => "ScenarioArt/bedroom_night",
            _ => "ScenarioArt/bedroom"
        };
        novelBackground.texture = Resources.Load<Texture2D>(resource);
        novelBackground.color = novelBackground.texture == null ? ArcColor(arc) : Color.white;
        bool temptation = arc == "gambling" && delivery == "cinematic";
        if (temptation && temptationBgm != null && !bgmSource.isPlaying)
        {
            bgmSource.clip = temptationBgm;
            bgmSource.Play();
        }
        else if (!temptation && bgmSource.isPlaying)
        {
            bgmSource.Stop();
        }
    }

    private static Color ArcColor(string arc)
    {
        return arc switch
        {
            "school" or "homework" => new Color(0.58f, 0.72f, 0.82f),
            "job" => new Color(0.55f, 0.66f, 0.58f),
            "gambling" or "debt" => new Color(0.24f, 0.09f, 0.13f),
            "sleep" => new Color(0.12f, 0.18f, 0.28f),
            _ => new Color(0.62f, 0.66f, 0.74f)
        };
    }

    private void PlaySfx(AudioClip clip, float volume)
    {
        if (clip != null && sfxSource != null)
            sfxSource.PlayOneShot(clip, volume);
    }

    private void HideNovel()
    {
        if (novelPanel != null)
            novelPanel.SetActive(false);
    }

    private void Save()
    {
        var data = new ScenarioV3SaveData
        {
            state = state.OrderBy(pair => pair.Key)
                .Select(pair => new ScenarioV3StateEntry { key = pair.Key, value = pair.Value }).ToList(),
            choices = new List<ScenarioV3ChoiceRecord>(choiceHistory),
            seenScenes = seenScenes.OrderBy(value => value).ToList()
        };
        File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
    }

    private string SnapshotState()
    {
        var entries = state.OrderBy(pair => pair.Key)
            .Select(pair => new ScenarioV3StateEntry { key = pair.Key, value = pair.Value }).ToList();
        entries.Add(new ScenarioV3StateEntry { key = "cash", value = flow.V3BankCash.ToString() });
        entries.Add(new ScenarioV3StateEntry { key = "day", value = flow.CurrentDay.ToString() });
        entries.Add(new ScenarioV3StateEntry { key = "debt", value = flow.CurrentDebt.ToString() });
        return JsonUtility.ToJson(new ScenarioV3SaveData { state = entries });
    }

    private static SpeakerType MapSpeaker(string speaker)
    {
        return (speaker ?? string.Empty).ToLowerInvariant() switch
        {
            "minjae" => SpeakerType.Friend,
            "mom" => SpeakerType.Mom,
            "teacher" => SpeakerType.Teacher,
            "cafemanager" => SpeakerType.CafeManager,
            "seoyeon" => SpeakerType.Seoyeon,
            "seojun" => SpeakerType.Joonho,
            "bank" => SpeakerType.Bank,
            "counselor" => SpeakerType.Counselor,
            _ => SpeakerType.Unknown
        };
    }

    private static string ContactName(string speaker)
    {
        return (speaker ?? string.Empty).ToLowerInvariant() switch
        {
            "minjae" => "민재",
            "mom" => "엄마",
            "teacher" => "담임 선생님",
            "cafemanager" => "카페 매니저",
            "seoyeon" => "서연",
            "seojun" => "서준",
            "bank" => "은행",
            "counselor" => "상담 선생님",
            "narrator" => "",
            _ => "알림"
        };
    }

    private static GameObject Panel(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font, float size, FontStyles style, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        TMP_Text text = go.GetComponent<TMP_Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        return text;
    }

    private static Button Button(string name, Transform parent, TMP_FontAsset font, string label, Color color)
    {
        GameObject go = Panel(name, parent, color);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        TMP_Text text = Text("Label", go.transform, font, 22, FontStyles.Bold, Color.white);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        Stretch(text.rectTransform);
        return button;
    }

    private static void PlaceBottomButton(Button button, float anchorX, float width)
    {
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(anchorX - width * 0.5f, 0.08f);
        rect.anchorMax = new Vector2(anchorX + width * 0.5f, 0.29f);
        rect.offsetMin = new Vector2(8f, 0f);
        rect.offsetMax = new Vector2(-8f, 0f);
    }

    private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
