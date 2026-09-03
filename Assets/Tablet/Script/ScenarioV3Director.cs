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
    private readonly HashSet<string> deliveredIncomingLineIds =
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
    private ScenarioV3Scene waitingMessageScene;
    private int waitingMessageLineIndex = -1;
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
    private bool sceneTransitionInProgress;
    private bool waitingForIncomingMessageRead;
    private SpeakerType waitingIncomingSpeaker = SpeakerType.Unknown;
    private ScenarioV3Line waitingIncomingLine;
    private Coroutine incomingMessageCoroutine;
    private Coroutine unreadAttentionSyncCoroutine;
    private bool waitingForMessageSceneClose;
    private Action pendingAfterMessageClose;
    private bool pendingLateWakeAfterGambling;
    private bool pendingBorrowMorningAdvance;
    private bool activeSceneTransitionHandled;
    private int lastHomeVisualPeriod = -1;
    private bool bypassHomeTimeTransition;
    private Coroutine homeTimeTransitionCoroutine;
    private RawImage homeTransitionOverlay;

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
    private RectTransform historyViewportRect;
    private RectTransform historyContentRect;
    private ScrollRect historyScroll;
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
            ? GetAvailableChoices(activeScene.lines[activeLineIndex])
            : Array.Empty<ScenarioV3Choice>();
    public bool CanRewind => checkpoints.Count > 0;
    public string RewindLabel => FindRewindCheckpoint()?.label ?? string.Empty;
    public bool HasPendingMessageAction => pendingOutgoingLine != null || waitingForMessageChoice || GetInt("unread_count") > 0;
    public bool HasUnreadMessageAttention => GetInt("unread_count") > 0;
    public bool HasPendingGambleOffer => GetState("pending.gamble_attention") == "true";
    public bool IsGamblingAppUnlocked => GetState("flag.gambling_app_unlocked") == "true";
    public bool HasCompletedInitialMessageIntro =>
        GetState("flag.d1_mom_message_read") == "true" &&
        GetState("flag.minjae_first_invite_read") == "true";

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

        bool scheduleAction = trigger == "homework_complete" || trigger == "school_complete" ||
                              trigger == "job_complete" || trigger == "school_missed" || trigger == "job_missed";
        if (scheduleAction)
        {
            // 메시지 답장을 미룬 채 실제 일정 행동을 선택했다면 그 답장이
            // 학교/알바/공부 장면을 막지 않게 한다. 메시지는 '안 보냄'으로 남긴다.
            if (pendingOutgoingLine != null)
            {
                dialogue?.DismissEventChoices(pendingOutgoingSpeaker);
                ClearPendingOutgoingMessage();
                activeScene = null;
                activeLineIndex = 0;
            }
            if (waitingForMessageChoice)
            {
                dialogue?.DismissEventChoices(waitingMessageSpeaker);
                if (waitingMessageSpeaker == SpeakerType.Friend)
                    AddInt("counter.minjae_ignored", 1);
                waitingForMessageChoice = false;
                waitingMessageSpeaker = SpeakerType.Unknown;
                waitingMessageScene = null;
                waitingMessageLineIndex = -1;
                activeScene = null;
                activeLineIndex = 0;
            }
        }

        if (trigger == "homework_complete" || trigger == "school_complete" || trigger == "job_complete")
            ResolvePendingGambleAttentionAsDeclined();

        if (trigger == "homework_complete")
            SetState("schedule.homework", "complete");
        else if (trigger == "school_complete")
            SetState("schedule.school", "complete");
        else if (trigger == "school_missed")
        {
            if (GetState("schedule.school") == "pending")
            {
                SetState("schedule.school", "missed");
                AddInt("counter.school_absences", 1);
            }
        }
        else if (trigger == "job_complete")
            SetState("schedule.job", "complete");
        else if (trigger == "job_missed")
            SetState("schedule.job", "missed");

        PlayTrigger(trigger);
        if (activeScene == null && sceneQueue.Count == 0)
        {
            if (!TryResolveMissedJob() && !TryQueueEveningFill())
                TryQueueBedtimeCue();
        }
        TrySendUnreadReminder();
        Save();
    }

    public void NotifyAppOpened(AppType? app)
    {
        if (app == null && waitingForMessageSceneClose && (dialogue == null || !dialogue.IsDialogueOpen))
        {
            ResumeAfterMessageClose();
            return;
        }

        if (app == AppType.Message)
        {
            flow.V3HideTutorialHint(AppType.Message);
            SetState("unread_count", dialogue != null
                ? dialogue.TotalUnreadCount.ToString(CultureInfo.InvariantCulture)
                : "0");
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

    public void NotifyConversationOpened(SpeakerType speaker)
    {
        if (!IsReady || dialogue == null)
            return;

        // 채팅방을 실제로 열어 읽는 순간 unread와 홈의 빨간 점을 즉시 동기화한다.
        // Unity UI/알림 오브젝트가 같은 프레임에 정리되는 경우가 있어 다음 프레임에도 한 번 더 맞춘다.
        SynchronizeMessageAttention();
        if (unreadAttentionSyncCoroutine != null)
            StopCoroutine(unreadAttentionSyncCoroutine);
        unreadAttentionSyncCoroutine = StartCoroutine(SynchronizeMessageAttentionNextFrame());

        if (speaker == SpeakerType.Mom && GetState("flag.d1_mom_message_available") == "true")
        {
            SetState("flag.d1_mom_message_read", "true");
        }
        else if (speaker == SpeakerType.Friend)
        {
            flow.V3HideTutorialHint(AppType.Message);
            if (GetState("flag.minjae_first_invite_available") == "true")
            {
                SetState("flag.minjae_first_invite_read", "true");
                if (!IsGamblingAppUnlocked)
                    ApplyEffect("gamble:unlock");
            }
        }

        // 알림만 본 상태에서는 시나리오 독백/다음 메시지로 진행하지 않는다.
        // 실제 해당 채팅방을 열었을 때 비로소 읽은 것으로 보고 진행한다.
        if (waitingForIncomingMessageRead && speaker == waitingIncomingSpeaker && waitingIncomingLine != null)
        {
            waitingForIncomingMessageRead = false;
            if (incomingMessageCoroutine != null)
                StopCoroutine(incomingMessageCoroutine);
            incomingMessageCoroutine = StartCoroutine(ResumeAfterIncomingMessageRead(waitingIncomingLine));
        }
        Save();
    }

    private void SynchronizeMessageAttention()
    {
        int unread = dialogue != null ? dialogue.TotalUnreadCount : 0;
        SetState("unread_count", unread.ToString(CultureInfo.InvariantCulture));
        if (unread <= 0)
            flow?.V3ClearAppAttention(AppType.Message);
        flow?.V3Refresh();
    }

    private IEnumerator SynchronizeMessageAttentionNextFrame()
    {
        yield return null;
        SynchronizeMessageAttention();
        unreadAttentionSyncCoroutine = null;
        Save();
    }

    public void NotifyConversationClosed(SpeakerType speaker)
    {
        if (!waitingForMessageSceneClose)
            return;
        ResumeAfterMessageClose();
    }

    private void ResumeAfterMessageClose()
    {
        waitingForMessageSceneClose = false;
        Action action = pendingAfterMessageClose;
        pendingAfterMessageClose = null;
        if (action != null)
            action();
        else
            FinishScene();
    }

    public void TryStartGambleFromHome()
    {
        if (!IsReady || flow.IsGameEnded || !IsGamblingAppUnlocked || activeScene != null ||
            waitingForMessageChoice || pendingOutgoingLine != null)
            return;

        if (!flow.IsDailyScheduleComplete)
        {
            flow.V3ShowDialogue("나", "(아직 오늘 해야 할 일이 남아 있다. 다른 일정부터 하자.)", null);
            return;
        }

        SetState("pending.gamble_attention", "false");
        flow.V3SetGamblingAttention(false);
        ApplyEffect("gamble:advance");
        string target = immediateRoute;
        immediateRoute = string.Empty;
        if (!string.IsNullOrWhiteSpace(target))
            PlayScene(target);
        Save();
    }

    public void ConfirmDeferredBorrowMessage(string target)
    {
        string normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized != "mom" && normalized != "seojun")
            return;
        if (!string.Equals(GetState("pending.borrow_target"), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        SpeakerType speaker = normalized == "mom" ? SpeakerType.Mom : SpeakerType.Joonho;
        dialogue?.DismissEventChoices(speaker);
        SetState("pending.borrow_target", "none");
        flow.V3ClearAppAttention(AppType.Message);
        Save();
        PlayScene(normalized == "mom" ? "mom_loan_response" : "seojun_loan_response");
    }

    public void CancelDeferredBorrowMessage(string target)
    {
        string normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
        SpeakerType speaker = normalized == "mom" ? SpeakerType.Mom : SpeakerType.Joonho;
        dialogue?.DismissEventChoices(speaker);
        if (string.Equals(GetState("pending.borrow_target"), normalized, StringComparison.OrdinalIgnoreCase))
            SetState("pending.borrow_target", "none");
        flow.V3ClearAppAttention(AppType.Message);
        flow.V3ShowDialogue("나", "(역시 지금은 부탁하지 말자. 다른 방법을 생각해 보자.)", null);
        Save();
    }

    private void PrepareDeferredBorrowRequest(string target)
    {
        string normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
        SpeakerType speaker;
        string contactName;
        string replyText;

        if (normalized == "mom")
        {
            speaker = SpeakerType.Mom;
            contactName = "엄마";
            replyText = "엄마. 교통카드 충전해야 하는데 5만 원만 보내주면 안 돼?";
        }
        else if (normalized == "seojun")
        {
            speaker = SpeakerType.Joonho;
            contactName = "서준";
            replyText = "서준아. 미안한데 지금 5만 원만 빌려줄 수 있어? 다음 주에 꼭 갚을게.";
        }
        else
        {
            return;
        }

        if (GetState("borrowed." + normalized) == "true")
            return;

        SetState("pending.borrow_target", normalized);
        dialogue?.EnsureContact(speaker, contactName);
        dialogue?.PreferConversation(speaker);
        dialogue?.SetEventChoices(speaker, new List<Choice>
        {
            new Choice
            {
                choiceText = "돈을 빌려 달라고 메시지 보낸다",
                replyText = replyText,
                nextDialogueID = -1,
                scenarioAction = "v3-borrow-send:" + normalized
            },
            new Choice
            {
                choiceText = "역시 보내지 않는다",
                replyText = string.Empty,
                nextDialogueID = -1,
                scenarioAction = "v3-borrow-cancel:" + normalized
            }
        });
        flow.V3MarkAppAttention(AppType.Message);
        Save();
    }

    public void HandleChoice(string choiceId)
    {
        if (activeScene == null && waitingForMessageChoice && waitingMessageScene != null)
        {
            activeScene = waitingMessageScene;
            activeLineIndex = waitingMessageLineIndex;
        }
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
        waitingMessageScene = null;
        waitingMessageLineIndex = -1;
        flow.V3Refresh();
        if (!string.Equals(line.delivery, "message", StringComparison.OrdinalIgnoreCase))
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
        Action continueChoice = () => ContinueAfterResolvedChoice(nextScene);
        if (string.Equals(line.delivery, "message", StringComparison.OrdinalIgnoreCase) &&
            dialogue != null && dialogue.IsDialogueOpen && !WillContinueInsideMessage(nextScene))
        {
            waitingForMessageSceneClose = true;
            pendingAfterMessageClose = continueChoice;
            return;
        }
        continueChoice();
    }

    private bool WillContinueInsideMessage(string nextScene)
    {
        string target = !string.IsNullOrWhiteSpace(immediateRoute) ? immediateRoute : nextScene;
        if (!string.IsNullOrWhiteSpace(target))
        {
            ScenarioV3Scene scene = database.GetScene(target);
            if (scene != null && scene.lines.Any(candidate =>
                    string.Equals(candidate.delivery, "message", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(reactiveTrigger))
        {
            return database.GetByTrigger(reactiveTrigger).Any(scene => scene.lines.Any(candidate =>
                string.Equals(candidate.delivery, "message", StringComparison.OrdinalIgnoreCase)));
        }
        return false;
    }

    private void ContinueAfterResolvedChoice(string nextScene)
    {
        if (!string.IsNullOrWhiteSpace(immediateRoute))
        {
            string routedScene = immediateRoute;
            immediateRoute = string.Empty;
            activeScene = null;
            PlayScene(routedScene);
            return;
        }
        if (pendingDayAdvance)
        {
            // 시나리오 대사/메시지가 날짜를 강제로 넘기지 않는다.
            // 날짜 변경은 취침 앱을 통해서만 처리한다.
            pendingDayAdvance = false;
            activeScene = null;
            HideNovel();
            flow.V3MarkAppAttention(AppType.Sleep);
            TryQueueBedtimeCue();
            return;
        }

        if (!string.IsNullOrWhiteSpace(reactiveTrigger))
        {
            activeScene = null;
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

        ResolvePendingGambleAttentionAsDeclined();

        if (waitingForMessageChoice)
        {
            dialogue?.DismissEventChoices(waitingMessageSpeaker);
            if (waitingMessageSpeaker == SpeakerType.Friend)
                AddInt("counter.minjae_ignored", 1);
            waitingForMessageChoice = false;
            waitingMessageSpeaker = SpeakerType.Unknown;
            waitingMessageScene = null;
            waitingMessageLineIndex = -1;
            activeScene = null;
            activeLineIndex = 0;
        }

        if (pendingOutgoingLine != null)
        {
            dialogue?.DismissEventChoices(pendingOutgoingSpeaker);
            ClearPendingOutgoingMessage();
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
        if (GetState("schedule.school") == "missed")
            QueueTrigger("school_missed", null);
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
        waitingForIncomingMessageRead = false;
        waitingIncomingSpeaker = SpeakerType.Unknown;
        waitingIncomingLine = null;
        if (incomingMessageCoroutine != null)
            StopCoroutine(incomingMessageCoroutine);
        incomingMessageCoroutine = null;
        waitingForMessageSceneClose = false;
        pendingAfterMessageClose = null;
        waitingForMessageChoice = false;
        waitingMessageScene = null;
        waitingMessageLineIndex = -1;
        ClearPendingOutgoingMessage();
        deliveredOutgoingLineIds.Clear();
        deliveredIncomingLineIds.Clear();
        pendingDayAdvance = false;
        // DOBak V13-D01: 되감기 뒤 런타임 전용 아침 전환 플래그가 미래 분기로 새지 않게 한다.
        pendingLateWakeAfterGambling = false;
        pendingBorrowMorningAdvance = false;
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
        flow.V3SetGamblingUnlocked(IsGamblingAppUnlocked);
        flow.V3SetGamblingAttention(HasPendingGambleOffer);

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
        sceneTransitionInProgress = false;
        activeSceneTransitionHandled = false;
        waitingForIncomingMessageRead = false;
        waitingIncomingSpeaker = SpeakerType.Unknown;
        waitingIncomingLine = null;
        if (incomingMessageCoroutine != null)
            StopCoroutine(incomingMessageCoroutine);
        incomingMessageCoroutine = null;
        waitingForMessageSceneClose = false;
        pendingAfterMessageClose = null;
        pendingLateWakeAfterGambling = false;
        pendingBorrowMorningAdvance = false;
        activeScene = null;
        activeLineIndex = 0;
        waitingForMessageChoice = false;
        waitingMessageSpeaker = SpeakerType.Unknown;
        waitingMessageScene = null;
        waitingMessageLineIndex = -1;
        ClearPendingOutgoingMessage();
        seenScenes.Clear();
        choiceHistory.Clear();
        checkpoints.Clear();
        dialogueLog.Clear();
        deliveredOutgoingLineIds.Clear();
        deliveredIncomingLineIds.Clear();
        lastHomeVisualPeriod = -1;
        if (homeTimeTransitionCoroutine != null)
        {
            StopCoroutine(homeTimeTransitionCoroutine);
            homeTimeTransitionCoroutine = null;
        }
        if (homeTransitionOverlay != null)
            homeTransitionOverlay.gameObject.SetActive(false);
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
        state["pending.gamble_attention"] = "false";
        state["pending.borrow_menu"] = "false";
        state["pending.borrow_target"] = "none";
        state["flag.late_wake_today"] = "false";
        state["flag.borrow_deferred"] = "false";
        // DOBak V13-D02: 새 게임은 이전 밤샘 상태를 절대 이어받지 않는다.
        state["flag.gambled_late"] = "false";
        state["borrowed.mom"] = "false";
        state["borrowed.seojun"] = "false";
        state["borrowed.minjae"] = "false";
        state["flag.gambling_app_unlocked"] = "false";
        state["flag.d1_mom_message_available"] = "false";
        state["flag.d1_mom_message_read"] = "false";
        state["flag.minjae_first_invite_read"] = "false";
        state["relation.seoyeon"] = "0";
        state["relation.manager"] = "0";
        flow.V3ResetRun(50000);
        ClearSavedRun();
        Save();
    }

    private void ContinueAfterChoice(string nextScene)
    {
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
        // DOBak V13-D03: 직접 라우팅된 장면도 현재 트리거 체인의 완료 콜백을 보존한다.
        // 새 게임/되감기/강제 날짜 전환처럼 체인을 폐기해야 하는 경로는 호출부에서 명시적으로 초기화한다.
        BeginScene(scene);
    }

    private void StartQueuedScene()
    {
        if (activeScene != null || waitingForMessageChoice || sceneTransitionInProgress)
            return;

        if (sceneQueue.Count > 0)
        {
            BeginScene(sceneQueue.Dequeue());
            return;
        }

        Action completed = queueCompleted;
        queueCompleted = null;
        if (flow.CurrentLocation == "학교" && flow.IsSchoolDone)
        {
            ShowReturnHomeMonologue("school", () =>
                flow.V3ReturnHomeAfterActivity(() =>
                {
                    completed?.Invoke();
                    ContinueAfterActivityReturn();
                }));
            return;
        }
        if (flow.CurrentLocation == "카페" && flow.IsJobDone)
        {
            ShowReturnHomeMonologue("job", () =>
                flow.V3ReturnHomeAfterActivity(() =>
                {
                    completed?.Invoke();
                    ContinueAfterActivityReturn();
                }));
            return;
        }
        HideNovel();
        completed?.Invoke();
    }

    private void BeginScene(ScenarioV3Scene scene)
    {
        sceneTransitionInProgress = false;
        activeSceneTransitionHandled = false;
        BeginSceneImmediate(scene);
    }

    private void BeginSceneImmediate(ScenarioV3Scene scene)
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
        if (pendingLateWakeAfterGambling &&
            string.Equals(activeScene.arc, "gambling", StringComparison.OrdinalIgnoreCase) &&
            GetAvailableChoices(line).Count > 0)
        {
            BeginForcedLateMorningAdvance();
            return;
        }
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
            // 실제 이동 직후 태블릿 오버레이가 이어지는 경우에는
            // 이동 연출을 여기서 소비해 이후 장면까지 남지 않게 한다.
            flow?.V3ConsumeLocationTransition();
            ShowTabletOverlayLine(line);
            return;
        }
        if (delivery == "message")
        {
            // 메시지 수신 자체에는 화면 전환 연출을 재생하지 않는다.
            flow?.V3ConsumeLocationTransition();
            PresentMessage(line);
            return;
        }

        // 메시지/오버레이/라우터에는 장면 페이드를 재생하지 않는다.
        // 실제 VN 화면이 나타나는 첫 순간에만 한 번 전환한다.
        if (!activeSceneTransitionHandled)
        {
            if (flow != null && flow.V3ConsumeLocationTransition())
            {
                activeSceneTransitionHandled = true;
            }
            else if (flow != null)
            {
                sceneTransitionInProgress = true;
                if (flow.V3TransitionScene(() =>
                    {
                        sceneTransitionInProgress = false;
                        activeSceneTransitionHandled = true;
                        ShowNovelLine(line);
                    }))
                    return;
                sceneTransitionInProgress = false;
                activeSceneTransitionHandled = true;
            }
            else
            {
                activeSceneTransitionHandled = true;
            }
        }

        ShowNovelLine(line);
    }

    private void ShowTabletOverlayLine(ScenarioV3Line line)
    {
        HideNovel();
        string title = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(line.speaker, "System", StringComparison.OrdinalIgnoreCase)
            ? "안내"
            : ContactName(line.speaker);
        string text = FormatProtagonistMonologue(line, ExpandText(line.text));
        if (!flow.V3ShowDialogue(title, text, () => FinishLine(line)))
            ShowNovelLine(line);
    }

    private void PresentMessage(ScenarioV3Line line)
    {
        HideNovel();
        bool sentByPlayer = string.Equals(line.speaker, "Protagonist", StringComparison.OrdinalIgnoreCase);
        SpeakerType speaker = sentByPlayer ? MapContact(line.contact) : MapSpeaker(line.speaker);
        string contact = string.IsNullOrWhiteSpace(line.contact)
            ? ContactName(line.speaker)
            : line.contact;
        string text = ExpandText(line.text);

        if (sentByPlayer)
        {
            dialogue?.EnsureConversation(speaker, contact);
            dialogue?.PreferConversation(speaker);
            dialogue?.SetEventChoices(speaker, new List<Choice>
            {
                new Choice
                {
                    choiceText = "메시지 보낸다.",
                    replyText = text,
                    nextDialogueID = -1,
                    scenarioAction = "v3-send-message:" + line.id
                }
            });
            flow.V3MarkAppAttention(AppType.Message);

            // 앱 밖에서 자동 전송하지 않는다. 해당 상대의 채팅방을 열고 전송 버튼을 눌러야 한다.
            pendingOutgoingLine = line;
            pendingOutgoingSpeaker = speaker;
            pendingOutgoingContact = contact;
            pendingOutgoingText = text;

            if (appWindow?.CurrentAppType != AppType.Message || dialogue == null || !dialogue.IsConversationOpen(speaker))
            {
                string prompt = $"({contact}에게 메시지를 보내야겠다.)";
                flow.V3ShowDialogue("나", prompt, null);
            }
            Save();
            return;
        }

        if (!deliveredIncomingLineIds.Add(line.id))
        {
            FinishLine(line);
            return;
        }

        List<ScenarioV3Choice> choices = GetAvailableChoices(line);
        bool conversationOpen = dialogue != null && dialogue.IsConversationOpen(speaker);
        bool speakerChanged = activeLineIndex == 0 ||
            !string.Equals(activeScene.lines[activeLineIndex - 1].speaker, line.speaker,
                StringComparison.OrdinalIgnoreCase);
        bool announce = line.sequence == 1 || speakerChanged;

        // 이미 채팅방을 보고 있다면 상대가 입력하는 시간을 보여 준 뒤 실제 말풍선을 만든다.
        if (conversationOpen)
        {
            if (incomingMessageCoroutine != null)
                StopCoroutine(incomingMessageCoroutine);
            incomingMessageCoroutine = StartCoroutine(
                DeliverIncomingMessageWithTyping(line, speaker, contact, text, choices));
            return;
        }

        var data = new NotificationData
        {
            title = contact,
            message = text,
            appType = AppType.Message,
            speakerType = speaker
        };

        if (announce && notifications != null)
            notifications.SendNotification(data);
        else
            dialogue?.ReceiveNotificationMessage(speaker, contact, text);

        if (announce)
        {
            PlaySfx(popupClip, 0.26f);
            SetState("unread_count", dialogue != null
                ? dialogue.TotalUnreadCount.ToString(CultureInfo.InvariantCulture)
                : (GetInt("unread_count") + 1).ToString(CultureInfo.InvariantCulture));
            flow.V3Refresh();

            // 첫 민재 권유는 알림만 보고 내용에 반응하지 않는다.
            // 대신 메시지 앱을 직접 확인하도록 짧게 유도한다.
            if (speaker == SpeakerType.Friend &&
                string.Equals(line.id, "d1_minjae_invite_01", StringComparison.OrdinalIgnoreCase))
            {
                flow.V3ShowDialogue("나", "(민재한테 메시지가 왔다. 확인해 보자.)", null);
            }
        }

        if (choices.Count > 0)
        {
            dialogue?.PreferConversation(speaker);
            waitingForMessageChoice = true;
            waitingMessageSpeaker = speaker;
            waitingMessageScene = activeScene;
            waitingMessageLineIndex = activeLineIndex;
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
            Save();
            return;
        }

        bool hasFollowingLine = activeScene != null && activeLineIndex + 1 < activeScene.lines.Count;
        bool needsReadBeforeContinuing = hasFollowingLine || !string.IsNullOrWhiteSpace(line.autoNext);
        if (!needsReadBeforeContinuing)
        {
            // 단독 안내 메시지는 알림으로 남기고 시나리오를 막지 않는다.
            // 이후 플레이어가 메시지 앱에서 언제든 확인할 수 있다.
            FinishLine(line);
            return;
        }

        // 핵심: 알림이 도착했다는 이유만으로 다음 독백/다음 장면을 재생하지 않는다.
        // 후속 대사/연출이 있는 메시지는 실제 채팅방을 열어 확인할 때까지 현재 줄에서 대기한다.
        waitingForIncomingMessageRead = true;
        waitingIncomingSpeaker = speaker;
        waitingIncomingLine = line;
        flow.V3MarkAppAttention(AppType.Message);
        Save();
    }

    private IEnumerator ResumeAfterIncomingMessageRead(ScenarioV3Line line)
    {
        yield return new WaitForSecondsRealtime(0.45f);
        incomingMessageCoroutine = null;
        if (waitingIncomingLine != line)
            yield break;
        waitingIncomingLine = null;
        waitingIncomingSpeaker = SpeakerType.Unknown;
        FinishLine(line);
    }

    private IEnumerator DeliverIncomingMessageWithTyping(
        ScenarioV3Line line,
        SpeakerType speaker,
        string contact,
        string text,
        List<ScenarioV3Choice> choices)
    {
        dialogue?.ShowTypingIndicator(speaker, contact);
        float typingDelay = GetTypingIndicatorDelay(text);
        yield return new WaitForSecondsRealtime(typingDelay);

        // 입력 중에 채팅방을 닫았어도 메시지는 도착해야 하므로 알림으로 전환한다.
        if (dialogue != null && dialogue.IsConversationOpen(speaker))
        {
            dialogue.ReceiveNotificationMessage(speaker, contact, text);
        }
        else if (notifications != null)
        {
            notifications.SendNotification(new NotificationData
            {
                title = contact,
                message = text,
                appType = AppType.Message,
                speakerType = speaker
            });
            PlaySfx(popupClip, 0.22f);
        }
        else
        {
            dialogue?.ReceiveNotificationMessage(speaker, contact, text);
        }

        SetState("unread_count", dialogue != null
            ? dialogue.TotalUnreadCount.ToString(CultureInfo.InvariantCulture)
            : GetState("unread_count"));

        if (choices.Count > 0)
        {
            dialogue?.PreferConversation(speaker);
            waitingForMessageChoice = true;
            waitingMessageSpeaker = speaker;
            waitingMessageScene = activeScene;
            waitingMessageLineIndex = activeLineIndex;
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
            incomingMessageCoroutine = null;
            Save();
            yield break;
        }

        yield return new WaitForSecondsRealtime(GetMessageReadingDelay(text, false));
        incomingMessageCoroutine = null;
        FinishLine(line);
    }

    private static float GetTypingIndicatorDelay(string text)
    {
        int characters = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(character => !char.IsWhiteSpace(character));
        return Mathf.Clamp(0.55f + characters * 0.028f, 0.8f, 1.8f);
    }

    public void ConfirmPendingOutgoingMessage(string lineId)
    {
        if (pendingOutgoingLine == null ||
            !string.Equals(pendingOutgoingLine.id, lineId, StringComparison.OrdinalIgnoreCase))
            return;

        ScenarioV3Line line = pendingOutgoingLine;
        deliveredOutgoingLineIds.Add(line.id);
        ClearPendingOutgoingMessage(false);
        SetState("unread_count", dialogue != null
            ? dialogue.TotalUnreadCount.ToString(CultureInfo.InvariantCulture)
            : "0");
        Save();
        FinishLine(line);
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

        // 앱은 AppWindow에서 먼저 활성화되므로 고정 1초 대기 대신
        // 레이아웃이 한두 프레임 안정된 뒤 바로 메시지를 보여 준다.
        yield return null;
        if (!IsMessageAppReady())
        {
            pendingOutgoingCoroutine = null;
            yield break;
        }

        dialogue.OpenDialogue(speaker);
        yield return null;

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

        yield return new WaitForSecondsRealtime(GetMessageReadingDelay(text, true));
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

    private IEnumerator AdvanceMessageLine(
        ScenarioV3Line line,
        string text,
        bool waitForMessageAppClose,
        bool nextLineIsMessage)
    {
        float delay = nextLineIsMessage ? GetMessageReadingDelay(text, false) : 0.4f;
        yield return new WaitForSecondsRealtime(delay);
        while (waitForMessageAppClose && appWindow != null &&
               appWindow.CurrentAppType == AppType.Message)
        {
            yield return null;
        }
        FinishLine(line);
    }

    private static float GetMessageReadingDelay(string text, bool sentByPlayer)
    {
        int readableCharacters = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(character => !char.IsWhiteSpace(character));
        float baseDelay = sentByPlayer ? 0.22f : 0.28f;
        float perCharacter = sentByPlayer ? 0.008f : 0.012f;
        float minimum = sentByPlayer ? 0.35f : 0.45f;
        float maximum = sentByPlayer ? 0.8f : 1.1f;
        return Mathf.Clamp(baseDelay + readableCharacters * perCharacter, minimum, maximum);
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
                if (!deliveredIncomingLineIds.Add(line.id))
                    continue;
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
        ShowNovelLine(line, () => FinishLine(line));
    }

    private void ShowNovelLine(ScenarioV3Line line, Action completed, string visualArc = null)
    {
        if (novelPanel == null)
            return;

        string resolvedArc = visualArc ?? activeScene?.arc ?? "home";
        if (!bypassHomeTimeTransition && TryStartHomeTimeTransition(line, completed, visualArc, resolvedArc))
            return;

        notifications?.HidePopup();
        novelPanel.SetActive(true);
        novelPanel.transform.SetAsLastSibling();
        chapterText.text = $"{flow.CurrentDay:00}일차  |  {flow.V3ClockText}";
        string displaySpeaker = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ContactName(line.speaker);
        speakerText.text = displaySpeaker;
        string rawText = ExpandText(line.text);
        currentDialoguePages = PaginateDialogue(rawText);
        if (IsProtagonistMonologue(line))
        {
            for (int i = 0; i < currentDialoguePages.Count; i++)
                currentDialoguePages[i] = FormatProtagonistMonologue(line, currentDialoguePages[i]);
        }
        currentDialoguePageIndex = 0;
        currentFullText = currentDialoguePages[0];
        string expandedText = string.Join(" ", currentDialoguePages);
        string logSpeaker = string.Equals(line.speaker, "Narrator", StringComparison.OrdinalIgnoreCase)
            ? "내레이션"
            : string.IsNullOrWhiteSpace(displaySpeaker) ? "나" : displaySpeaker;
        AppendDialogueLog(logSpeaker, expandedText);
        ApplyArcVisual(resolvedArc, line.delivery);
        if (IsHomeVisualArc(resolvedArc))
            lastHomeVisualPeriod = GetHomeVisualPeriod();
        ApplyCharacterPortrait(line);

        List<ScenarioV3Choice> choices = GetAvailableChoices(line);
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
                completed?.Invoke();
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

    private List<ScenarioV3Choice> GetAvailableChoices(ScenarioV3Line line)
    {
        if (line == null)
            return new List<ScenarioV3Choice>();

        return line.Choices.Where(IsChoiceAvailable).ToList();
    }

    private bool IsChoiceAvailable(ScenarioV3Choice choice)
    {
        if (choice == null || string.IsNullOrWhiteSpace(choice.id))
            return false;

        if (choice.id.Equals("borrow_mom", StringComparison.OrdinalIgnoreCase))
            return GetState("borrowed.mom") != "true";
        if (choice.id.Equals("borrow_friend", StringComparison.OrdinalIgnoreCase))
            return GetState("borrowed.seojun") != "true";
        if (choice.id.Equals("minjae_loan_accept", StringComparison.OrdinalIgnoreCase))
            return GetState("borrowed.minjae") != "true";
        if (choice.id.Equals("no_funds_borrow_again", StringComparison.OrdinalIgnoreCase))
        {
            return GetState("borrowed.mom") != "true" ||
                   GetState("borrowed.seojun") != "true" ||
                   GetState("borrowed.minjae") != "true";
        }

        return true;
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
        if (activeScene == null || activeLineIndex < 0 || activeLineIndex >= activeScene.lines.Count)
            return;

        ScenarioV3Line currentLine = activeScene.lines[activeLineIndex];
        if (!ReferenceEquals(currentLine, line) &&
            !string.Equals(currentLine.id, line?.id, StringComparison.OrdinalIgnoreCase))
            return;

        string next = line.autoNext;
        activeLineIndex++;
        if ((pendingLateWakeAfterGambling || pendingBorrowMorningAdvance) &&
            (activeLineIndex >= activeScene.lines.Count || !string.IsNullOrWhiteSpace(next)))
        {
            BeginForcedLateMorningAdvance();
            return;
        }
        if (!database.ShouldReturnToTablet(activeScene?.id) && !string.IsNullOrWhiteSpace(next))
        {
            Action playNext = () =>
            {
                if (TryReturnHomeBeforeNextScene(next, () => PlayScene(next)))
                    return;
                activeScene = null;
                PlayScene(next);
            };
            if (string.Equals(line.delivery, "message", StringComparison.OrdinalIgnoreCase) &&
                dialogue != null && dialogue.IsDialogueOpen)
            {
                waitingForMessageSceneClose = true;
                pendingAfterMessageClose = playNext;
                return;
            }
            playNext();
            return;
        }
        PresentLine();
    }

    private void FinishScene()
    {
        if (waitingForMessageChoice)
            return;

        if (pendingLateWakeAfterGambling || pendingBorrowMorningAdvance)
        {
            BeginForcedLateMorningAdvance();
            return;
        }

        bool isMessageScene = activeScene != null && activeScene.lines.Any(candidate =>
            string.Equals(candidate.delivery, "message", StringComparison.OrdinalIgnoreCase));
        if (isMessageScene && dialogue != null && dialogue.IsDialogueOpen)
        {
            waitingForMessageSceneClose = true;
            return;
        }
        waitingForMessageSceneClose = false;

        bool returnToTablet = activeScene != null && database.ShouldReturnToTablet(activeScene.id);
        bool hasQueuedScene = sceneQueue.Count > 0;
        string activityArc = activeScene?.arc;
        bool returnHome = IsActivityArc(activityArc) && flow.CurrentLocation != "집" && !hasQueuedScene;
        activeScene = null;
        activeLineIndex = 0;
        Save();
        if (pendingDayAdvance)
        {
            pendingDayAdvance = false;
            HideNovel();
            flow.V3MarkAppAttention(AppType.Sleep);
            TryQueueBedtimeCue();
            return;
        }
        if (returnHome)
        {
            ShowReturnHomeMonologue(activityArc, ContinueAfterActivityReturn);
            return;
        }
        if (TryResolveMissedJob())
            return;
        if (returnToTablet && !hasQueuedScene)
        {
            HideNovel();
            waitingForMessageChoice = false;
            waitingMessageSpeaker = SpeakerType.Unknown;
            waitingMessageScene = null;
            waitingMessageLineIndex = -1;
            appWindow?.CloseCurrentApp();

            // DOBak V13-D04: 태블릿 복귀 장면이 day_start 뒤의 차용 안내 같은 예약 작업을 지우지 않게 한다.
            // 먼저 남은 완료 콜백을 소진하고, 그 콜백이 새 장면/메시지를 시작했다면 저녁·취침 자동 진행을 막는다.
            StartQueuedScene();
            if (activeScene != null || sceneQueue.Count > 0 || queueCompleted != null ||
                waitingForMessageChoice || pendingOutgoingLine != null || waitingForMessageSceneClose ||
                sceneTransitionInProgress)
            {
                return;
            }

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
            flow.V3ShowDialogue("나", "오늘 일정은 모두 끝냈다. 도박 앱도 눈에 들어오고, 피곤하기도 한데.... 어떻게 할지 정해야겠다.",
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

        string activityArc = activeScene.arc;
        activeScene = null;
        ShowReturnHomeMonologue(activityArc, completed);
        return true;
    }

    private void ShowReturnHomeMonologue(string activityArc, Action completed)
    {
        string prompt = string.Equals(activityArc, "school", StringComparison.OrdinalIgnoreCase)
            ? "종례도 끝났다. 집에 가서 남은 일정을 확인하자."
            : "오늘 근무도 끝났다. 정리하고 집에 가자.";
        var line = new ScenarioV3Line
        {
            id = "return_home_monologue",
            speaker = "Protagonist",
            contact = "나",
            delivery = "narration",
            text = prompt
        };
        ShowNovelLine(line, () =>
        {
            // VN을 먼저 숨기면 귀가 페이드가 시작되기 전에 태블릿 홈이 비친다.
            // 화면이 완전히 검어진 시점에 VN을 숨긴다.
            flow.V3ReturnHomeAfterActivity(completed, HideNovel);
        }, activityArc);
    }

    private string FormatProtagonistMonologue(ScenarioV3Line line, string text)
    {
        if (!IsProtagonistMonologue(line) || string.IsNullOrWhiteSpace(text))
            return text;

        string trimmed = text.Trim();
        while (trimmed.StartsWith("(", StringComparison.Ordinal) &&
               trimmed.EndsWith(")", StringComparison.Ordinal) && trimmed.Length >= 2)
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }
        return $"({trimmed})";
    }

    private bool IsProtagonistMonologue(ScenarioV3Line line)
    {
        bool isProtagonist = string.Equals(line?.speaker, "Protagonist", StringComparison.OrdinalIgnoreCase);
        if (!isProtagonist)
            return false;

        bool isMonologue = string.Equals(line?.delivery, "narration", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(line?.delivery, "overlay", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(line?.delivery, "cinematic", StringComparison.OrdinalIgnoreCase);
        if (!isMonologue && string.Equals(line?.delivery, "dialogue", StringComparison.OrdinalIgnoreCase) &&
            activeScene != null)
        {
            isMonologue = !activeScene.lines.Any(candidate =>
                !string.Equals(candidate.speaker, "Protagonist", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.speaker, "Narrator", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.speaker, "System", StringComparison.OrdinalIgnoreCase));
        }
        return isMonologue;
    }

    private static bool IsActivityArc(string arc)
    {
        return string.Equals(arc, "school", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arc, "job", StringComparison.OrdinalIgnoreCase);
    }

    private void BeginForcedLateMorningAdvance()
    {
        bool explicitBorrowDeferral = pendingBorrowMorningAdvance ||
                                      GetState("flag.borrow_deferred") == "true";
        bool showBorrowMenu = explicitBorrowDeferral || GetState("pending.borrow_menu") == "true" ||
                              (flow.V3BankCash <= 0 && GetInt("counter.gamble_sessions") >= 5);
        // DOBak V13-D05: 차용 연락을 아침으로 미룬 것과 실제 도박 밤샘을 분리한다.
        bool wokeFromGambling = pendingLateWakeAfterGambling;

        pendingLateWakeAfterGambling = false;
        pendingBorrowMorningAdvance = false;

        FinalizeCurrentDayStatus();
        if (flow.CurrentDay >= FinalDay)
        {
            activeScene = null;
            activeLineIndex = 0;
            HideNovel();
            return;
        }

        sceneQueue.Clear();
        queueCompleted = null;
        activeScene = null;
        activeLineIndex = 0;
        waitingForMessageChoice = false;
        waitingForMessageSceneClose = false;
        pendingAfterMessageClose = null;
        ClearPendingOutgoingMessage();
        HideNovel();
        appWindow?.CloseCurrentApp();

        flow.V3BeginNextDay();
        dialogueLog.Clear();
        state["schedule.school"] = "pending";
        state["schedule.homework"] = "pending";
        state["schedule.job"] = "pending";
        state["schedule.sleep"] = "pending";
        state["evening_filled"] = "0";
        state["bedtime_cued"] = "0";
        state["day_finalized"] = "0";
        state["pending.gamble_attention"] = "false";
        state["pending.borrow_menu"] = showBorrowMenu ? "true" : "false";
        // DOBak V13-D06: 실제 밤샘만 10시 늦잠으로 처리한다. 차용 예약만 있으면 7시에 정상 기상한다.
        state["flag.late_wake_today"] = wokeFromGambling ? "true" : "false";
        state["flag.borrow_deferred"] = wokeFromGambling && explicitBorrowDeferral ? "true" : "false";
        state["flag.gambled_late"] = wokeFromGambling ? "true" : "false";
        state["day_cash_start"] = flow.V3BankCash.ToString(CultureInfo.InvariantCulture);
        flow.V3SetLocation("집");
        flow.V3SetClock(wokeFromGambling ? "10:00" : "07:00");
        Save();

        QueueTrigger("day_start", () =>
        {
            // 특수 기상 연출과 그 날의 부가 이벤트가 모두 끝난 뒤에만 차용을 이어 간다.
            SetState("flag.late_wake_today", "false");
            // DOBak V13-D07: 당일 아침 장면 선택이 끝난 뒤 임시 플래그를 정리해 다음 날 장면에 남기지 않는다.
            SetState("flag.gambled_late", "false");
            SetState("flag.borrow_deferred", "false");
            if (GetState("pending.borrow_menu") != "true")
            {
                Save();
                return;
            }

            SetState("pending.borrow_menu", "false");
            Save();
            PlayScene("borrow_morning_cue");
        });
        StartQueuedScene();
    }

    private static bool CrossesClockHour(int startHour, int elapsedHours, int targetHour)
    {
        if (elapsedHours <= 0)
            return false;

        int normalizedStart = ((startHour % 24) + 24) % 24;
        int normalizedTarget = ((targetHour % 24) + 24) % 24;
        int distance = (normalizedTarget - normalizedStart + 24) % 24;
        if (distance == 0)
            distance = 24;
        return elapsedHours >= distance;
    }

    private void AdvanceToNextDay()
    {
        ResolvePendingGambleAttentionAsDeclined();
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
        state["pending.gamble_attention"] = "false";
        state["flag.late_wake_today"] = "false";
        state["flag.borrow_deferred"] = "false";
        // DOBak V13-D08: 정상 취침으로 넘어간 날에도 전날 밤샘 표식을 정리한다.
        state["flag.gambled_late"] = "false";
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
            SetState("schedule.job", "missed");
        // DOBak V13-D09: 즉시 결근 처리로 이미 missed가 된 날도 하루 확정 시 정확히 한 번 집계한다.
        // day_finalized 가드 덕분에 중복 호출되어도 같은 결근을 두 번 세지 않는다.
        if (weekendDay && GetState("schedule.job") == "missed")
            AddInt("counter.job_failures", 1);

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
            operation.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            SetState("flag.gambling_app_unlocked", "true");
            SetState("pending.gamble_attention", "true");
            flow.V3SetGamblingUnlocked(true, true);
            return;
        }
        if (key.Equals("gamble", StringComparison.OrdinalIgnoreCase) &&
            (operation.Equals("offer", StringComparison.OrdinalIgnoreCase) ||
             operation.Equals("attention", StringComparison.OrdinalIgnoreCase)))
        {
            SetState("pending.gamble_attention", "true");
            flow.V3SetGamblingAttention(true);
            return;
        }
        if (key.Equals("gamble", StringComparison.OrdinalIgnoreCase) &&
            operation.Equals("advance", StringComparison.OrdinalIgnoreCase))
        {
            SetState("pending.gamble_attention", "false");
            flow.V3SetGamblingAttention(false);
            int session = GetInt("counter.gamble_sessions") + 1;
            if (session >= 6 && flow.V3BankCash <= 0)
            {
                AddInt("counter.no_funds_attempts", 1);
                bool allBorrowSourcesUsed = GetState("borrowed.mom") == "true" &&
                                            GetState("borrowed.seojun") == "true" &&
                                            GetState("borrowed.minjae") == "true";
                immediateRoute = allBorrowSourcesUsed ? "gamble_no_funds_exhausted" : "gamble_no_funds";
                return;
            }
            if (session > 6)
            {
                SetState("counter.gamble_sessions", session.ToString(CultureInfo.InvariantCulture));
                immediateRoute = "gamble_repeat_loss";
                return;
            }
            SetState("counter.gamble_sessions", session.ToString(CultureInfo.InvariantCulture));
            SetState("flag.gambling_started", "true");
            immediateRoute = "gamble_" + session.ToString(CultureInfo.InvariantCulture);
            return;
        }
        if (key.Equals("borrow", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.Equals("defer", StringComparison.OrdinalIgnoreCase))
            {
                SetState("pending.borrow_menu", "true");
                SetState("flag.borrow_deferred", "true");
                pendingBorrowMorningAdvance = true;
                return;
            }
            if (operation.StartsWith("prepare=", StringComparison.OrdinalIgnoreCase))
            {
                PrepareDeferredBorrowRequest(operation.Substring("prepare=".Length));
                return;
            }
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

                    // DOBak V13-D10: 게임의 하루 시작 시각(07:00)을 실제 밤샘 경계로 사용한다.
                    if (GetState("flag.gambled_late") == "true" &&
                        CrossesClockHour(startHour, elapsedHours, 7))
                    {
                        pendingLateWakeAfterGambling = true;
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
        if (key.Equals("repay", StringComparison.OrdinalIgnoreCase) &&
            operation.Equals("available", StringComparison.OrdinalIgnoreCase))
        {
            int repaid = flow.V3RepayAvailableDebt("서준에게 빌린 돈 상환");
            SetState("last_repayment", repaid.ToString(CultureInfo.InvariantCulture));
            if (flow.CurrentDebt <= 0)
                SetState("debt_owner", "none");
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

    private void ResolvePendingGambleAttentionAsDeclined()
    {
        if (!HasPendingGambleOffer)
            return;

        SetState("pending.gamble_attention", "false");
        AddInt("counter.refusals", 1);
        flow.V3SetGamblingAttention(false);
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

        if (historyPanel == null)
            return;

        Stretch(historyPanel.GetComponent<RectTransform>());
        Image historyBackground = historyPanel.GetComponent<Image>();
        if (historyBackground == null)
            historyBackground = historyPanel.AddComponent<Image>();
        historyBackground.color = new Color(0.015f, 0.025f, 0.04f, 1f);
        historyBackground.raycastTarget = true;

        TMP_Text title = historyPanel.transform.Find("History Title")?.GetComponent<TMP_Text>();
        if (title != null)
        {
            RectTransform titleRect = title.rectTransform;
            titleRect.anchorMin = titleRect.anchorMax = new Vector2(0f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(72f, -44f);
            titleRect.sizeDelta = new Vector2(520f, 64f);
            title.alignment = TextAlignmentOptions.MidlineLeft;
        }

        RebuildHistoryViewport();
    }

    private void RebuildHistoryViewport()
    {
        if (historyPanel == null)
            return;

        // 이전 핫픽스에서 만든 런타임 복제 Viewport가 남아 있으면 제거한다.
        Transform runtimeViewport = historyPanel.transform.Find("History Viewport Runtime");
        if (runtimeViewport != null)
            Destroy(runtimeViewport.gameObject);

        Transform viewportTransform = historyPanel.transform.Find("History Viewport");
        if (viewportTransform == null)
            return;

        viewportTransform.gameObject.SetActive(true);
        historyViewportRect = viewportTransform.GetComponent<RectTransform>();
        historyViewportRect.anchorMin = new Vector2(0.06f, 0.08f);
        historyViewportRect.anchorMax = new Vector2(0.94f, 0.86f);
        historyViewportRect.offsetMin = Vector2.zero;
        historyViewportRect.offsetMax = Vector2.zero;
        historyViewportRect.pivot = new Vector2(0.5f, 0.5f);

        Image viewportImage = viewportTransform.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewportTransform.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(0.04f, 0.065f, 0.10f, 1f);
        viewportImage.raycastTarget = true;

        // RectMask2D가 실제 배치 씬에서 정상적으로 잘리지 않는 사례가 있어
        // 메시지 Viewport와 같은 부모-자식 구조를 유지하면서 stencil Mask로 강제 클리핑한다.
        RectMask2D rectMask = viewportTransform.GetComponent<RectMask2D>();
        if (rectMask != null)
            rectMask.enabled = false;
        Mask stencilMask = viewportTransform.GetComponent<Mask>();
        if (stencilMask == null)
            stencilMask = viewportTransform.gameObject.AddComponent<Mask>();
        stencilMask.showMaskGraphic = true;

        Transform contentTransform = viewportTransform.Find("History Content");
        if (contentTransform == null)
        {
            GameObject contentObject = new GameObject("History Content", typeof(RectTransform));
            contentObject.layer = historyPanel.layer;
            contentObject.transform.SetParent(viewportTransform, false);
            contentTransform = contentObject.transform;
        }
        historyContentRect = contentTransform.GetComponent<RectTransform>();
        historyContentRect.anchorMin = new Vector2(0f, 1f);
        historyContentRect.anchorMax = new Vector2(1f, 1f);
        historyContentRect.pivot = new Vector2(0.5f, 1f);
        historyContentRect.anchoredPosition = Vector2.zero;

        VerticalLayoutGroup layout = contentTransform.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
            layout.enabled = false;
        ContentSizeFitter contentFitter = contentTransform.GetComponent<ContentSizeFitter>();
        if (contentFitter != null)
            contentFitter.enabled = false;

        TMP_Text boundText = contentTransform.Find("History Text")?.GetComponent<TMP_Text>();
        if (boundText == null)
            boundText = historyText;
        if (boundText == null)
        {
            GameObject textObject = new GameObject("History Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.layer = historyPanel.layer;
            textObject.transform.SetParent(contentTransform, false);
            TextMeshProUGUI created = textObject.GetComponent<TextMeshProUGUI>();
            created.font = bodyText != null ? bodyText.font : null;
            created.fontSize = 27f;
            created.fontStyle = FontStyles.Bold;
            created.color = new Color(0.9f, 0.93f, 0.98f);
            boundText = created;
        }
        else if (boundText.transform.parent != contentTransform)
        {
            boundText.transform.SetParent(contentTransform, false);
        }

        historyText = boundText;
        historyText.gameObject.SetActive(true);
        historyText.alignment = TextAlignmentOptions.TopLeft;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        historyText.raycastTarget = false;
        historyText.maskable = true;
        ContentSizeFitter textFitter = historyText.GetComponent<ContentSizeFitter>();
        if (textFitter != null)
            textFitter.enabled = false;

        // 혹시 다른 legacy History Text가 남아 있으면 반드시 숨긴다.
        foreach (TMP_Text legacy in historyPanel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (legacy == historyText)
                continue;
            if (legacy.gameObject.name.StartsWith("History Text", StringComparison.OrdinalIgnoreCase))
                legacy.gameObject.SetActive(false);
        }

        historyScroll = viewportTransform.GetComponent<ScrollRect>();
        if (historyScroll == null)
            historyScroll = viewportTransform.gameObject.AddComponent<ScrollRect>();
        historyScroll.viewport = historyViewportRect;
        historyScroll.content = historyContentRect;
        historyScroll.horizontal = false;
        historyScroll.vertical = true;
        historyScroll.movementType = ScrollRect.MovementType.Clamped;
        historyScroll.scrollSensitivity = 45f;
        historyScroll.inertia = true;
        historyScroll.decelerationRate = 0.12f;

        RefreshHistoryLayout();
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
        if (sceneId == "d6_mom_allowance")
            return "엄마 용돈";
        if (sceneId.StartsWith("gamble_", StringComparison.OrdinalIgnoreCase))
            return amount > 0 ? "도박 앱 자동 환전" : "도박 앱 결제";
        if (sceneId.Contains("mom_loan", StringComparison.OrdinalIgnoreCase))
            return "엄마에게 빌린 돈";
        if (sceneId.Contains("seojun_loan", StringComparison.OrdinalIgnoreCase))
            return "서준에게 빌린 돈";
        if (sceneId.Contains("minjae_loan", StringComparison.OrdinalIgnoreCase))
            return "민재에게 빌린 돈";
        return amount >= 0 ? "입금" : "결제";
    }

    private bool TryStartHomeTimeTransition(ScenarioV3Line line, Action completed, string visualArc, string resolvedArc)
    {
        if (!IsHomeVisualArc(resolvedArc) || flow == null || flow.CurrentLocation != "집")
            return false;

        int targetPeriod = GetHomeVisualPeriod();
        if (lastHomeVisualPeriod < 0 || targetPeriod <= lastHomeVisualPeriod)
        {
            lastHomeVisualPeriod = targetPeriod;
            return false;
        }

        if (homeTimeTransitionCoroutine != null)
            StopCoroutine(homeTimeTransitionCoroutine);
        homeTimeTransitionCoroutine = StartCoroutine(
            RunHomeTimeTransition(line, completed, visualArc, lastHomeVisualPeriod, targetPeriod));
        return true;
    }

    private IEnumerator RunHomeTimeTransition(
        ScenarioV3Line line, Action completed, string visualArc, int fromPeriod, int targetPeriod)
    {
        notifications?.HidePopup();
        novelPanel.SetActive(true);
        novelPanel.transform.SetAsLastSibling();

        GameObject dialogueBox = bodyText != null && bodyText.transform.parent != null
            ? bodyText.transform.parent.gameObject
            : null;
        bool dialogueWasActive = dialogueBox != null && dialogueBox.activeSelf;
        if (dialogueBox != null)
            dialogueBox.SetActive(false);
        if (characterPortrait != null)
            characterPortrait.gameObject.SetActive(false);

        Texture2D startTexture = LoadHomeTexture(fromPeriod);
        if (startTexture != null)
        {
            novelBackground.texture = startTexture;
            novelBackground.color = Color.white;
        }

        // 낮에서 밤으로 크게 건너뛰어도 반드시 저녁을 거쳐 자연스럽게 바뀐다.
        for (int period = fromPeriod + 1; period <= targetPeriod; period++)
        {
            Texture2D targetTexture = LoadHomeTexture(period);
            if (targetTexture == null)
                continue;
            yield return CrossfadeHomeBackground(targetTexture, 0.65f);
            if (period < targetPeriod)
                yield return new WaitForSecondsRealtime(0.16f);
        }

        lastHomeVisualPeriod = targetPeriod;
        homeTimeTransitionCoroutine = null;
        if (dialogueBox != null && dialogueWasActive)
            dialogueBox.SetActive(true);

        bypassHomeTimeTransition = true;
        ShowNovelLine(line, completed, visualArc);
        bypassHomeTimeTransition = false;
    }

    private IEnumerator CrossfadeHomeBackground(Texture2D targetTexture, float duration)
    {
        if (homeTransitionOverlay == null)
        {
            homeTransitionOverlay = new GameObject("Home Time Crossfade", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
            homeTransitionOverlay.transform.SetParent(novelPanel.transform, false);
            Stretch(homeTransitionOverlay.rectTransform);
            homeTransitionOverlay.raycastTarget = false;
        }

        homeTransitionOverlay.transform.SetSiblingIndex(
            Mathf.Min(novelBackground.transform.GetSiblingIndex() + 1, novelPanel.transform.childCount - 1));
        homeTransitionOverlay.texture = targetTexture;
        homeTransitionOverlay.color = new Color(1f, 1f, 1f, 0f);
        homeTransitionOverlay.gameObject.SetActive(true);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float alpha = Mathf.Clamp01(elapsed / duration);
            homeTransitionOverlay.color = new Color(1f, 1f, 1f, alpha);
            yield return null;
        }

        novelBackground.texture = targetTexture;
        novelBackground.color = Color.white;
        homeTransitionOverlay.color = new Color(1f, 1f, 1f, 0f);
        homeTransitionOverlay.gameObject.SetActive(false);
    }

    private int GetHomeVisualPeriod()
    {
        if (flow == null)
            return 0;
        if (flow.CurrentHour >= 21 || flow.CurrentHour < 6)
            return 2;
        if (flow.CurrentHour >= 18)
            return 1;
        return 0;
    }

    private static bool IsHomeVisualArc(string arc)
    {
        string normalized = (arc ?? string.Empty).ToLowerInvariant();
        // sleep 장면도 집에서 낮→저녁→밤 전환을 거치게 한다.
        // debt는 별도 메시지/상환 장면이 있어 여기서는 기존 분리 정책을 유지한다.
        return normalized != "school" && normalized != "homework" && normalized != "job" &&
               normalized != "gambling" && normalized != "debt";
    }

    private static Texture2D LoadHomeTexture(int period)
    {
        string resource = period >= 2 ? "ScenarioArt/bedroom_night"
            : period == 1 ? "ScenarioArt/bedroom_evening"
            : "ScenarioArt/bedroom";
        return Resources.Load<Texture2D>(resource);
    }

    private void ApplyArcVisual(string arc, string delivery)
    {
        bool isHome = flow != null && flow.CurrentLocation == "집";
        bool isEveningAtHome = isHome && flow.CurrentHour >= 18 && flow.CurrentHour <= 20;
        bool isNightAtHome = isHome && (flow.CurrentHour >= 21 || flow.CurrentHour < 6);
        string resource = arc switch
        {
            "school" or "homework" => "ScenarioArt/classroom",
            "job" => "ScenarioArt/cafe",
            "gambling" => "ScenarioArt/temptation",
            "debt" => isEveningAtHome ? "ScenarioArt/bedroom_evening"
                : isNightAtHome ? "ScenarioArt/bedroom_night"
                : "ScenarioArt/bedroom",
            "sleep" => "ScenarioArt/bedroom_night",
            _ => isEveningAtHome ? "ScenarioArt/bedroom_evening"
                : isNightAtHome ? "ScenarioArt/bedroom_night"
                : "ScenarioArt/bedroom"
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
        StartCoroutine(ScrollHistoryToLatest());
    }

    private void RefreshHistoryLayout()
    {
        if (historyText == null || historyViewportRect == null || historyContentRect == null)
            return;

        Canvas.ForceUpdateCanvases();
        float viewportWidth = Mathf.Max(480f, historyViewportRect.rect.width);
        float viewportHeight = Mathf.Max(320f, historyViewportRect.rect.height);

        const float sidePadding = 32f;
        const float verticalPadding = 24f;
        float textWidth = Mathf.Max(320f, viewportWidth - sidePadding * 2f);
        float preferredHeight = Mathf.Max(80f,
            historyText.GetPreferredValues(historyText.text, textWidth, 0f).y);
        float contentHeight = Mathf.Max(viewportHeight, preferredHeight + verticalPadding * 2f);

        // Content만 세로로 길어진다. Viewport와 텍스트 가로폭은 고정한다.
        historyContentRect.anchorMin = new Vector2(0f, 1f);
        historyContentRect.anchorMax = new Vector2(1f, 1f);
        historyContentRect.pivot = new Vector2(0.5f, 1f);
        historyContentRect.anchoredPosition = Vector2.zero;
        historyContentRect.sizeDelta = new Vector2(0f, contentHeight);

        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 0f);
        textRect.pivot = new Vector2(0.5f, 0f);
        textRect.anchoredPosition = new Vector2(0f, verticalPadding);
        // 텍스트 가로폭 = Viewport 폭 - 좌우 여백.
        textRect.sizeDelta = new Vector2(-sidePadding * 2f, preferredHeight);

        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        historyText.maskable = true;

        if (historyScroll != null)
        {
            historyScroll.viewport = historyViewportRect;
            historyScroll.content = historyContentRect;
            historyScroll.horizontal = false;
            historyScroll.vertical = true;
            historyScroll.movementType = ScrollRect.MovementType.Clamped;
            historyScroll.StopMovement();
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(historyContentRect);
        Canvas.ForceUpdateCanvases();
    }

    private IEnumerator ScrollHistoryToLatest()
    {
        yield return null;
        RefreshHistoryLayout();
        yield return null;

        if (historyScroll == null)
            yield break;

        Canvas.ForceUpdateCanvases();
        historyScroll.StopMovement();
        historyScroll.velocity = Vector2.zero;
        historyScroll.verticalNormalizedPosition = 0f;
        yield return null;
        historyScroll.verticalNormalizedPosition = 0f;
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
