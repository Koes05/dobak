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
public sealed class ScenarioV3CheckpointData
{
    public string label;
    public string sceneId;
    public string lineId;
    public int lineIndex;
    public int day;
    public int hour;
    public string location;
    public int cash;
    public int debt;
    public int choiceCount;
    public List<ScenarioV3StateEntry> state = new List<ScenarioV3StateEntry>();
    public List<string> seenScenes = new List<string>();
}

[Serializable]
public sealed class ScenarioV3SaveData
{
    public int version = 3;
    public List<ScenarioV3StateEntry> state = new List<ScenarioV3StateEntry>();
    public List<ScenarioV3ChoiceRecord> choices = new List<ScenarioV3ChoiceRecord>();
    public List<string> seenScenes = new List<string>();
    public List<ScenarioV3CheckpointData> checkpoints = new List<ScenarioV3CheckpointData>();
    public List<string> dialogueLog = new List<string>();
}

public sealed class ScenarioV3Director : MonoBehaviour
{
    private const int FinalDay = 14;

    private readonly Dictionary<string, string> state =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScenarioV3ChoiceRecord> choiceHistory = new List<ScenarioV3ChoiceRecord>();
    private readonly List<ScenarioV3CheckpointData> checkpoints = new List<ScenarioV3CheckpointData>();
    private readonly HashSet<string> seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ScenarioV3Scene> sceneQueue = new Queue<ScenarioV3Scene>();
    private readonly List<string> dialogueLog = new List<string>();
    private readonly HashSet<string> deliveredOutgoingLineIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
    private SpeakerType waitingMessageSpeaker = SpeakerType.Unknown;
    private ScenarioV3Line pendingOutgoingLine;
    private SpeakerType pendingOutgoingSpeaker = SpeakerType.Unknown;
    private string pendingOutgoingContact = string.Empty;
    private string pendingOutgoingText = string.Empty;
    private Coroutine pendingOutgoingCoroutine;
    private Coroutine typewriterCoroutine;
    private string currentFullText = string.Empty;
    private List<string> currentDialoguePages = new List<string>();
    private int currentDialoguePageIndex;
    private bool isTyping;

    private GameObject novelPanel;
    private RawImage novelBackground;
    private RawImage characterPortrait;
    private TMP_Text chapterText;
    private TMP_Text speakerText;
    private TMP_Text bodyText;
    private Button continueButton;
    private Button choiceAButton;
    private Button choiceBButton;
    private Button choiceCButton;
    private GameObject historyPanel;
    private TMP_Text historyText;
    private AudioSource sfxSource;
    private AudioSource typingSource;
    private AudioClip popupClip;
    private AudioClip buttonClip;
    private AudioClip typingClip;

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
    public bool CanRewind => checkpoints.Count > 0;
    public string RewindLabel => FindRewindCheckpoint()?.label ?? string.Empty;
    public bool HasPendingMessageAction => pendingOutgoingLine != null || waitingForMessageChoice || GetInt("unread_count") > 0;

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
        else if (trigger == "job_missed")
            SetState("schedule.job", "missed");

        PlayTrigger(trigger);
        TrySendUnreadReminder();
        Save();
    }

    public void NotifyAppOpened(AppType? app)
    {
        if (app == AppType.Message)
        {
            SetState("unread_count", "0");
            flow.V3HideTutorialHint(AppType.Message);
            TryDeliverPendingOutgoingMessage();
            Save();
        }
        else if (app == AppType.Map)
        {
            flow.V3HideTutorialHint(AppType.Map);
        }
        else if (app == AppType.Study)
        {
            flow.V3HideTutorialHint(AppType.Study);
        }
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

        PlaySfx(buttonClip, 0.24f);

        waitingForMessageChoice = false;
        waitingMessageSpeaker = SpeakerType.Unknown;
        flow.V3Refresh();
        AppendDialogueLog("나", string.IsNullOrWhiteSpace(choice.replyText) ? choice.text : choice.replyText);
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
        if (!string.IsNullOrWhiteSpace(immediateRoute))
        {
            string routedScene = immediateRoute;
            immediateRoute = string.Empty;
            activeScene = null;
            HideNovel();
            PlayScene(routedScene);
            return;
        }
        if (pendingDayAdvance)
        {
            FinalizeCurrentDayStatus();
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

        if (waitingForMessageChoice)
        {
            dialogue?.DismissEventChoices(waitingMessageSpeaker);
            if (waitingMessageSpeaker == SpeakerType.Friend)
                AddInt("counter.minjae_ignored", 1);
            waitingForMessageChoice = false;
            waitingMessageSpeaker = SpeakerType.Unknown;
            activeScene = null;
            activeLineIndex = 0;
        }

        SetState("schedule.sleep", "complete");
        QueueTrigger("before_sleep", CompleteSleepDay);
        StartQueuedScene();
    }

    private void CompleteSleepDay()
    {
        FinalizeCurrentDayStatus();
        QueueTrigger("day_end", AdvanceToNextDay);
        StartQueuedScene();
    }

    public void ClearSavedRun()
    {
        if (File.Exists(SavePath))
            File.Delete(SavePath);
    }

    public void RestorePreviousCheckpoint()
    {
        ScenarioV3CheckpointData checkpoint = FindRewindCheckpoint();
        if (checkpoint == null)
            return;

        sceneQueue.Clear();
        queueCompleted = null;
        waitingForMessageChoice = false;
        ClearPendingOutgoingMessage();
        deliveredOutgoingLineIds.Clear();
        pendingDayAdvance = false;
        reactiveTrigger = string.Empty;
        immediateRoute = string.Empty;
        state.Clear();
        foreach (ScenarioV3StateEntry entry in checkpoint.state)
            state[entry.key] = entry.value;
        seenScenes.Clear();
        foreach (string seen in checkpoint.seenScenes)
            seenScenes.Add(seen);
        if (choiceHistory.Count > checkpoint.choiceCount)
            choiceHistory.RemoveRange(checkpoint.choiceCount, choiceHistory.Count - checkpoint.choiceCount);
        checkpoints.RemoveAll(candidate => candidate.day > checkpoint.day);

        dialogue?.ResetScenarioConversations();
        notifications?.Clear();
        appWindow?.CloseCurrentApp();
        flow.V3RestoreRun(checkpoint.day, checkpoint.hour, checkpoint.location,
            checkpoint.cash, checkpoint.debt);
        flow.V3SetSchedule("school", GetState("schedule.school"));
        flow.V3SetSchedule("homework", GetState("schedule.homework"));
        flow.V3SetSchedule("job", GetState("schedule.job"));
        flow.V3SetSchedule("sleep", GetState("schedule.sleep"));

        activeScene = database.GetScene(checkpoint.sceneId);
        activeLineIndex = checkpoint.lineIndex;
        HideNovel();
        Save();
        PresentLine();
    }

    public string GetState(string key)
    {
        if (string.Equals(key, "day", StringComparison.OrdinalIgnoreCase))
            return flow.CurrentDay.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(key, "cash", StringComparison.OrdinalIgnoreCase))
            return flow.V3BankCash.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(key, "debt", StringComparison.OrdinalIgnoreCase))
            return flow.CurrentDebt.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(key, "hour", StringComparison.OrdinalIgnoreCase))
            return flow.CurrentHour.ToString(CultureInfo.InvariantCulture);
        return state.TryGetValue(key ?? string.Empty, out string value) ? value : "0";
    }

    private void ResetRun()
    {
        sceneQueue.Clear();
        activeScene = null;
        activeLineIndex = 0;
        waitingForMessageChoice = false;
        waitingMessageSpeaker = SpeakerType.Unknown;
        ClearPendingOutgoingMessage();
        seenScenes.Clear();
        choiceHistory.Clear();
        checkpoints.Clear();
        dialogueLog.Clear();
        deliveredOutgoingLineIds.Clear();
        state.Clear();
        state["schedule.school"] = "pending";
        state["schedule.homework"] = "pending";
        state["schedule.job"] = "pending";
        state["schedule.project"] = "pending";
        state["schedule.sleep"] = "pending";
        state["evening_filled"] = "0";
        state["bedtime_cued"] = "0";
        state["sleep_hours"] = "0";
        state["schedule_failures"] = "0";
        state["cash_delta_today"] = "0";
        state["unread_count"] = "0";
        state["day_cash_start"] = "50000";
        state["day_finalized"] = "0";
        state["counter.job_attendance"] = "0";
        state["counter.gamble_sessions"] = "0";
        state["relation.seoyeon"] = "0";
        state["relation.manager"] = "0";
        flow.V3ResetRun(50000);
        ClearSavedRun();
        Save();
    }

    private void ContinueAfterChoice(string nextScene)
    {
        HideNovel();
        if (!string.IsNullOrWhiteSpace(nextScene))
        {
            if (!TryReturnHomeBeforeNextScene(nextScene, () => PlayScene(nextScene)))
                PlayScene(nextScene);
        }
        else
            FinishScene();
    }

    private void PlayTrigger(string trigger)
    {
        QueueTrigger(trigger, null);
        StartQueuedScene();
    }

    private int QueueTrigger(string trigger, Action completed)
    {
        int queued = 0;
        foreach (ScenarioV3Scene scene in database.GetByTrigger(trigger))
        {
            if (!MatchesDay(scene.day) || !EvaluateCondition(scene.condition) || WasSeen(scene))
                continue;
            sceneQueue.Enqueue(scene);
            queued++;
        }
        queueCompleted = Combine(queueCompleted, completed);
        return queued;
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
        CaptureCheckpointIfNeeded(line);
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
        if (delivery == "overlay")
        {
            ShowTabletOverlayLine(line);
            return;
        }
        if (delivery == "message")
        {
            PresentMessage(line);
            return;
        }

        ShowNovelLine(line);
    }

    private void ShowTabletOverlayLine(ScenarioV3Line line)
    {
        string title = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(line.speaker, "System", StringComparison.OrdinalIgnoreCase)
            ? "안내"
            : ContactName(line.speaker);
        string text = ExpandText(line.text);
        if (!flow.V3ShowDialogue(title, text, () => FinishLine(line)))
            ShowNovelLine(line);
    }

    private void PresentMessage(ScenarioV3Line line)
    {
        bool sentByPlayer = string.Equals(line.speaker, "Protagonist", StringComparison.OrdinalIgnoreCase);
        SpeakerType speaker = sentByPlayer ? MapContact(line.contact) : MapSpeaker(line.speaker);
        string contact = string.IsNullOrWhiteSpace(line.contact)
            ? ContactName(line.speaker)
            : line.contact;
        string text = ExpandText(line.text);
        if (sentByPlayer)
        {
            pendingOutgoingLine = line;
            pendingOutgoingSpeaker = speaker;
            pendingOutgoingContact = contact;
            pendingOutgoingText = text;
            flow.V3MarkAppAttention(AppType.Message);
            TryDeliverPendingOutgoingMessage();
            return;
        }

        var data = new NotificationData
        {
            title = contact,
            message = text,
            appType = AppType.Message,
            speakerType = speaker
        };
        bool speakerChanged = activeLineIndex == 0 ||
            !string.Equals(activeScene.lines[activeLineIndex - 1].speaker, line.speaker,
                StringComparison.OrdinalIgnoreCase);
        bool announce = line.sequence == 1 || speakerChanged;
        bool messageAppOpen = appWindow != null && appWindow.CurrentAppType == AppType.Message;
        if (announce && notifications != null && !messageAppOpen)
            notifications.SendNotification(data);
        else
            dialogue?.ReceiveNotificationMessage(speaker, contact, text);
        if (announce && !messageAppOpen)
        {
            PlaySfx(popupClip, 0.26f);
            AddInt("unread_count", 1);
            flow.V3Refresh();
        }

        List<ScenarioV3Choice> choices = line.Choices.ToList();
        if (choices.Count > 0)
        {
            waitingForMessageChoice = true;
            waitingMessageSpeaker = speaker;
            flow.V3Refresh();
            var chatChoices = new List<Choice>();
            foreach (ScenarioV3Choice choice in choices)
            {
                chatChoices.Add(new Choice
                {
                    choiceText = choice.text,
                    replyText = choice.replyText,
                    nextDialogueID = -1,
                    scenarioAction = "v3-choice:" + choice.id
                });
            }
            dialogue?.SetEventChoices(speaker, chatChoices);
            return;
        }

        bool isTerminalMessage = activeScene != null && activeLineIndex >= activeScene.lines.Count - 1;
        StartCoroutine(AdvanceMessageLine(line, isTerminalMessage));
    }

    private void TryDeliverPendingOutgoingMessage()
    {
        if (pendingOutgoingLine == null || pendingOutgoingCoroutine != null ||
            !IsMessageAppReady())
            return;

        pendingOutgoingCoroutine = StartCoroutine(DeliverPendingOutgoingMessage());
    }

    private bool IsMessageAppReady()
    {
        if (appWindow == null || appWindow.CurrentAppType != AppType.Message || dialogue == null)
            return false;

        Transform parent = dialogue.transform.parent;
        return parent == null || parent.gameObject.activeInHierarchy;
    }

    private bool IsMessageUiReady()
    {
        return IsMessageAppReady() &&
               dialogue != null && dialogue.isActiveAndEnabled && dialogue.gameObject.activeInHierarchy;
    }

    private IEnumerator DeliverPendingOutgoingMessage()
    {
        ScenarioV3Line line = pendingOutgoingLine;
        SpeakerType speaker = pendingOutgoingSpeaker;
        string contact = pendingOutgoingContact;
        string text = pendingOutgoingText;

        yield return new WaitForSecondsRealtime(0.18f);
        if (!IsMessageAppReady())
        {
            pendingOutgoingCoroutine = null;
            yield break;
        }

        dialogue.OpenDialogue(speaker);
        yield return new WaitForSecondsRealtime(0.3f);

        if (pendingOutgoingLine != line || !IsMessageUiReady())
        {
            pendingOutgoingCoroutine = null;
            yield break;
        }

        if (deliveredOutgoingLineIds.Add(line.id))
        {
            dialogue?.ReceivePlayerMessage(speaker, contact, text);
            PlaySfx(buttonClip, 0.2f);
        }
        bool isTerminalPlayerMessage = activeScene != null && activeLineIndex >= activeScene.lines.Count - 1;
        ClearPendingOutgoingMessage(false);

        yield return new WaitForSecondsRealtime(0.4f);
        while (isTerminalPlayerMessage && appWindow != null && appWindow.CurrentAppType == AppType.Message)
            yield return null;

        FinishLine(line);
    }

    private void ClearPendingOutgoingMessage(bool stopCoroutine = true)
    {
        if (stopCoroutine && pendingOutgoingCoroutine != null)
            StopCoroutine(pendingOutgoingCoroutine);
        pendingOutgoingCoroutine = null;
        pendingOutgoingLine = null;
        pendingOutgoingSpeaker = SpeakerType.Unknown;
        pendingOutgoingContact = string.Empty;
        pendingOutgoingText = string.Empty;
    }

    private IEnumerator AdvanceMessageLine(ScenarioV3Line line, bool waitForMessageAppClose)
    {
        yield return new WaitForSeconds(0.28f);
        while (waitForMessageAppClose && appWindow != null &&
               appWindow.CurrentAppType == AppType.Message)
        {
            yield return null;
        }
        FinishLine(line);
    }

    private void TrySendUnreadReminder()
    {
        if (!waitingForMessageChoice || waitingMessageSpeaker != SpeakerType.Friend ||
            flow.CurrentDay < 2 || GetInt("unread_count") <= 0 ||
            appWindow?.CurrentAppType == AppType.Message)
            return;

        foreach (ScenarioV3Scene scene in database.GetByTrigger("minjae_unread_reminder"))
        {
            if (!MatchesDay(scene.day) || !EvaluateCondition(scene.condition) || WasSeen(scene))
                continue;

            seenScenes.Add(SceneSeenKey(scene));
            foreach (ScenarioV3Line line in scene.lines.Where(candidate =>
                         string.Equals(candidate.delivery, "message", StringComparison.OrdinalIgnoreCase)))
            {
                string contact = string.IsNullOrWhiteSpace(line.contact) ? "민재" : line.contact;
                string text = ExpandText(line.text);
                notifications?.SendNotification(new NotificationData
                {
                    title = contact,
                    message = text,
                    appType = AppType.Message,
                    speakerType = SpeakerType.Friend
                });
            }
            PlaySfx(popupClip, 0.22f);
            AddInt("unread_count", 1);
            Save();
            break;
        }
    }

    private void ShowNovelLine(ScenarioV3Line line)
    {
        if (novelPanel == null)
            return;

        notifications?.HidePopup();
        novelPanel.SetActive(true);
        novelPanel.transform.SetAsLastSibling();
        chapterText.text = $"{flow.CurrentDay:00}일차  |  {flow.V3ClockText}";
        string displaySpeaker = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ContactName(line.speaker);
        speakerText.text = displaySpeaker;
        string expandedText = ExpandText(line.text);
        currentDialoguePages = PaginateDialogue(expandedText);
        currentDialoguePageIndex = 0;
        currentFullText = currentDialoguePages[0];
        AppendDialogueLog(string.IsNullOrWhiteSpace(displaySpeaker) ? "나" : displaySpeaker, expandedText);
        ApplyArcVisual(activeScene.arc, line.delivery);
        ApplyCharacterPortrait(line);

        List<ScenarioV3Choice> choices = line.Choices.ToList();
        continueButton.gameObject.SetActive(false);
        ConfigureChoiceButton(choiceAButton, choices.Count > 0 ? choices[0] : null);
        ConfigureChoiceButton(choiceBButton, choices.Count > 1 ? choices[1] : null);
        ConfigureChoiceButton(choiceCButton, choices.Count > 2 ? choices[2] : null);
        SetChoiceButtonsVisible(false);
        continueButton.onClick.RemoveAllListeners();
        continueButton.onClick.AddListener(() =>
        {
            if (isTyping)
                CompleteTypewriter(IsLastDialoguePage && choices.Count > 0);
            else if (!IsLastDialoguePage)
                ShowNextDialoguePage(choices.Count > 0);
            else
                FinishLine(line);
        });
        if (typewriterCoroutine != null)
            StopCoroutine(typewriterCoroutine);
        typewriterCoroutine = StartCoroutine(TypeLine(IsLastDialoguePage && choices.Count > 0));
    }

    private bool IsLastDialoguePage => currentDialoguePageIndex >= currentDialoguePages.Count - 1;

    private void ShowNextDialoguePage(bool hasChoices)
    {
        currentDialoguePageIndex++;
        currentFullText = currentDialoguePages[currentDialoguePageIndex];
        SetChoiceButtonsVisible(false);
        if (typewriterCoroutine != null)
            StopCoroutine(typewriterCoroutine);
        typewriterCoroutine = StartCoroutine(TypeLine(IsLastDialoguePage && hasChoices));
    }

    private static List<string> PaginateDialogue(string text)
    {
        const int pageLimit = 72;
        var pages = new List<string>();
        string normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length <= pageLimit)
        {
            pages.Add(normalized);
            return pages;
        }

        string current = string.Empty;
        foreach (Match match in Regex.Matches(normalized, @"[^.!?。！？]+[.!?。！？]*"))
        {
            string sentence = match.Value.Trim();
            if (sentence.Length == 0)
                continue;

            if (current.Length > 0 && current.Length + 1 + sentence.Length > pageLimit)
            {
                pages.Add(current);
                current = string.Empty;
            }

            while (sentence.Length > pageLimit)
            {
                int split = sentence.LastIndexOf(' ', pageLimit);
                if (split < pageLimit / 2)
                    split = pageLimit;
                string part = sentence.Substring(0, split).Trim();
                if (current.Length > 0)
                {
                    pages.Add(current);
                    current = string.Empty;
                }
                pages.Add(part);
                sentence = sentence.Substring(split).Trim();
            }

            if (sentence.Length > 0)
                current = current.Length == 0 ? sentence : $"{current} {sentence}";
        }

        if (current.Length > 0)
            pages.Add(current);
        if (pages.Count == 0)
            pages.Add(normalized);
        return pages;
    }

    private IEnumerator TypeLine(bool hasChoices)
    {
        isTyping = true;
        bodyText.text = currentFullText;
        bodyText.maxVisibleCharacters = 0;
        continueButton.gameObject.SetActive(true);
        yield return null;

        int total = bodyText.textInfo.characterCount;
        for (int visible = 1; visible <= total; visible++)
        {
            bodyText.maxVisibleCharacters = visible;
            char character = visible - 1 < currentFullText.Length ? currentFullText[visible - 1] : ' ';
            if (!char.IsWhiteSpace(character) && !char.IsPunctuation(character) && visible % 2 == 0)
                PlayTypingSfx();
            yield return new WaitForSecondsRealtime(char.IsWhiteSpace(character) ? 0.006f : 0.026f);
        }

        CompleteTypewriter(hasChoices);
    }

    private void CompleteTypewriter(bool hasChoices)
    {
        if (typewriterCoroutine != null)
        {
            StopCoroutine(typewriterCoroutine);
            typewriterCoroutine = null;
        }
        isTyping = false;
        bodyText.text = currentFullText;
        bodyText.maxVisibleCharacters = int.MaxValue;
        continueButton.gameObject.SetActive(!hasChoices);
        SetChoiceButtonsVisible(hasChoices);
    }

    private void SetChoiceButtonsVisible(bool visible)
    {
        foreach (Button button in new[] { choiceAButton, choiceBButton, choiceCButton })
            if (button != null && button.GetComponentInChildren<TMP_Text>().text.Length > 0)
                button.gameObject.SetActive(visible);
    }

    private void ConfigureChoiceButton(Button button, ScenarioV3Choice choice)
    {
        button.gameObject.SetActive(choice != null);
        if (choice == null)
        {
            button.GetComponentInChildren<TMP_Text>().text = string.Empty;
            return;
        }
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
        if (!database.ShouldReturnToTablet(activeScene?.id) && !string.IsNullOrWhiteSpace(next))
        {
            if (TryReturnHomeBeforeNextScene(next, () => PlayScene(next)))
                return;
            activeScene = null;
            PlayScene(next);
            return;
        }
        PresentLine();
    }

    private void FinishScene()
    {
        bool returnToTablet = activeScene != null && database.ShouldReturnToTablet(activeScene.id);
        bool returnHome = IsActivityArc(activeScene?.arc) && flow.CurrentLocation != "집";
        activeScene = null;
        activeLineIndex = 0;
        HideNovel();
        Save();
        if (pendingDayAdvance)
        {
            pendingDayAdvance = false;
            FinalizeCurrentDayStatus();
            QueueTrigger("day_end", AdvanceToNextDay);
            StartQueuedScene();
            return;
        }
        if (returnHome)
        {
            flow.V3PromptReturnHomeAfterActivity(ContinueAfterActivityReturn);
            return;
        }
        if (TryResolveMissedJob())
            return;
        if (returnToTablet)
        {
            sceneQueue.Clear();
            queueCompleted = null;
            waitingForMessageChoice = false;
            waitingMessageSpeaker = SpeakerType.Unknown;
            appWindow?.CloseCurrentApp();
            if (!TryQueueEveningFill())
                TryQueueBedtimeCue();
            return;
        }
        if (TryQueueEveningFill())
            return;
        if (TryQueueBedtimeCue())
            return;
        StartQueuedScene();
    }

    private void ContinueAfterActivityReturn()
    {
        if (TryQueueEveningFill())
            return;
        if (TryQueueBedtimeCue())
            return;
        StartQueuedScene();
    }

    private bool TryQueueEveningFill()
    {
        if (flow.CurrentDay >= FinalDay || flow.CurrentLocation != "집" || flow.IsSleepHour ||
            !flow.IsDailyScheduleComplete || GetState("evening_filled") == "1" ||
            activeScene != null || sceneQueue.Count > 0)
            return false;

        SetState("evening_filled", "1");
        int queued = QueueTrigger("evening_fill", null);
        if (queued == 0)
        {
            flow.V3SetClock("21:00");
            Save();
            return false;
        }

        Save();
        StartQueuedScene();
        return true;
    }

    private bool TryResolveMissedJob()
    {
        if (!flow.IsWeekend || flow.CurrentHour <= 8 || GetState("schedule.job") != "pending" ||
            activeScene != null || sceneQueue.Count > 0)
            return false;

        SetState("schedule.job", "missed");
        appWindow?.CloseCurrentApp();
        int queued = QueueTrigger("job_missed", null);
        Save();
        if (queued > 0)
        {
            StartQueuedScene();
            return true;
        }

        return TryQueueEveningFill() || TryQueueBedtimeCue();
    }

    private bool TryQueueBedtimeCue()
    {
        if (flow.CurrentDay >= FinalDay || flow.CurrentLocation != "집" || !flow.IsSleepHour ||
            !flow.IsDailyScheduleComplete || GetState("bedtime_cued") == "1" ||
            HasPendingMessageAction || appWindow?.CurrentAppType == AppType.Message ||
            activeScene != null || sceneQueue.Count > 0)
            return false;

        SetState("bedtime_cued", "1");
        int queued = QueueTrigger("bedtime_cue", null);
        Save();
        if (queued == 0)
        {
            flow.V3ShowDialogue("나", "오늘은 이만 자자. 취침 앱을 열어서 하루를 마무리하자.",
                () => flow.V3MarkAppAttention(AppType.Sleep));
            return true;
        }

        StartQueuedScene();
        return true;
    }

    private bool TryReturnHomeBeforeNextScene(string nextSceneId, Action completed)
    {
        if (flow.CurrentLocation == "집" || !IsActivityArc(activeScene?.arc))
            return false;

        ScenarioV3Scene nextScene = database.GetScene(nextSceneId);
        if (nextScene != null && string.Equals(nextScene.arc, activeScene.arc, StringComparison.OrdinalIgnoreCase))
            return false;

        activeScene = null;
        HideNovel();
        flow.V3PromptReturnHomeAfterActivity(completed);
        return true;
    }

    private static bool IsActivityArc(string arc)
    {
        return string.Equals(arc, "school", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arc, "job", StringComparison.OrdinalIgnoreCase);
    }

    private void AdvanceToNextDay()
    {
        FinalizeCurrentDayStatus();

        if (QueueTrigger("collapse_check", null) > 0)
        {
            StartQueuedScene();
            return;
        }

        if (flow.CurrentDay >= FinalDay)
            return;

        flow.V3BeginNextDay();
        dialogueLog.Clear();
        state["schedule.school"] = "pending";
        state["schedule.homework"] = "pending";
        state["schedule.job"] = "pending";
        state["schedule.sleep"] = "pending";
        state["evening_filled"] = "0";
        state["bedtime_cued"] = "0";
        state["day_finalized"] = "0";
        state["day_cash_start"] = flow.V3BankCash.ToString(CultureInfo.InvariantCulture);
        Save();

        QueueTrigger("day_start", null);
        StartQueuedScene();
    }

    private void FinalizeCurrentDayStatus()
    {
        if (GetState("day_finalized") == "1")
            return;

        int previousCash = GetInt("day_cash_start", 50000);
        state["cash_delta_today"] = (flow.V3BankCash - previousCash).ToString(CultureInfo.InvariantCulture);
        state["previous.homework_status"] = GetState("schedule.homework");
        bool weekendDay = flow.IsWeekend;
        if (!weekendDay && GetState("schedule.school") == "pending")
        {
            SetState("schedule.school", "missed");
            AddInt("counter.school_absences", 1);
        }
        if (!weekendDay && flow.V3HasStudyToday && GetState("schedule.homework") == "pending")
        {
            SetState("schedule.homework", "missed");
            AddInt("counter.homework_failures", 1);
        }
        if (weekendDay && GetState("schedule.job") == "pending")
        {
            SetState("schedule.job", "missed");
            AddInt("counter.job_failures", 1);
        }

        bool requiredDone = weekendDay
            ? GetState("schedule.job") == "complete"
            : GetState("schedule.school") == "complete" &&
              (!flow.V3HasStudyToday || GetState("schedule.homework") == "complete");
        if (!requiredDone && flow.CurrentDay < FinalDay)
            AddInt("schedule_failures", 1);
        state["day_finalized"] = "1";
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
        if (key.Equals("gamble", StringComparison.OrdinalIgnoreCase) &&
            operation.Equals("advance", StringComparison.OrdinalIgnoreCase))
        {
            int session = GetInt("counter.gamble_sessions") + 1;
            if (session >= 6 && flow.V3BankCash <= 0)
            {
                AddInt("counter.no_funds_attempts", 1);
                immediateRoute = "gamble_no_funds";
                return;
            }
            SetState("counter.gamble_sessions", session.ToString(CultureInfo.InvariantCulture));
            SetState("flag.gambling_started", "true");
            immediateRoute = "gamble_" + Mathf.Min(session, 6).ToString(CultureInfo.InvariantCulture);
            return;
        }
        if (key.Equals("tutorial", StringComparison.OrdinalIgnoreCase) && operation.StartsWith("set="))
        {
            string target = operation.Substring(4).Trim().ToLowerInvariant();
            if (target == "map")
                flow.V3ShowTutorialHint(AppType.Map, "지도 앱에서 학교를 찍고 출발하면 되겠네.");
            else if (target == "message")
                flow.V3ShowTutorialHint(AppType.Message, "메시지 앱에서 민재의 연락을 확인해 보자.");
            else if (target == "study")
                flow.V3ShowTutorialHint(AppType.Study, "공부 앱에서 서연과의 조별과제를 진행해 보자.");
            else if (target == "sleep")
                flow.V3ShowTutorialHint(AppType.Sleep, "취침 앱을 열어 오늘 하루를 마무리하자.");
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
            else if (operation.StartsWith("add="))
            {
                int minutes = ResolveInt(operation.Substring(4));
                int startHour = flow.CurrentHour;
                flow.V3AddMinutes(minutes);
                if (activeScene != null && activeScene.arc == "gambling")
                {
                    int elapsedHours = Mathf.Max(0, Mathf.CeilToInt(minutes / 60f));
                    for (int step = 0; step <= elapsedHours; step++)
                    {
                        int hour = (startHour + step) % 24;
                        if (hour >= 22 || hour < 7)
                        {
                            SetState("flag.gambled_late", "true");
                            break;
                        }
                    }
                }
            }
            else if (operation.StartsWith("advance_to_next_day=")) pendingDayAdvance = true;
            return;
        }
        if (key.Equals("cash", StringComparison.OrdinalIgnoreCase))
        {
            int before = flow.V3BankCash;
            int amount = ResolveInt(operation.Substring(4));
            string description = GetCashTransactionDescription(amount);
            if (operation.StartsWith("set=")) flow.V3SetCash(amount, description);
            else if (operation.StartsWith("add=")) flow.V3AddCash(amount, description);
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
            "recovery" => "회복을 시작한 날",
            "prevented" => "일상을 선택한 시간",
            "no_help" => "말하지 못한 문제",
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
        if (TryBindPlacedNovelUI())
        {
            ConfigureNovelLayout();
            return;
        }

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

        characterPortrait = new GameObject("Character Portrait", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage))
            .GetComponent<RawImage>();
        characterPortrait.transform.SetParent(novelPanel.transform, false);
        characterPortrait.raycastTarget = false;
        characterPortrait.color = Color.white;
        RectTransform portraitRect = characterPortrait.rectTransform;
        portraitRect.anchorMin = portraitRect.anchorMax = new Vector2(0.27f, 0.5f);
        portraitRect.pivot = new Vector2(0.5f, 0.5f);
        portraitRect.anchoredPosition = new Vector2(0f, 30f);
        portraitRect.sizeDelta = new Vector2(700f, 940f);
        characterPortrait.gameObject.SetActive(false);
        GameObject clockPanel = Panel("Digital Clock", novelPanel.transform, new Color(0.015f, 0.035f, 0.055f, 0.94f));
        SetRect(clockPanel.GetComponent<RectTransform>(), new Vector2(42f, -32f), new Vector2(350f, 70f));
        Outline clockOutline = clockPanel.AddComponent<Outline>();
        clockOutline.effectColor = new Color(0.2f, 0.68f, 0.86f, 0.8f);
        clockOutline.effectDistance = new Vector2(2f, -2f);
        chapterText = Text("Chapter", clockPanel.transform, font, 25, FontStyles.Bold,
            new Color(0.58f, 0.9f, 1f));
        chapterText.alignment = TextAlignmentOptions.Center;
        Stretch(chapterText.rectTransform);
        Button historyButton = Button("History Button", novelPanel.transform, font, "대화 기록", new Color(0.08f, 0.12f, 0.18f, 0.88f));
        RectTransform historyButtonRect = historyButton.GetComponent<RectTransform>();
        historyButtonRect.anchorMin = historyButtonRect.anchorMax = new Vector2(1f, 1f);
        historyButtonRect.pivot = new Vector2(1f, 1f);
        historyButtonRect.anchoredPosition = new Vector2(-42f, -32f);
        historyButtonRect.sizeDelta = new Vector2(190f, 54f);
        historyButton.onClick.AddListener(ShowHistory);

        GameObject dialogueBox = Panel("Dialogue Box", novelPanel.transform, new Color(0.025f, 0.045f, 0.075f, 0.96f));
        RectTransform boxRect = dialogueBox.GetComponent<RectTransform>();
        boxRect.anchorMin = new Vector2(0.06f, 0.06f);
        boxRect.anchorMax = new Vector2(0.94f, 0.39f);
        boxRect.offsetMin = boxRect.offsetMax = Vector2.zero;
        speakerText = Text("Speaker", dialogueBox.transform, font, 27, FontStyles.Bold, new Color(0.44f, 0.72f, 1f));
        SetRect(speakerText.rectTransform, new Vector2(34f, -22f), new Vector2(420f, 42f));
        bodyText = Text("Body", dialogueBox.transform, font, 29, FontStyles.Bold, Color.white);
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
        choiceBButton = Button("Choice B", dialogueBox.transform, font, "선택지 B", new Color(0.47f, 0.25f, 0.31f));
        PlaceBottomButton(choiceAButton, 0.18f, 0.3f);
        PlaceBottomButton(choiceBButton, 0.5f, 0.3f);
        choiceCButton = Button("Choice C", dialogueBox.transform, font, "선택지 C", new Color(0.34f, 0.31f, 0.5f));
        PlaceBottomButton(choiceCButton, 0.82f, 0.3f);

        historyPanel = Panel("Dialogue History", novelPanel.transform, new Color(0.025f, 0.04f, 0.065f, 0.99f));
        Stretch(historyPanel.GetComponent<RectTransform>());
        TMP_Text historyTitle = Text("History Title", historyPanel.transform, font, 32, FontStyles.Bold, Color.white);
        historyTitle.text = "지나간 대화";
        SetRect(historyTitle.rectTransform, new Vector2(70f, -54f), new Vector2(500f, 54f));
        Button closeHistory = Button("Close History", historyPanel.transform, font, "닫기", new Color(0.17f, 0.46f, 0.78f));
        RectTransform closeRect = closeHistory.GetComponent<RectTransform>();
        closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
        closeRect.pivot = new Vector2(1f, 1f);
        closeRect.anchoredPosition = new Vector2(-70f, -45f);
        closeRect.sizeDelta = new Vector2(150f, 58f);
        closeHistory.onClick.AddListener(() => historyPanel.SetActive(false));

        GameObject viewport = Panel("History Viewport", historyPanel.transform, new Color(0.04f, 0.065f, 0.1f, 1f));
        RectTransform viewportRect = viewport.GetComponent<RectTransform>();
        viewportRect.anchorMin = new Vector2(0.06f, 0.08f);
        viewportRect.anchorMax = new Vector2(0.94f, 0.86f);
        viewportRect.offsetMin = viewportRect.offsetMax = Vector2.zero;
        viewport.AddComponent<RectMask2D>();
        GameObject content = new GameObject("History Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;
        VerticalLayoutGroup historyLayout = content.AddComponent<VerticalLayoutGroup>();
        historyLayout.padding = new RectOffset(28, 28, 24, 24);
        historyLayout.childForceExpandHeight = false;
        historyLayout.childForceExpandWidth = true;
        ContentSizeFitter contentFitter = content.AddComponent<ContentSizeFitter>();
        contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        historyText = Text("History Text", content.transform, font, 25, FontStyles.Bold, new Color(0.9f, 0.93f, 0.98f));
        historyText.alignment = TextAlignmentOptions.TopLeft;
        historyText.rectTransform.anchorMin = new Vector2(0f, 1f);
        historyText.rectTransform.anchorMax = new Vector2(1f, 1f);
        historyText.rectTransform.pivot = new Vector2(0.5f, 1f);
        historyText.rectTransform.anchoredPosition = new Vector2(0f, -24f);
        historyText.rectTransform.sizeDelta = new Vector2(-56f, 0f);
        ContentSizeFitter fitter = historyText.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        ScrollRect scroll = viewport.AddComponent<ScrollRect>();
        scroll.viewport = viewportRect;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        historyPanel.SetActive(false);
        novelPanel.SetActive(false);
        ConfigureNovelLayout();
    }

    private void ConfigureNovelLayout()
    {
        if (characterPortrait != null)
        {
            RectTransform portraitRect = characterPortrait.rectTransform;
            portraitRect.anchorMin = portraitRect.anchorMax = new Vector2(0.27f, 0.5f);
            portraitRect.pivot = new Vector2(0.5f, 0.5f);
            portraitRect.anchoredPosition = new Vector2(0f, 30f);
        }

        if (bodyText != null)
        {
            bodyText.fontStyle |= FontStyles.Bold;
            bodyText.enableAutoSizing = false;
            bodyText.fontSize = 34f;
        }
        if (speakerText != null)
            speakerText.fontStyle |= FontStyles.Bold;
        if (historyPanel != null)
            Stretch(historyPanel.GetComponent<RectTransform>());
        if (historyText == null)
            return;

        historyText.fontStyle |= FontStyles.Bold;
        historyText.enableAutoSizing = false;
        historyText.fontSize = 27f;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0.5f, 1f);
        textRect.anchorMax = new Vector2(0.5f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = new Vector2(0f, -24f);
        ContentSizeFitter textFitter = historyText.GetComponent<ContentSizeFitter>();
        if (textFitter != null)
            textFitter.enabled = false;
        RectTransform contentRect = historyText.transform.parent as RectTransform;
        if (contentRect != null)
        {
            VerticalLayoutGroup layout = contentRect.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
                layout.enabled = false;
            ContentSizeFitter contentFitter = contentRect.GetComponent<ContentSizeFitter>();
            if (contentFitter != null)
                contentFitter.enabled = false;
            contentRect.anchorMin = new Vector2(0.5f, 1f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            RectTransform viewportRect = contentRect.parent as RectTransform;
            if (viewportRect != null)
            {
                viewportRect.anchorMin = new Vector2(0.06f, 0.08f);
                viewportRect.anchorMax = new Vector2(0.94f, 0.86f);
                viewportRect.offsetMin = viewportRect.offsetMax = Vector2.zero;
            }
        }
    }

    private bool TryBindPlacedNovelUI()
    {
        novelPanel = FindSceneObject("Scenario V3 Novel");
        if (novelPanel == null)
            return false;

        novelBackground = FindSceneObject("Illustrated Background")?.GetComponent<RawImage>();
        characterPortrait = FindSceneObject("Character Portrait")?.GetComponent<RawImage>();
        chapterText = FindSceneObject("Chapter")?.GetComponent<TMP_Text>();
        speakerText = FindSceneObject("Speaker")?.GetComponent<TMP_Text>();
        bodyText = FindSceneObject("Body")?.GetComponent<TMP_Text>();
        continueButton = FindSceneObject("Continue")?.GetComponent<Button>();
        choiceAButton = FindSceneObject("Choice A")?.GetComponent<Button>();
        choiceBButton = FindSceneObject("Choice B")?.GetComponent<Button>();
        choiceCButton = FindSceneObject("Choice C")?.GetComponent<Button>();
        historyPanel = FindSceneObject("Dialogue History");
        historyText = FindSceneObject("History Text")?.GetComponent<TMP_Text>();

        Button historyButton = FindSceneObject("History Button")?.GetComponent<Button>();
        if (historyButton != null)
        {
            historyButton.onClick.RemoveAllListeners();
            historyButton.onClick.AddListener(ShowHistory);
        }
        Button closeHistory = FindSceneObject("Close History")?.GetComponent<Button>();
        if (closeHistory != null)
        {
            closeHistory.onClick.RemoveAllListeners();
            closeHistory.onClick.AddListener(() => historyPanel?.SetActive(false));
        }

        historyPanel?.SetActive(false);
        novelPanel.SetActive(false);
        return novelBackground != null && chapterText != null && bodyText != null;
    }

    private void CreateAudio()
    {
        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        sfxSource.volume = 0.5f;
        typingSource = gameObject.AddComponent<AudioSource>();
        typingSource.playOnAwake = false;
        typingSource.volume = 0.12f;
        popupClip = Resources.Load<AudioClip>("Audio/SFX/popup");
        buttonClip = Resources.Load<AudioClip>("Audio/SFX/button_click");
        typingClip = Resources.Load<AudioClip>("Audio/SFX/dialogue_type");
    }

    private string GetCashTransactionDescription(int amount)
    {
        string sceneId = activeScene?.id ?? string.Empty;
        if (sceneId == "d1_intro")
            return "생활비";
        if (sceneId.StartsWith("gamble_", StringComparison.OrdinalIgnoreCase))
            return amount > 0 ? "포인트 환전" : "온라인 결제";
        if (sceneId == "borrow_choice")
        {
            string owner = GetState("debt_owner");
            if (owner == "mom")
                return "엄마 송금";
            if (owner == "seojun")
                return "서준 송금";
            if (owner == "minjae")
                return "민재 송금";
            return "빌린 돈 입금";
        }
        return amount >= 0 ? "입금" : "결제";
    }

    private void ApplyArcVisual(string arc, string delivery)
    {
        bool isNightAtHome = flow != null && flow.CurrentLocation == "집" &&
                             (flow.CurrentHour >= 19 || flow.CurrentHour < 6);
        string resource = arc switch
        {
            "school" or "homework" => "ScenarioArt/classroom",
            "job" => "ScenarioArt/cafe",
            "gambling" or "debt" => "ScenarioArt/temptation",
            "sleep" => "ScenarioArt/bedroom_night",
            _ => isNightAtHome ? "ScenarioArt/bedroom_night" : "ScenarioArt/bedroom"
        };
        novelBackground.texture = Resources.Load<Texture2D>(resource);
        novelBackground.color = novelBackground.texture == null ? ArcColor(arc) : Color.white;
    }

    private void ApplyCharacterPortrait(ScenarioV3Line line)
    {
        if (characterPortrait == null)
            return;

        if (line == null || string.IsNullOrWhiteSpace(line.portrait))
        {
            characterPortrait.gameObject.SetActive(false);
            return;
        }

        Texture2D portrait = Resources.Load<Texture2D>($"Characters/{line.portrait}");
        if (portrait == null)
        {
            Debug.LogWarning($"[Scenario V3] 캐릭터 일러스트를 찾을 수 없습니다: {line.portrait}");
            characterPortrait.gameObject.SetActive(false);
            return;
        }

        characterPortrait.texture = portrait;
        float aspect = portrait.height > 0 ? portrait.width / (float)portrait.height : 0.75f;
        characterPortrait.rectTransform.sizeDelta = new Vector2(940f * aspect, 940f);
        characterPortrait.gameObject.SetActive(true);
        characterPortrait.transform.SetSiblingIndex(Mathf.Max(2, characterPortrait.transform.GetSiblingIndex()));
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

    private void PlayTypingSfx()
    {
        if (typingClip == null || typingSource == null || typingSource.isPlaying)
            return;

        typingSource.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
        typingSource.PlayOneShot(typingClip, 0.12f);
    }

    private void HideNovel()
    {
        if (typewriterCoroutine != null)
        {
            StopCoroutine(typewriterCoroutine);
            typewriterCoroutine = null;
        }
        isTyping = false;
        if (historyPanel != null)
            historyPanel.SetActive(false);
        if (novelPanel != null)
            novelPanel.SetActive(false);
    }

    private void AppendDialogueLog(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        dialogueLog.Add($"{speaker}\n{text}");
        if (dialogueLog.Count > 120)
            dialogueLog.RemoveAt(0);
    }

    private void ShowHistory()
    {
        if (historyPanel == null || historyText == null)
            return;
        historyText.text = dialogueLog.Count == 0
            ? "아직 기록된 대화가 없습니다."
            : string.Join("\n\n", dialogueLog);
        historyPanel.SetActive(true);
        historyPanel.transform.SetAsLastSibling();
        RefreshHistoryLayout();
    }

    private void RefreshHistoryLayout()
    {
        RectTransform textRect = historyText?.rectTransform;
        RectTransform contentRect = textRect?.parent as RectTransform;
        RectTransform viewportRect = contentRect?.parent as RectTransform;
        if (textRect == null || contentRect == null || viewportRect == null)
            return;

        Canvas.ForceUpdateCanvases();
        float viewportWidth = Mathf.Max(480f, viewportRect.rect.width);
        float viewportHeight = Mathf.Max(320f, viewportRect.rect.height);
        float textWidth = viewportWidth - 56f;
        float textHeight = Mathf.Max(80f, historyText.GetPreferredValues(historyText.text, textWidth, 0f).y + 8f);

        contentRect.sizeDelta = new Vector2(viewportWidth, Mathf.Max(viewportHeight, textHeight + 48f));
        contentRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = new Vector2(textWidth, textHeight);
        textRect.anchoredPosition = new Vector2(0f, -24f);
        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
    }

    private void Save()
    {
        var data = new ScenarioV3SaveData
        {
            state = state.OrderBy(pair => pair.Key)
                .Select(pair => new ScenarioV3StateEntry { key = pair.Key, value = pair.Value }).ToList(),
            choices = new List<ScenarioV3ChoiceRecord>(choiceHistory),
            seenScenes = seenScenes.OrderBy(value => value).ToList(),
            checkpoints = checkpoints,
            dialogueLog = new List<string>(dialogueLog)
        };
        File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
    }

    private void CaptureCheckpointIfNeeded(ScenarioV3Line line)
    {
        if (!database.TryGetCheckpointLabel(line, out string label))
            return;
        if (checkpoints.Any(checkpoint => checkpoint.day == flow.CurrentDay && checkpoint.lineId == line.id))
            return;

        checkpoints.Add(new ScenarioV3CheckpointData
        {
            label = label,
            sceneId = activeScene.id,
            lineId = line.id,
            lineIndex = activeLineIndex,
            day = flow.CurrentDay,
            hour = flow.CurrentHour,
            location = flow.CurrentLocation,
            cash = flow.V3BankCash,
            debt = flow.CurrentDebt,
            choiceCount = choiceHistory.Count,
            state = state.OrderBy(pair => pair.Key)
                .Select(pair => new ScenarioV3StateEntry { key = pair.Key, value = pair.Value }).ToList(),
            seenScenes = seenScenes.OrderBy(value => value).ToList()
        });
        Save();
    }

    private ScenarioV3CheckpointData FindRewindCheckpoint()
    {
        ScenarioV3CheckpointData previousDay = checkpoints.LastOrDefault(checkpoint => checkpoint.day < flow.CurrentDay);
        return previousDay ?? checkpoints.LastOrDefault();
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

    private static SpeakerType MapContact(string contact)
    {
        return (contact ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "민재" => SpeakerType.Friend,
            "minjae" => SpeakerType.Friend,
            "엄마" => SpeakerType.Mom,
            "mom" => SpeakerType.Mom,
            "서연" => SpeakerType.Seoyeon,
            "seoyeon" => SpeakerType.Seoyeon,
            "서준" => SpeakerType.Joonho,
            "seojun" => SpeakerType.Joonho,
            "담임 선생님" => SpeakerType.Teacher,
            "teacher" => SpeakerType.Teacher,
            "카페 매니저" => SpeakerType.CafeManager,
            "cafemanager" => SpeakerType.CafeManager,
            _ => SpeakerType.Unknown
        };
    }

    private static GameObject FindSceneObject(string objectName)
    {
        foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (candidate.scene.IsValid() && candidate.name == objectName)
                return candidate;
        }
        return null;
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
            "protagonist" => "나",
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
        text.fontStyle = style | FontStyles.Bold;
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
