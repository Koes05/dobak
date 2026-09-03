using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Scenario V3 final flow/staging hotfix.
///
/// This component deliberately does not change the game's design. It repairs the runtime connections
/// between the existing schedule, VN, message, debt and history systems. It bootstraps itself in the
/// TabletUI scene, so no scene prefab needs to be edited by hand.
/// </summary>
public sealed class ScenarioV3FinalRuntimeFix : MonoBehaviour
{
    private const string TabletSceneName = "TabletUI";
    private const string PatchVersion = "V17-0903";

    private GameFlowManager flow;
    private ScenarioV3Director director;
    private DialogueManager dialogue;
    private NotificationManager notifications;
    private AppWindow appWindow;
    private ScenarioV3Database database;

    private readonly Dictionary<ScenarioV3CheckpointData, CheckpointRuntimeSnapshot> checkpointSnapshots =
        new Dictionary<ScenarioV3CheckpointData, CheckpointRuntimeSnapshot>();
    private readonly HashSet<int> patchedSchoolButtons = new HashSet<int>();
    private readonly HashSet<int> patchedDebounceButtons = new HashSet<int>();

    private GameObject choiceOverlay;
    private TMP_Text choiceOverlaySpeaker;
    private TMP_Text choiceOverlayBody;
    private readonly List<Button> choiceOverlayButtons = new List<Button>();
    private ScenarioV3Line choiceOverlayLine;
    private SpeakerType choiceOverlayMessageSpeaker = SpeakerType.Unknown;
    private bool choiceOverlayBusy;

    private Button gamblingLauncherButton;
    private Button rewindButton;
    private int lastDay = -1;
    private int lastCheckpointCount = -1;
    private bool explicitBorrowPending;
    private bool borrowOverlayShownForCurrentDay;
    private bool restoringCheckpoint;
    private List<string> preservedDialogueLog = new List<string>();
    private float nextButtonScanAt;

    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterBootstrap()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!string.Equals(scene.name, TabletSceneName, StringComparison.Ordinal))
            return;

        if (FindAnyObjectByType<ScenarioV3FinalRuntimeFix>(FindObjectsInactive.Include) != null)
            return;

        new GameObject("Scenario V3 Final Runtime Fix " + PatchVersion)
            .AddComponent<ScenarioV3FinalRuntimeFix>();
    }

    private IEnumerator Start()
    {
        float timeoutAt = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeoutAt)
        {
            flow = GameFlowManager.Instance ?? FindAnyObjectByType<GameFlowManager>(FindObjectsInactive.Include);
            director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
            dialogue = FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
            notifications = FindAnyObjectByType<NotificationManager>(FindObjectsInactive.Include);
            appWindow = FindAnyObjectByType<AppWindow>(FindObjectsInactive.Include);

            if (flow != null && director != null && director.IsReady && dialogue != null)
                break;
            yield return null;
        }

        if (flow == null || director == null || !director.IsReady || dialogue == null)
        {
            Debug.LogError("[Scenario V3 Final Fix] 필요한 매니저를 찾지 못해 적용을 중단했습니다.");
            enabled = false;
            yield break;
        }

        database = GetField<ScenarioV3Database>(director, "database");
        if (database == null)
        {
            Debug.LogError("[Scenario V3 Final Fix] 시나리오 데이터베이스를 찾지 못했습니다.");
            enabled = false;
            yield break;
        }

        // TabletUI 진입은 '처음부터 시작'이므로 이전 런타임 채팅만 이때 정리한다.
        // 엔딩의 분기점 되감기는 아래의 스냅샷 복원 경로를 사용하므로 이 코드를 다시 타지 않는다.
        dialogue.ResetScenarioConversations();
        notifications?.Clear();

        PatchScenarioDatabase();
        CreateChoiceOverlay();
        PatchHistoryViewport();
        PatchButtons(true);

        lastDay = flow.CurrentDay;
        preservedDialogueLog = CopyDialogueLog();
        CaptureNewCheckpointSnapshots();

        Debug.Log("[Scenario V3 Final Fix] " + PatchVersion + " 적용 완료");
    }

    private void Update()
    {
        if (flow == null || director == null || !director.IsReady)
            return;

        if (Time.unscaledTime >= nextButtonScanAt)
        {
            nextButtonScanAt = Time.unscaledTime + 0.75f;
            PatchButtons(false);
        }

        TrackExplicitBorrowRequest();
        KeepMinjaeLoanOfferRepeatableUntilAccepted();
        HandleDayChangeAndDialogueLog();
        CaptureNewCheckpointSnapshots();
        TryShowDeferredBorrowChoice();
        TryReplaceDebtChatChoicesWithDialogue();
    }

    private void LateUpdate()
    {
        if (flow == null)
            return;

        ApplyExactAttentionDots();
        if (choiceOverlay != null && choiceOverlay.activeSelf)
            choiceOverlay.transform.SetAsLastSibling();

        GameObject historyPanel = GetField<GameObject>(director, "historyPanel");
        if (historyPanel != null && historyPanel.activeInHierarchy)
            PatchHistoryViewport();
    }

    // ---------------------------------------------------------------------
    // Scenario data corrections
    // ---------------------------------------------------------------------

    private void PatchScenarioDatabase()
    {
        // Borrowing is deferred to normal sleep, not a forced morning transition.
        ScenarioV3Line borrowDefer = FindLine("borrow_defer_night_01");
        if (borrowDefer != null)
        {
            borrowDefer.text = "이 시간에 돈을 빌려 달라고 연락하는 건 늦었다. 오늘은 자고, 아침에 누구에게 부탁할지 정하자.";
            borrowDefer.enterEffects =
                "pending.borrow_menu:set=true|flag.borrow_deferred:set=true|tutorial:set=sleep";
        }

        ScenarioV3Scene invalidBorrowMorning = database.GetScene("sys_borrow_late_morning");
        if (invalidBorrowMorning != null)
            invalidBorrowMorning.condition = "day<0";

        ScenarioV3Line borrowMorning = FindLine("borrow_morning_cue_01");
        if (borrowMorning != null)
        {
            // Keep the legacy scene as a silent compatibility router. The visible target choice is
            // rendered once, directly over the tablet home screen by TryShowDeferredBorrowChoice.
            borrowMorning.delivery = "router";
            borrowMorning.text = string.Empty;
            borrowMorning.enterEffects = "pending.borrow_menu:set=true";
            borrowMorning.autoNext = string.Empty;
        }

        ScenarioV3Line borrowChoice = FindLine("borrow_choice_01");
        if (borrowChoice != null)
        {
            borrowChoice.text = "어젯밤 미뤄 둔 연락을 지금 정해야 한다. 누구에게 부탁할까.";
            if (borrowChoice.choiceA != null)
                borrowChoice.choiceA.nextSceneId = string.Empty;
            if (borrowChoice.choiceB != null)
                borrowChoice.choiceB.nextSceneId = string.Empty;
        }

        ScenarioV3Line noFunds = FindLine("gamble_no_funds_02");
        if (noFunds != null)
        {
            noFunds.text = "계좌 잔액은 0원이다. 더 하려면 누군가에게 돈을 빌려야 한다. 돈을 구할지, 여기서 멈출지 정해야 한다.";
            if (noFunds.choiceA != null)
                noFunds.choiceA.text = "돈을 빌릴 방법을 찾는다";
        }

        ScenarioV3Line lateMorning = FindLine("sys_late_gamble_morning_02");
        if (lateMorning != null)
            lateMorning.text = "벌써 오전 열 시다. 오늘 아침 일정은 이미 놓쳤다.";

        ScenarioV3Line jobIncome = FindLine("d5_job_02");
        if (jobIncome != null)
            jobIncome.text = "오늘 번 오만 원만큼 수리비에 가까워졌다. 하루를 꼬박 일해서 번 돈이라는 게 숫자로 보니 더 또렷했다.";

        ScenarioV3Line jobEvening = FindLine("d5_evening_02");
        if (jobEvening != null)
            jobEvening.text = "몸은 무겁지만 오늘 해야 할 일은 끝냈다. 이제 씻고 쉬자.";

        ScenarioV3Line minjaeReject = FindLine("minjae_loan_rejected_01");
        if (minjaeReject != null)
        {
            minjaeReject.portrait = "minjae_angry";
            minjaeReject.text = "그래. 마음 바뀌면 말해. 난 링크 안 지울 테니까.";
        }
        ScenarioV3Line minjaeRejectThought = FindLine("minjae_loan_rejected_02");
        if (minjaeRejectThought != null)
            minjaeRejectThought.text = "지금은 민재에게 빌리지 않기로 했다. 돈이 없는 건 그대로지만, 당장은 여기서 멈추는 편이 낫겠다.";

        ScenarioV3Line blockThought = FindLine("d14_no_help_messages_03");
        if (blockThought != null)
        {
            blockThought.delivery = "overlay";
            blockThought.text = "민재를 차단하고 도박 링크를 지우면 끝날 일인데, 손가락이 화면 위에서 움직이지 않았다.";
        }

        ScenarioV3Line recoveryMinjae = FindLine("d14_recovery_minjae_02");
        if (recoveryMinjae != null)
        {
            recoveryMinjae.portrait = "minjae_angry";
            recoveryMinjae.text = "됐고, 끊든 말든 네 사정이야. 빌린 오만 원이나 약속한 날짜에 제대로 갚아.";
            recoveryMinjae.enterEffects = string.Empty;
        }
        ScenarioV3Line recoveryMinjaeReply = FindLine("d14_recovery_minjae_03");
        if (recoveryMinjaeReply != null)
            recoveryMinjaeReply.text = "알겠어. 갚는 날짜부터 정해서 알려줄게. 네가 보낸 링크는 이제 열지 않을 거야.";

        PatchSeojunRepaymentScene();
        PatchMinjaeRepaymentScene();
        AddExtendedGamblingScenes();

        HashSet<string> returnToTablet = GetField<HashSet<string>>(database, "returnToTabletScenes");
        returnToTablet?.Remove("d10_seojun_followup");
        returnToTablet?.Remove("d10_minjae_debt");
    }

    private void PatchSeojunRepaymentScene()
    {
        ScenarioV3Scene scene = database.GetScene("d10_seojun_followup");
        ScenarioV3Line line = FindLine("d10_seojun_followup_01");
        if (scene != null)
            scene.condition = "borrowed.seojun=true;debt_owner=seojun;debt>0";
        if (line == null)
            return;

        line.text = "다음 주에 갚는다고 했던 거 기억하지? 급한 건 아닌데 언제쯤 가능한지는 알려줘.";
        if (line.choiceA != null)
        {
            line.choiceA.text = "갚을 수 있는 만큼 먼저 갚는다";
            line.choiceA.effects = string.Empty;
            line.choiceA.nextSceneId = "d10_seojun_repay_router";
        }
        if (line.choiceB != null)
        {
            line.choiceB.text = "조금만 더 기다려 달라고 한다";
            line.choiceB.effects = string.Empty;
            line.choiceB.nextSceneId = "d10_seojun_delay_thought";
        }

        ScenarioV3Line delayThought = FindLine("d10_seojun_followup_02");
        if (delayThought != null)
            delayThought.text = "답장은 금방 쓸 수 있는데, 약속을 또 미룬다는 말을 보내려니 손이 멈췄다.";
        ScenarioV3Line cannotRepay = FindLine("d10_seojun_cannot_repay_01");
        if (cannotRepay != null)
            cannotRepay.text = "갚고 싶지만 지금은 보낼 수 있는 돈이 없다. 결국 조금만 더 기다려 달라고 말해야겠다.";
    }

    private void PatchMinjaeRepaymentScene()
    {
        ScenarioV3Scene scene = database.GetScene("d10_minjae_debt");
        if (scene == null || scene.lines.Count == 0)
            return;

        ScenarioV3Line first = FindLine("d10_minjae_debt_01");
        ScenarioV3Line second = FindLine("d10_minjae_debt_02");
        ScenarioV3Line oldReply = FindLine("d10_minjae_debt_03");
        if (first != null)
            first.text = "주말 알바비 들어오면 내 돈부터 갚을 거지? 더 하든 말든 네 사정인데, 갚을 날짜는 확실히 말해.";
        if (second != null)
        {
            second.text = "지금 보낼 수 있으면 먼저 보내. 아니면 언제 줄 건지 말해.";
            second.choiceA = new ScenarioV3Choice
            {
                id = "d10_minjae_repay_now",
                text = "갚을 수 있는 만큼 먼저 갚는다",
                replyText = string.Empty,
                effects = string.Empty,
                nextSceneId = "d10_minjae_repay_router"
            };
            second.choiceB = new ScenarioV3Choice
            {
                id = "d10_minjae_delay_repay",
                text = "조금만 더 기다려 달라고 한다",
                replyText = string.Empty,
                effects = string.Empty,
                nextSceneId = "d10_minjae_delay_thought"
            };
            second.choiceC = null;
        }
        if (oldReply != null)
            scene.lines.Remove(oldReply);

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repay_router", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repay_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d10_minjae_repaid if last_repayment>0 else d10_minjae_cannot_repay", string.Empty)));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repaid", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repaid_01", 1, "Protagonist", "나", "overlay", string.Empty,
                "민재가 뭐라고 하든 빌린 돈은 정리해야 한다. 지금 보낼 수 있는 돈부터 갚자.",
                string.Empty, "d10_minjae_repaid_message_router")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repaid_message_router", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repaid_message_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d10_minjae_repaid_full_message if debt=0 else d10_minjae_repaid_partial_message",
                string.Empty)));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repaid_full_message", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repaid_full_message_01", 1, "Protagonist", "민재", "message", string.Empty,
                "방금 빌린 돈 전부 보냈어. 확인해.", string.Empty, string.Empty)));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repaid_partial_message", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repaid_partial_message_01", 1, "Protagonist", "민재", "message", string.Empty,
                "지금 가진 돈부터 보냈어. 남은 금액도 날짜 정해서 갚을게.",
                string.Empty, string.Empty)));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_cannot_repay", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_cannot_repay_01", 1, "Protagonist", "나", "overlay", string.Empty,
                "갚고 싶지만 지금은 보낼 수 있는 돈이 없다. 이번 주말 알바비가 들어오면 먼저 갚겠다고 해야겠다.",
                string.Empty, "d10_minjae_delay_message")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_delay_thought", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_delay_thought_01", 1, "Protagonist", "나", "overlay", string.Empty,
                "지금 당장 보낼 돈이 부족하다. 또 미루는 셈이지만, 언제 갚을지는 분명히 말해야겠다.",
                string.Empty, "d10_minjae_delay_message")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_delay_message", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_delay_message_01", 1, "Protagonist", "민재", "message", string.Empty,
                "지금은 보낼 돈이 없어. 주말 알바비가 들어오면 먼저 갚을게.",
                string.Empty, string.Empty)));
    }

    private void AddExtendedGamblingScenes()
    {
        AddOrReplaceScene(CreateScene(
            "gamble_7", "gambling", "1..14", "cinematic", 300,
            CreateLine("gamble_7_01", 1, "Narrator", string.Empty, "cinematic", string.Empty,
                "다시 넣은 돈이 잠깐 불어나더니 이만 원이 계좌로 들어왔다.",
                "clock:add=120|cash:add=20000|temptation:add=1", string.Empty),
            CreateLine("gamble_7_02", 2, "Protagonist", "나", "dialogue", string.Empty,
                "이번에는 됐다. 조금만 더 하면 방금 전 손실도 메울 수 있을 것 같다.",
                string.Empty, string.Empty)));

        AddOrReplaceScene(CreateScene(
            "gamble_8", "gambling", "1..14", "cinematic", 300,
            CreateLine("gamble_8_01", 1, "Narrator", string.Empty, "cinematic", string.Empty,
                "작은 이익을 따라 금액을 키우자 흐름은 바로 뒤집혔다. 계좌에서 사만 원이 빠졌다.",
                "clock:add=120|cash:add=-40000|temptation:add=2", string.Empty),
            CreateLine("gamble_8_02", 2, "Protagonist", "나", "dialogue", string.Empty,
                "이만 원을 벌었다는 기억 때문에 두 배를 잃었다. 그래도 손을 떼기가 쉽지 않다.",
                string.Empty, string.Empty)));

        HashSet<string> returnToTablet = GetField<HashSet<string>>(database, "returnToTabletScenes");
        returnToTablet?.Add("gamble_7");
        returnToTablet?.Add("gamble_8");
    }

    private static ScenarioV3Scene CreateScene(
        string id, string arc, string day, string timeWindow, int priority, params ScenarioV3Line[] lines)
    {
        var scene = new ScenarioV3Scene
        {
            id = id,
            arc = arc,
            day = day,
            timeWindow = timeWindow,
            trigger = id,
            condition = string.Empty,
            priority = priority,
            onceScope = "day",
            purpose = "Scenario V3 final flow hotfix"
        };
        foreach (ScenarioV3Line line in lines)
            scene.lines.Add(line);
        return scene;
    }

    private static ScenarioV3Line CreateLine(
        string id, int sequence, string speaker, string contact, string delivery, string portrait,
        string text, string enterEffects, string autoNext)
    {
        return new ScenarioV3Line
        {
            id = id,
            sequence = sequence,
            speaker = speaker,
            contact = contact,
            delivery = delivery,
            portrait = portrait,
            text = text,
            enterEffects = enterEffects,
            autoNext = autoNext
        };
    }

    private void AddOrReplaceScene(ScenarioV3Scene scene)
    {
        Dictionary<string, ScenarioV3Scene> scenes =
            GetField<Dictionary<string, ScenarioV3Scene>>(database, "scenes");
        if (scenes == null || scene == null)
            return;
        scenes[scene.id] = scene;
    }

    private ScenarioV3Line FindLine(string lineId)
    {
        if (database == null || string.IsNullOrWhiteSpace(lineId))
            return null;
        return database.Scenes.SelectMany(scene => scene.lines)
            .FirstOrDefault(line => string.Equals(line.id, lineId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // App button corrections
    // ---------------------------------------------------------------------

    private void PatchButtons(bool force)
    {
        PatchGamblingLauncher();
        PatchSchoolTravelButtons();
        PatchRewindButton();
        PatchSimpleButtonDebounce();
    }

    private void PatchGamblingLauncher()
    {
        GameObject launcher = FindSceneObject("Gambling Launcher");
        Button button = launcher != null ? launcher.GetComponent<Button>() : null;
        if (button == null || button == gamblingLauncherButton)
            return;

        gamblingLauncherButton = button;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(HandleGamblingLauncher);
    }

    private void HandleGamblingLauncher()
    {
        if (flow == null || director == null || flow.IsGameEnded || !director.IsGamblingAppUnlocked)
            return;
        if (GetField<bool>(flow, "isTransitioning"))
            return;
        if (!string.IsNullOrWhiteSpace(director.ActiveSceneId))
            return;

        string outgoingContact = GetField<string>(director, "pendingOutgoingContact") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(outgoingContact))
        {
            string prompt = string.Equals(outgoingContact, "서연", StringComparison.OrdinalIgnoreCase)
                ? "(서연에게 보내기로 한 메시지부터 정리하는 편이 좋겠다.)"
                : $"({outgoingContact}에게 보내기로 한 메시지부터 정리하는 편이 좋겠다.)";
            flow.V3ShowDialogue("나", prompt, () => flow.V3MarkAppAttention(AppType.Message));
            return;
        }

        if (GetField<bool>(director, "waitingForMessageChoice"))
        {
            flow.V3ShowDialogue("나", "(확인하고 답해야 할 메시지부터 정리하는 편이 좋겠다.)",
                () => flow.V3MarkAppAttention(AppType.Message));
            return;
        }

        if (flow.IsWeekend && !flow.IsJobDone)
        {
            flow.V3ShowDialogue("나", "(카페 출근 시간을 먼저 맞추는 편이 좋겠다.)",
                () => flow.V3MarkAppAttention(AppType.Map));
            return;
        }
        if (!flow.IsWeekend && !flow.IsSchoolDone)
        {
            flow.V3ShowDialogue("나", "(학교 일정을 먼저 챙기는 편이 좋겠다.)",
                () => flow.V3MarkAppAttention(AppType.Map));
            return;
        }
        if (!flow.IsWeekend && flow.V3HasStudyToday && !flow.IsHomeworkDone)
        {
            flow.V3ShowDialogue("나", "(오늘 하기로 한 공부부터 정리하는 편이 좋겠다.)",
                () => flow.V3MarkAppAttention(AppType.Study));
            return;
        }

        int nextSession = GetDirectorInt("counter.gamble_sessions") + 1;
        if ((nextSession == 7 || nextSession == 8) && flow.V3BankCash > 0)
        {
            SetDirectorState("pending.gamble_attention", "false");
            flow.V3SetGamblingAttention(false);
            SetDirectorState("counter.gamble_sessions", nextSession.ToString(CultureInfo.InvariantCulture));
            SetDirectorState("flag.gambling_started", "true");
            appWindow?.CloseCurrentApp();
            InvokePrivate(director, "PlayScene", "gamble_" + nextSession.ToString(CultureInfo.InvariantCulture));
            InvokePrivate(director, "Save");
            return;
        }

        InvokePrivate(flow, "StartScenarioGambling");
    }

    private void PatchSchoolTravelButtons()
    {
        foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (button == null || !button.gameObject.scene.IsValid())
                continue;
            int id = button.GetInstanceID();
            if (patchedSchoolButtons.Contains(id))
                continue;

            string label = button.GetComponentInChildren<TMP_Text>(true)?.text ?? string.Empty;
            bool strongSchoolName = button.name.IndexOf("School", StringComparison.OrdinalIgnoreCase) >= 0;
            bool looksLikeSchool = strongSchoolName || label.Trim() == "학교" || label.Contains("학교로");
            // Runtime-created map buttons are not always nested under an object literally named Map.
            // An explicit School object name is sufficient; text-only matches still require a map ancestor.
            if (!looksLikeSchool || (!strongSchoolName && !HasMapAncestor(button.transform)))
                continue;

            patchedSchoolButtons.Add(id);

            // Inspector-persistent UnityEvent listeners cannot be removed reliably at runtime.
            // Put a transparent child button over the original map button so the old TravelTo call
            // never fires before the player closes the late-arrival dialogue.
            Transform existingProxy = button.transform.Find("V17 School Travel Proxy");
            if (existingProxy != null)
                continue;

            GameObject proxyObject = new GameObject("V17 School Travel Proxy", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(Button));
            proxyObject.layer = button.gameObject.layer;
            proxyObject.transform.SetParent(button.transform, false);
            Stretch(proxyObject.GetComponent<RectTransform>());
            Image proxyImage = proxyObject.GetComponent<Image>();
            proxyImage.color = new Color(1f, 1f, 1f, 0.001f);
            proxyImage.raycastTarget = true;
            Button proxyButton = proxyObject.GetComponent<Button>();
            proxyButton.transition = Selectable.Transition.None;
            proxyButton.targetGraphic = proxyImage;
            proxyButton.onClick.AddListener(HandleSchoolTravelButton);
            patchedSchoolButtons.Add(proxyButton.GetInstanceID());
        }
    }

    private static bool HasMapAncestor(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            string name = current.name ?? string.Empty;
            if (name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("지도"))
                return true;
            current = current.parent;
        }
        return false;
    }

    private void HandleSchoolTravelButton()
    {
        if (flow == null || flow.IsGameEnded || GetField<bool>(flow, "isTransitioning"))
            return;

        int travelHours = flow.GetTravelHours("학교");
        int arrivalHour = flow.CurrentHour + travelHours;
        if (!flow.IsWeekend && !flow.IsSchoolDone && flow.CurrentHour < 16 && arrivalHour > 10)
        {
            appWindow?.CloseCurrentApp();
            flow.V3ShowDialogue("나", "(늦었지만 지금이라도 학교에 가는 편이 낫겠다.)",
                BeginLateSchoolTravelDirectly);
            return;
        }

        flow.TravelTo("학교");
    }

    private void BeginLateSchoolTravelDirectly()
    {
        MethodInfo method = typeof(GameFlowManager).GetMethod("TravelTransition", PrivateInstance);
        AudioClip clip = GetField<AudioClip>(flow, "schoolArrivalClip");
        if (method == null)
        {
            Debug.LogError("[Scenario V3 Final Fix] 학교 이동 코루틴을 찾지 못했습니다.");
            return;
        }

        object result = method.Invoke(flow, new object[] { "학교", flow.GetTravelHours("학교"), clip, 0.28f });
        if (result is IEnumerator routine)
            flow.StartCoroutine(routine);
    }

    private void PatchRewindButton()
    {
        GameObject target = FindSceneObject("Rewind Button");
        Button button = target != null ? target.GetComponent<Button>() : null;
        if (button == null || button == rewindButton)
            return;

        rewindButton = button;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(RewindWithHistory);
    }

    private void PatchSimpleButtonDebounce()
    {
        string[] names = { "Continue", "Narration Continue Button", "Choice A", "Choice B", "Choice C" };
        foreach (string name in names)
        {
            GameObject target = FindSceneObject(name);
            Button button = target != null ? target.GetComponent<Button>() : null;
            if (button == null || !patchedDebounceButtons.Add(button.GetInstanceID()))
                continue;
            button.onClick.AddListener(() => StartCoroutine(DebounceButton(button)));
        }
    }

    private static IEnumerator DebounceButton(Button button)
    {
        if (button == null)
            yield break;
        button.interactable = false;
        yield return new WaitForSecondsRealtime(0.12f);
        if (button != null)
            button.interactable = true;
    }

    // ---------------------------------------------------------------------
    // Borrowing: normal sleep -> next-day tablet choice
    // ---------------------------------------------------------------------

    private void TrackExplicitBorrowRequest()
    {
        bool pending = string.Equals(GetDirectorState("pending.borrow_menu"), "true", StringComparison.OrdinalIgnoreCase);
        bool deferred = string.Equals(GetDirectorState("flag.borrow_deferred"), "true", StringComparison.OrdinalIgnoreCase);
        if (pending && deferred)
            explicitBorrowPending = true;

        // Old source contains an emergency flag that used to force the day forward.
        // The data patch no longer sets it, and this guard prevents stale values from an older save/run.
        if (GetField<bool>(director, "pendingBorrowMorningAdvance"))
            SetField(director, "pendingBorrowMorningAdvance", false);
    }

    private void KeepMinjaeLoanOfferRepeatableUntilAccepted()
    {
        // 민재에게 실제로 돈을 받은 순간에만 그의 차용 이벤트를 소진한다.
        // 제안을 거절한 동안에는 같은 자금 부족 조건에서 다시 제안받을 수 있어야 한다.
        if (string.Equals(GetDirectorState("borrowed.minjae"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        HashSet<string> seen = GetField<HashSet<string>>(director, "seenScenes");
        if (seen == null || !seen.Any(key =>
                key.StartsWith("minjae_loan_rejected", StringComparison.OrdinalIgnoreCase)))
            return;

        seen.RemoveWhere(key =>
            key.Equals("minjae_loan_offer", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("minjae_loan_offer@", StringComparison.OrdinalIgnoreCase));
    }

    private void HandleDayChangeAndDialogueLog()
    {
        List<string> current = GetDialogueLog();
        if (!restoringCheckpoint)
        {
            if (flow.CurrentDay != lastDay)
            {
                borrowOverlayShownForCurrentDay = false;

                // Director used to clear the VN history every morning. Keep everything unless this is
                // an explicit branch rewind or a brand-new scene load.
                if (current.Count < preservedDialogueLog.Count)
                {
                    // A new day's first line can be appended immediately after Director clears the log.
                    // Keep those new entries while restoring every line from previous days.
                    List<string> newDayEntries = new List<string>(current);
                    current.Clear();
                    current.AddRange(preservedDialogueLog);
                    current.AddRange(newDayEntries);
                }

                // A real gambling all-nighter used to create a borrow menu just because cash was zero.
                // Preserve it only when the player actually selected borrowing the previous night.
                if (!explicitBorrowPending &&
                    string.Equals(GetDirectorState("flag.late_wake_today"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    SetDirectorState("pending.borrow_menu", "false");
                }

                lastDay = flow.CurrentDay;
            }

            if (current.Count >= preservedDialogueLog.Count)
                preservedDialogueLog = new List<string>(current);
        }
    }

    private void TryShowDeferredBorrowChoice()
    {
        if (borrowOverlayShownForCurrentDay || choiceOverlay == null || choiceOverlay.activeSelf)
            return;

        // 잔액이 0원이라는 사실만으로 차용 화면을 띄우지 않는다. 이 플래그는
        // 플레이어가 전날 실제로 '돈을 빌린다'를 고른 경우에만 설정된다.
        if (!explicitBorrowPending)
            return;
        if (!IsDirectorIdle() || flow.CurrentLocation != "집")
            return;

        ScenarioV3Scene scene = database.GetScene("borrow_choice");
        if (scene == null || scene.lines.Count == 0)
            return;

        borrowOverlayShownForCurrentDay = true;
        explicitBorrowPending = false;
        SetDirectorState("pending.borrow_menu", "false");
        SetDirectorState("flag.borrow_deferred", "false");
        SetField(director, "activeScene", scene);
        SetField(director, "activeLineIndex", 0);
        appWindow?.CloseCurrentApp();

        ScenarioV3Line line = scene.lines[0];
        ShowChoiceOverlay(line, "어젯밤 미뤄 둔 연락을 지금 정해야 한다. 누구에게 부탁할까.",
            SpeakerType.Unknown);
        InvokePrivate(director, "Save");
    }

    private bool IsDirectorIdle()
    {
        if (!string.IsNullOrWhiteSpace(director.ActiveSceneId))
            return false;
        if (GetField<bool>(director, "sceneTransitionInProgress") ||
            GetField<bool>(director, "waitingForIncomingMessageRead") ||
            GetField<bool>(director, "waitingForMessageSceneClose"))
            return false;
        Queue<ScenarioV3Scene> queue = GetField<Queue<ScenarioV3Scene>>(director, "sceneQueue");
        return queue == null || queue.Count == 0;
    }

    // ---------------------------------------------------------------------
    // Repayment choices displayed as VN dialogue over the live message app
    // ---------------------------------------------------------------------

    private void TryReplaceDebtChatChoicesWithDialogue()
    {
        if (choiceOverlay == null || choiceOverlay.activeSelf || !GetField<bool>(director, "waitingForMessageChoice"))
            return;

        ScenarioV3Scene waitingScene = GetField<ScenarioV3Scene>(director, "waitingMessageScene");
        int waitingIndex = GetField<int>(director, "waitingMessageLineIndex");
        if (waitingScene == null || waitingIndex < 0 || waitingIndex >= waitingScene.lines.Count)
            return;

        ScenarioV3Line line = waitingScene.lines[waitingIndex];
        bool seojun = string.Equals(line.id, "d10_seojun_followup_01", StringComparison.OrdinalIgnoreCase);
        bool minjae = string.Equals(line.id, "d10_minjae_debt_02", StringComparison.OrdinalIgnoreCase);
        if (!seojun && !minjae)
            return;

        SpeakerType speaker = seojun ? SpeakerType.Joonho : SpeakerType.Friend;
        dialogue.DismissEventChoices(speaker);
        if (!dialogue.IsConversationOpen(speaker))
            return;

        string body = seojun
            ? "서준에게 어떻게 답할까."
            : "민재에게 어떻게 답할까.";
        ShowChoiceOverlay(line, body, speaker);
    }

    private void ShowChoiceOverlay(ScenarioV3Line line, string body, SpeakerType messageSpeaker)
    {
        if (choiceOverlay == null || line == null)
            return;

        List<ScenarioV3Choice> choices = line.Choices.Where(IsChoiceCurrentlyAvailable).ToList();
        if (choices.Count == 0)
            return;

        choiceOverlayLine = line;
        choiceOverlayMessageSpeaker = messageSpeaker;
        choiceOverlayBusy = false;
        choiceOverlaySpeaker.text = "나";
        choiceOverlayBody.text = FormatThought(body);

        for (int index = 0; index < choiceOverlayButtons.Count; index++)
        {
            Button button = choiceOverlayButtons[index];
            bool visible = index < choices.Count;
            button.gameObject.SetActive(visible);
            button.onClick.RemoveAllListeners();
            if (!visible)
                continue;

            ScenarioV3Choice captured = choices[index];
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = captured.text;
            button.onClick.AddListener(() => ResolveOverlayChoice(captured));
        }

        choiceOverlay.SetActive(true);
        choiceOverlay.transform.SetAsLastSibling();
    }

    private bool IsChoiceCurrentlyAvailable(ScenarioV3Choice choice)
    {
        if (choice == null || string.IsNullOrWhiteSpace(choice.id))
            return false;
        if (string.Equals(choice.id, "borrow_mom", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(GetDirectorState("borrowed.mom"), "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(choice.id, "borrow_friend", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(GetDirectorState("borrowed.seojun"), "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(choice.id, "minjae_loan_accept", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(GetDirectorState("borrowed.minjae"), "true", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private void ResolveOverlayChoice(ScenarioV3Choice choice)
    {
        if (choiceOverlayBusy || choice == null || choiceOverlayLine == null)
            return;
        choiceOverlayBusy = true;

        ScenarioV3Line line = choiceOverlayLine;
        string oldDelivery = line.delivery;
        string oldEffects = choice.effects;
        int oldLogCount = GetDialogueLog().Count;

        bool seojunRepay = string.Equals(choice.id, "d10_repay_now", StringComparison.OrdinalIgnoreCase);
        bool seojunDelay = string.Equals(choice.id, "d10_delay_repay", StringComparison.OrdinalIgnoreCase);
        bool minjaeRepay = string.Equals(choice.id, "d10_minjae_repay_now", StringComparison.OrdinalIgnoreCase);
        bool minjaeDelay = string.Equals(choice.id, "d10_minjae_delay_repay", StringComparison.OrdinalIgnoreCase);

        if (seojunRepay || minjaeRepay)
        {
            string owner = seojunRepay ? "서준" : "민재";
            int repaid = flow.V3RepayAvailableDebt(owner + "에게 빌린 돈 상환");
            SetDirectorState("last_repayment", repaid.ToString(CultureInfo.InvariantCulture));
            if (flow.CurrentDebt <= 0)
                SetDirectorState("debt_owner", "none");
            if (seojunRepay)
                AddDirectorInt("relation.seojun", 1);
            choice.effects = string.Empty;
        }
        else if (seojunDelay || minjaeDelay)
        {
            // Preserve Seojun's existing trust consequence. Minjae is an antagonist and has no
            // newly invented relationship reward/penalty attached to this added repayment choice.
            if (seojunDelay)
                AddDirectorInt("relation.seojun", -1);
            choice.effects = string.Empty;
        }

        // Do not log this decision as if it were a sent chat message, and do not wait for the user
        // to leave the message app before continuing to the thought/outgoing-message scene.
        line.delivery = "dialogue_choice_overlay";
        if (choiceOverlayMessageSpeaker != SpeakerType.Unknown)
            dialogue.DismissEventChoices(choiceOverlayMessageSpeaker);
        choiceOverlay.SetActive(false);

        try
        {
            director.HandleChoice(choice.id);
        }
        finally
        {
            line.delivery = oldDelivery;
            choice.effects = oldEffects;
            List<string> log = GetDialogueLog();
            while (log.Count > oldLogCount)
                log.RemoveAt(log.Count - 1);
            preservedDialogueLog = new List<string>(log);
            choiceOverlayLine = null;
            choiceOverlayMessageSpeaker = SpeakerType.Unknown;
            choiceOverlayBusy = false;
        }
    }

    private void CreateChoiceOverlay()
    {
        if (choiceOverlay != null)
            return;

        Canvas canvas = Resources.FindObjectsOfTypeAll<Canvas>()
            .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid() &&
                                         candidate.transform.parent == null)
            ?? FindAnyObjectByType<Canvas>();
        if (canvas == null)
            return;

        TMP_FontAsset font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
            .FirstOrDefault(candidate => candidate != null && candidate.name.Contains("NotoSansKR"))
            ?? FindAnyObjectByType<TMP_Text>()?.font;

        choiceOverlay = CreatePanel("Scenario V3 In-App Dialogue Choice", canvas.transform,
            new Color(0f, 0f, 0f, 0.12f));
        Stretch(choiceOverlay.GetComponent<RectTransform>());

        GameObject box = CreatePanel("Choice Dialogue Box", choiceOverlay.transform,
            new Color(0.025f, 0.045f, 0.075f, 0.985f));
        RectTransform boxRect = box.GetComponent<RectTransform>();
        boxRect.anchorMin = new Vector2(0.12f, 0.08f);
        boxRect.anchorMax = new Vector2(0.88f, 0.43f);
        boxRect.offsetMin = boxRect.offsetMax = Vector2.zero;

        choiceOverlaySpeaker = CreateText("Speaker", box.transform, font, 27f, FontStyles.Bold,
            new Color(0.5f, 0.76f, 1f));
        RectTransform speakerRect = choiceOverlaySpeaker.rectTransform;
        speakerRect.anchorMin = new Vector2(0f, 1f);
        speakerRect.anchorMax = new Vector2(0.35f, 1f);
        speakerRect.pivot = new Vector2(0f, 1f);
        speakerRect.anchoredPosition = new Vector2(34f, -22f);
        speakerRect.sizeDelta = new Vector2(360f, 42f);

        choiceOverlayBody = CreateText("Body", box.transform, font, 31f, FontStyles.Bold, Color.white);
        choiceOverlayBody.alignment = TextAlignmentOptions.TopLeft;
        choiceOverlayBody.textWrappingMode = TextWrappingModes.Normal;
        RectTransform bodyRect = choiceOverlayBody.rectTransform;
        bodyRect.anchorMin = new Vector2(0f, 0.48f);
        bodyRect.anchorMax = new Vector2(1f, 1f);
        bodyRect.offsetMin = new Vector2(34f, 0f);
        bodyRect.offsetMax = new Vector2(-34f, -72f);

        for (int index = 0; index < 3; index++)
        {
            Button button = CreateButton("Dialogue Choice " + (index + 1), box.transform, font,
                string.Empty, index == 1 ? new Color(0.46f, 0.25f, 0.31f) : new Color(0.15f, 0.42f, 0.72f));
            RectTransform rect = button.GetComponent<RectTransform>();
            float width = 0.29f;
            float center = 0.18f + 0.32f * index;
            rect.anchorMin = new Vector2(center - width * 0.5f, 0.08f);
            rect.anchorMax = new Vector2(center + width * 0.5f, 0.34f);
            rect.offsetMin = new Vector2(8f, 0f);
            rect.offsetMax = new Vector2(-8f, 0f);
            choiceOverlayButtons.Add(button);
        }

        choiceOverlay.SetActive(false);
    }

    // ---------------------------------------------------------------------
    // VN history clipping and scrolling
    // ---------------------------------------------------------------------

    private void PatchHistoryViewport()
    {
        if (director == null)
            return;

        GameObject historyPanel = GetField<GameObject>(director, "historyPanel");
        TMP_Text historyText = GetField<TMP_Text>(director, "historyText");
        if (historyPanel == null || historyText == null)
            return;

        Transform viewportTransform = historyPanel.transform.Find("History Viewport");
        if (viewportTransform == null)
            return;

        RectTransform viewport = viewportTransform as RectTransform;
        Image viewportImage = viewportTransform.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewportTransform.gameObject.AddComponent<Image>();
        if (viewportImage.color.a <= 0.001f)
            viewportImage.color = new Color(0.04f, 0.065f, 0.1f, 0.96f);
        viewportImage.raycastTarget = true;

        Mask stencil = viewportTransform.GetComponent<Mask>();
        if (stencil != null)
            stencil.enabled = false;
        RectMask2D rectMask = viewportTransform.GetComponent<RectMask2D>();
        if (rectMask == null)
            rectMask = viewportTransform.gameObject.AddComponent<RectMask2D>();
        rectMask.enabled = true;
        rectMask.padding = Vector4.zero;

        Transform contentTransform = viewportTransform.Find("History Content");
        if (contentTransform == null)
            return;
        RectTransform content = contentTransform as RectTransform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;

        historyText.maskable = true;
        historyText.raycastTarget = false;
        historyText.textWrappingMode = TextWrappingModes.Normal;
        historyText.overflowMode = TextOverflowModes.Overflow;
        historyText.alignment = TextAlignmentOptions.TopLeft;

        float viewportWidth = Mathf.Max(480f, viewport.rect.width);
        float viewportHeight = Mathf.Max(320f, viewport.rect.height);
        const float horizontalPadding = 32f;
        const float verticalPadding = 24f;
        float textWidth = Mathf.Max(320f, viewportWidth - horizontalPadding * 2f);
        float preferredHeight = Mathf.Max(80f,
            historyText.GetPreferredValues(historyText.text ?? string.Empty, textWidth, 0f).y);
        float contentHeight = Mathf.Max(viewportHeight, preferredHeight + verticalPadding * 2f);
        content.sizeDelta = new Vector2(0f, contentHeight);

        RectTransform textRect = historyText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 1f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = new Vector2(0f, -verticalPadding);
        textRect.sizeDelta = new Vector2(-horizontalPadding * 2f, preferredHeight);

        ScrollRect scroll = viewportTransform.GetComponent<ScrollRect>();
        if (scroll == null)
            scroll = viewportTransform.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 45f;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;

        SetField(director, "historyViewportRect", viewport);
        SetField(director, "historyContentRect", content);
        SetField(director, "historyScroll", scroll);

        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    // ---------------------------------------------------------------------
    // Branch rewind: preserve history before the checkpoint, remove only future history
    // ---------------------------------------------------------------------

    private void CaptureNewCheckpointSnapshots()
    {
        List<ScenarioV3CheckpointData> checkpoints =
            GetField<List<ScenarioV3CheckpointData>>(director, "checkpoints");
        if (checkpoints == null)
            return;

        if (lastCheckpointCount == checkpoints.Count && checkpoints.All(checkpointSnapshots.ContainsKey))
            return;

        foreach (ScenarioV3CheckpointData checkpoint in checkpoints)
        {
            if (checkpoint == null || checkpointSnapshots.ContainsKey(checkpoint))
                continue;

            List<string> novel = CopyDialogueLog();
            if (string.Equals(director.ActiveLineId, checkpoint.lineId, StringComparison.OrdinalIgnoreCase) && novel.Count > 0)
                novel.RemoveAt(novel.Count - 1);

            checkpointSnapshots[checkpoint] = new CheckpointRuntimeSnapshot
            {
                chat = CaptureChatSnapshot(),
                novelLog = novel,
                deliveredOutgoing = CopyStringSet(director, "deliveredOutgoingLineIds"),
                deliveredIncoming = CopyStringSet(director, "deliveredIncomingLineIds"),
                submittedActions = CopyStringSet(dialogue, "submittedScenarioActions")
            };
        }
        lastCheckpointCount = checkpoints.Count;
    }

    private void RewindWithHistory()
    {
        if (director == null || restoringCheckpoint)
            return;

        ScenarioV3CheckpointData target = InvokePrivate(director, "FindRewindCheckpoint") as ScenarioV3CheckpointData;
        if (target == null)
            return;

        checkpointSnapshots.TryGetValue(target, out CheckpointRuntimeSnapshot snapshot);
        restoringCheckpoint = true;
        director.RestorePreviousCheckpoint();
        StartCoroutine(RestoreHistoryAfterRewind(snapshot));
    }

    private IEnumerator RestoreHistoryAfterRewind(CheckpointRuntimeSnapshot snapshot)
    {
        yield return null;

        Coroutine incoming = GetField<Coroutine>(director, "incomingMessageCoroutine");
        if (incoming != null)
        {
            director.StopCoroutine(incoming);
            SetField(director, "incomingMessageCoroutine", null);
        }

        if (snapshot != null)
        {
            RestoreChatSnapshot(snapshot.chat);
            ReplaceDialogueLog(snapshot.novelLog);
            ReplaceStringSet(director, "deliveredOutgoingLineIds", snapshot.deliveredOutgoing);
            ReplaceStringSet(director, "deliveredIncomingLineIds", snapshot.deliveredIncoming);
            ReplaceStringSet(dialogue, "submittedScenarioActions", snapshot.submittedActions);
            preservedDialogueLog = new List<string>(snapshot.novelLog ?? new List<string>());
        }

        notifications?.Clear();
        choiceOverlay?.SetActive(false);
        choiceOverlayLine = null;
        explicitBorrowPending = string.Equals(GetDirectorState("pending.borrow_menu"), "true",
            StringComparison.OrdinalIgnoreCase);
        borrowOverlayShownForCurrentDay = false;
        lastDay = flow.CurrentDay;
        restoringCheckpoint = false;
        ApplyExactAttentionDots();
    }

    private ChatRuntimeSnapshot CaptureChatSnapshot()
    {
        var snapshot = new ChatRuntimeSnapshot
        {
            currentSpeaker = GetField<SpeakerType>(dialogue, "currentSpeaker"),
            mostRecentSpeaker = GetField<SpeakerType>(dialogue, "mostRecentSpeaker"),
            preferredSpeaker = GetField<SpeakerType>(dialogue, "preferredSpeaker"),
            dialogueOpen = dialogue.IsDialogueOpen
        };

        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null)
            return snapshot;

        foreach (KeyValuePair<SpeakerType, ChatChannel> pair in channels)
        {
            ChatChannel channel = pair.Value;
            if (channel == null)
                continue;
            var channelSnapshot = new ChannelRuntimeSnapshot
            {
                speaker = pair.Key,
                speakerName = channel.speakerName,
                lastMessage = channel.lastMessage,
                unreadCount = channel.unreadCount,
                renderedReceivedCount = channel.renderedReceivedCount,
                receivedMessages = new List<string>(channel.receivedMessages),
                messages = channel.messageHistory
                    .Where(entry => entry != null)
                    .Select(entry => new MessageRuntimeSnapshot { isPlayer = entry.isPlayer, text = entry.text })
                    .ToList(),
                eventChoices = CloneChoices(channel.eventChoices),
                pendingChoiceSets = channel.pendingChoiceSets.Select(CloneChoices).ToList()
            };
            snapshot.channels.Add(channelSnapshot);
        }

        List<SpeakerType> order = GetField<List<SpeakerType>>(dialogue, "contactOrder");
        if (order != null)
            snapshot.contactOrder.AddRange(order);
        return snapshot;
    }

    private void RestoreChatSnapshot(ChatRuntimeSnapshot snapshot)
    {
        if (dialogue == null || snapshot == null)
            return;

        dialogue.ResetScenarioConversations();
        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null)
            return;

        channels.Clear();
        foreach (ChannelRuntimeSnapshot saved in snapshot.channels)
        {
            var channel = new ChatChannel
            {
                speakerType = saved.speaker,
                speakerName = saved.speakerName,
                lastMessage = saved.lastMessage,
                unreadCount = saved.unreadCount,
                renderedReceivedCount = saved.renderedReceivedCount,
                receivedMessages = new List<string>(saved.receivedMessages),
                messageHistory = saved.messages.Select(message => new ChatMessageEntry
                {
                    isPlayer = message.isPlayer,
                    text = message.text
                }).ToList(),
                eventChoices = CloneChoices(saved.eventChoices),
                pendingChoiceSets = new Queue<List<Choice>>(saved.pendingChoiceSets.Select(CloneChoices))
            };
            channels[saved.speaker] = channel;
        }

        if (!channels.ContainsKey(SpeakerType.Friend))
            channels[SpeakerType.Friend] = new ChatChannel { speakerType = SpeakerType.Friend, speakerName = "민재" };
        if (!channels.ContainsKey(SpeakerType.Mom))
            channels[SpeakerType.Mom] = new ChatChannel { speakerType = SpeakerType.Mom, speakerName = "엄마" };

        Dictionary<SpeakerType, ProfileSlot> profileSlots =
            GetField<Dictionary<SpeakerType, ProfileSlot>>(dialogue, "profileSlotsBySpeaker");
        if (profileSlots != null)
        {
            foreach (KeyValuePair<SpeakerType, ProfileSlot> pair in profileSlots)
                if (pair.Value != null)
                    pair.Value.gameObject.SetActive(channels.ContainsKey(pair.Key));
        }

        foreach (ChatChannel channel in channels.Values)
        {
            dialogue.EnsureContact(channel.speakerType, channel.speakerName);
            if (profileSlots != null && profileSlots.TryGetValue(channel.speakerType, out ProfileSlot slot) && slot != null)
                slot.gameObject.SetActive(true);
        }

        SetField(dialogue, "currentSpeaker", snapshot.currentSpeaker);
        SetField(dialogue, "mostRecentSpeaker", snapshot.mostRecentSpeaker);
        SetField(dialogue, "preferredSpeaker", snapshot.preferredSpeaker);
        List<SpeakerType> contactOrder = GetField<List<SpeakerType>>(dialogue, "contactOrder");
        if (contactOrder != null)
        {
            contactOrder.Clear();
            contactOrder.AddRange(snapshot.contactOrder.Where(channels.ContainsKey));
            foreach (SpeakerType speaker in channels.Keys)
                if (!contactOrder.Contains(speaker))
                    contactOrder.Add(speaker);
        }
        dialogue.UpdateAllProfileUI();
    }

    private static List<Choice> CloneChoices(IEnumerable<Choice> source)
    {
        if (source == null)
            return new List<Choice>();
        return source.Where(choice => choice != null).Select(choice => new Choice
        {
            choiceText = choice.choiceText,
            replyText = choice.replyText,
            nextDialogueID = choice.nextDialogueID,
            riskScoreChange = choice.riskScoreChange,
            openApp = choice.openApp,
            targetApp = choice.targetApp,
            action = choice.action,
            scenarioAction = choice.scenarioAction
        }).ToList();
    }

    // ---------------------------------------------------------------------
    // Exact attention dots
    // ---------------------------------------------------------------------

    private void ApplyExactAttentionDots()
    {
        Dictionary<AppType, GameObject> dots =
            GetField<Dictionary<AppType, GameObject>>(flow, "appAttentionDots");
        if (dots == null)
            return;

        bool transitioning = GetField<bool>(flow, "isTransitioning");
        bool sleepDone = GetField<bool>(flow, "sleepDone");
        foreach (KeyValuePair<AppType, GameObject> pair in dots)
        {
            if (pair.Value == null)
                continue;

            bool visible;
            switch (pair.Key)
            {
                case AppType.Browser:
                    visible = director.IsGamblingAppUnlocked && !flow.IsGameEnded;
                    break;
                case AppType.Message:
                    visible = !flow.IsGameEnded &&
                              (director.HasPendingMessageAction || dialogue.TotalUnreadCount > 0 ||
                               !string.Equals(GetDirectorState("pending.borrow_target"), "none",
                                   StringComparison.OrdinalIgnoreCase) || HasChatActionChoices());
                    break;
                case AppType.Sleep:
                    visible = !flow.IsGameEnded && !transitioning && !sleepDone && flow.CanSleepNow;
                    break;
                case AppType.Map:
                    visible = !flow.IsGameEnded && !transitioning && CanActuallyMoveNow();
                    break;
                case AppType.Study:
                    visible = !flow.IsGameEnded && !flow.IsWeekend && flow.IsSchoolDone &&
                              flow.V3HasStudyToday && !flow.IsHomeworkDone;
                    break;
                default:
                    visible = false;
                    break;
            }
            pair.Value.SetActive(visible);
        }
    }

    private bool CanActuallyMoveNow()
    {
        if (!string.Equals(flow.CurrentLocation, "집", StringComparison.Ordinal))
            return true;

        int arrival = flow.CurrentHour + flow.GetTravelHours(flow.IsWeekend ? "카페" : "학교");
        if (flow.IsWeekend)
            return !flow.IsJobDone && arrival == 8;
        return !flow.IsSchoolDone && arrival >= 8 && flow.CurrentHour < 16;
    }

    private bool HasChatActionChoices()
    {
        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null)
            return false;

        return channels.Values.Any(channel => channel != null &&
            ((channel.eventChoices != null && channel.eventChoices.Count > 0) ||
             (channel.pendingChoiceSets != null && channel.pendingChoiceSets.Count > 0)));
    }

    // ---------------------------------------------------------------------
    // Reflection/state helpers
    // ---------------------------------------------------------------------

    private string GetDirectorState(string key)
    {
        return director != null ? director.GetState(key) : "0";
    }

    private void SetDirectorState(string key, string value)
    {
        Dictionary<string, string> state = GetField<Dictionary<string, string>>(director, "state");
        if (state == null)
            return;
        state[key] = value;
        if (key.StartsWith("schedule.", StringComparison.OrdinalIgnoreCase))
            flow.V3SetSchedule(key.Substring("schedule.".Length), value);
    }

    private int GetDirectorInt(string key)
    {
        return int.TryParse(GetDirectorState(key), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value) ? value : 0;
    }

    private void AddDirectorInt(string key, int delta)
    {
        SetDirectorState(key, (GetDirectorInt(key) + delta).ToString(CultureInfo.InvariantCulture));
    }

    private List<string> GetDialogueLog()
    {
        return GetField<List<string>>(director, "dialogueLog") ?? new List<string>();
    }

    private List<string> CopyDialogueLog()
    {
        return new List<string>(GetDialogueLog());
    }

    private void ReplaceDialogueLog(IEnumerable<string> source)
    {
        List<string> log = GetDialogueLog();
        log.Clear();
        if (source != null)
            log.AddRange(source);
    }

    private static List<string> CopyStringSet(object target, string field)
    {
        HashSet<string> set = GetField<HashSet<string>>(target, field);
        return set == null ? new List<string>() : set.ToList();
    }

    private static void ReplaceStringSet(object target, string field, IEnumerable<string> source)
    {
        HashSet<string> set = GetField<HashSet<string>>(target, field);
        if (set == null)
            return;
        set.Clear();
        if (source != null)
            foreach (string value in source)
                set.Add(value);
    }

    private static T GetField<T>(object target, string name)
    {
        if (target == null)
            return default;
        FieldInfo field = target.GetType().GetField(name, PrivateInstance);
        if (field == null)
            return default;
        object value = field.GetValue(target);
        return value is T typed ? typed : default;
    }

    private static void SetField(object target, string name, object value)
    {
        if (target == null)
            return;
        FieldInfo field = target.GetType().GetField(name, PrivateInstance);
        field?.SetValue(target, value);
    }

    private static object InvokePrivate(object target, string methodName, params object[] args)
    {
        if (target == null)
            return null;
        Type[] argumentTypes = args == null
            ? Type.EmptyTypes
            : args.Select(argument => argument?.GetType() ?? typeof(object)).ToArray();
        MethodInfo method = target.GetType().GetMethods(PrivateInstance)
            .FirstOrDefault(candidate => candidate.Name == methodName &&
                                         candidate.GetParameters().Length == argumentTypes.Length);
        return method?.Invoke(target, args);
    }

    private static string FormatThought(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        while (trimmed.StartsWith("(", StringComparison.Ordinal) &&
               trimmed.EndsWith(")", StringComparison.Ordinal) && trimmed.Length > 1)
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        return "(" + trimmed + ")";
    }

    private static GameObject FindSceneObject(string objectName)
    {
        foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            if (candidate != null && candidate.scene.IsValid() && candidate.name == objectName)
                return candidate;
        return null;
    }

    private static GameObject CreatePanel(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        Image image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = true;
        return go;
    }

    private static TMP_Text CreateText(string name, Transform parent, TMP_FontAsset font,
        float size, FontStyles style, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        TMP_Text text = go.GetComponent<TMP_Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style | FontStyles.Bold;
        text.color = color;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }

    private static Button CreateButton(string name, Transform parent, TMP_FontAsset font,
        string label, Color color)
    {
        GameObject go = CreatePanel(name, parent, color);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        TMP_Text text = CreateText("Label", go.transform, font, 22f, FontStyles.Bold, Color.white);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        Stretch(text.rectTransform);
        return button;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    [Serializable]
    private sealed class MessageRuntimeSnapshot
    {
        public bool isPlayer;
        public string text;
    }

    [Serializable]
    private sealed class ChannelRuntimeSnapshot
    {
        public SpeakerType speaker;
        public string speakerName;
        public string lastMessage;
        public int unreadCount;
        public int renderedReceivedCount;
        public List<string> receivedMessages = new List<string>();
        public List<MessageRuntimeSnapshot> messages = new List<MessageRuntimeSnapshot>();
        public List<Choice> eventChoices = new List<Choice>();
        public List<List<Choice>> pendingChoiceSets = new List<List<Choice>>();
    }

    [Serializable]
    private sealed class ChatRuntimeSnapshot
    {
        public SpeakerType currentSpeaker;
        public SpeakerType mostRecentSpeaker;
        public SpeakerType preferredSpeaker;
        public bool dialogueOpen;
        public List<SpeakerType> contactOrder = new List<SpeakerType>();
        public List<ChannelRuntimeSnapshot> channels = new List<ChannelRuntimeSnapshot>();
    }

    private sealed class CheckpointRuntimeSnapshot
    {
        public ChatRuntimeSnapshot chat;
        public List<string> novelLog = new List<string>();
        public List<string> deliveredOutgoing = new List<string>();
        public List<string> deliveredIncoming = new List<string>();
        public List<string> submittedActions = new List<string>();
    }
}
