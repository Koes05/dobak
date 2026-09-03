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
    private const string PatchVersion = "V23.2-PreOpenMapGate";

    private GameFlowManager flow;
    private ScenarioV3Director director;
    private DialogueManager dialogue;
    private NotificationManager notifications;
    private AppWindow appWindow;
    private ScenarioV3Database database;

    private readonly Dictionary<ScenarioV3CheckpointData, CheckpointRuntimeSnapshot> checkpointSnapshots =
        new Dictionary<ScenarioV3CheckpointData, CheckpointRuntimeSnapshot>();
    private readonly HashSet<Button> patchedSchoolButtons = new HashSet<Button>();
    private readonly HashSet<Button> patchedMapLauncherButtons = new HashSet<Button>();
    private readonly HashSet<Button> patchedDebounceButtons = new HashSet<Button>();

    private GameObject choiceOverlay;
    private TMP_Text choiceOverlaySpeaker;
    private TMP_Text choiceOverlayBody;
    private readonly List<Button> choiceOverlayButtons = new List<Button>();
    private ScenarioV3Line choiceOverlayLine;
    private SpeakerType choiceOverlayMessageSpeaker = SpeakerType.Unknown;
    private bool choiceOverlayBusy;
    private bool manualChoiceMode;
    private readonly List<Action> manualChoiceActions = new List<Action>();

    private Button gamblingLauncherButton;
    private Button rewindButton;
    private int lastDay = -1;
    private int lastCheckpointCount = -1;
    private bool explicitBorrowPending;
    private int explicitBorrowRequestDay = -1;
    private bool borrowOverlayShownForCurrentDay;
    private bool restoringCheckpoint;
    private List<string> preservedDialogueLog = new List<string>();
    private float nextButtonScanAt;
    private AppType? lastObservedApp;
    private int lateMapCueShownDay = -1;
    private int postJobRepaymentPromptedDay = -1;
    private bool lenderDebtStateInitialized;
    private bool observedBorrowedMom;
    private bool observedBorrowedSeojun;
    private bool observedBorrowedMinjae;

    private Button novelTapAdvanceSurface;
    private Button tabletTapAdvanceSurface;
    private CanvasGroup novelContinueCanvasGroup;
    private CanvasGroup tabletContinueCanvasGroup;
    private float nextDialogueTapAt;

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
        InstallTouchDialogueControls();
        InitializeLenderDebtState();
        InvokePrivate(director, "Save");
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        HandleQaShortcuts();
#endif
        TrackExplicitBorrowRequest();
        ExpireBorrowActionsOutsideMorning();
        KeepMinjaeLoanOfferRepeatableUntilAccepted();
        SynchronizeLenderDebtState();
        HandleDayChangeAndDialogueLog();
        TrackLateMapCue();
        CaptureNewCheckpointSnapshots();
        TryShowDeferredBorrowChoice();
        TryReplaceDebtChatChoicesWithDialogue();
        TryOfferPostJobRepayment();
        MaintainTouchDialogueControls();
    }

    private void LateUpdate()
    {
        if (flow == null)
            return;

        ApplyExactAttentionDots();
        ApplyRightSideChoiceLayout();
        if (choiceOverlay != null && choiceOverlay.activeSelf)
            choiceOverlay.transform.SetAsLastSibling();
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

        ScenarioV3Scene lateMorningScene = database.GetScene("sys_late_gamble_morning");
        if (lateMorningScene != null)
        {
            // 평일 밤샘 장면. 주말은 아래의 별도 장면에서 카페 결근으로 안내한다.
            lateMorningScene.arc = "main";
            lateMorningScene.condition =
                "flag.gambled_late=true;flag.borrow_deferred!=true;day!=4;day!=5;day!=11;day!=12";
        }

        ScenarioV3Line lateMorningFirst = FindLine("sys_late_gamble_morning_01");
        if (lateMorningFirst != null)
            lateMorningFirst.text = "언제 잠든 거지.... 도박 앱을 켜둔 채 그대로 잠든 모양이다.";
        ScenarioV3Line lateMorning = FindLine("sys_late_gamble_morning_02");
        if (lateMorning != null)
            lateMorning.text = "벌써 오전 10시다.... 학교에 늦었다. 그래도 지금이라도 가는 편이 낫겠다.";

        var weekendLateMorning = new ScenarioV3Scene
        {
            id = "sys_late_gamble_morning_weekend",
            arc = "main",
            day = "2..14",
            timeWindow = "7:00",
            trigger = "day_start",
            condition = "flag.gambled_late=true;flag.borrow_deferred!=true;day=4|day=5|day=11|day=12",
            priority = 203,
            onceScope = "day",
            purpose = "주말 밤샘 뒤 오전 10시 기상과 카페 결근을 명확히 안내한다."
        };
        weekendLateMorning.lines.Add(CreateLine(
            "sys_late_gamble_morning_weekend_01", 1, "Protagonist", "나", "narration", string.Empty,
            "언제 잠든 거지.... 도박 앱을 켜둔 채 그대로 잠든 모양이다.",
            string.Empty, string.Empty));
        weekendLateMorning.lines.Add(CreateLine(
            "sys_late_gamble_morning_weekend_02", 2, "Protagonist", "나", "narration", string.Empty,
            "벌써 오전 10시다. 카페 근무 시간은 이미 지나 있었다.... 결국 오늘 알바를 놓쳤다.",
            "fatigue:add=1|counter.short_sleep_days:add=1|flag.gambled_late:set=false", string.Empty));
        AddOrReplaceScene(weekendLateMorning);

        ScenarioV3Line jobIncome = FindLine("d5_job_02");
        if (jobIncome != null)
            jobIncome.text = "오늘 번 5만 원만큼 수리비에 가까워졌다. 하루를 꼬박 일해서 번 돈이라는 게 숫자로 보니 더 또렷했다.";

        ScenarioV3Line jobEvening = FindLine("d5_evening_02");
        if (jobEvening != null)
            jobEvening.text = "몸은 무겁지만 오늘 해야 할 일은 끝냈다. 이제 씻고 쉬자.";

        ScenarioV3Line minjaeReject = FindLine("minjae_loan_rejected_01");
        if (minjaeReject != null)
        {
            minjaeReject.portrait = "minjae_angry";
            minjaeReject.text = "그래. 마음 바뀌면 말해. 돈 필요하면 그때 연락하고.";
        }
        ScenarioV3Line minjaeRejectThought = FindLine("minjae_loan_rejected_02");
        if (minjaeRejectThought != null)
            minjaeRejectThought.text = "지금은 민재에게 빌리지 않기로 했다. 돈이 없는 건 그대로지만, 당장은 여기서 멈추는 편이 낫겠다.";

        ScenarioV3Line blockThought = FindLine("d14_no_help_messages_03");
        if (blockThought != null)
        {
            blockThought.delivery = "overlay";
            blockThought.text = "민재를 차단하고 그 앱을 지우면 되는데.... 손가락이 화면 위에서 움직이지 않았다.";
        }

        ScenarioV3Line recoveryMinjae = FindLine("d14_recovery_minjae_02");
        if (recoveryMinjae != null)
        {
            recoveryMinjae.portrait = "minjae_angry";
            recoveryMinjae.text = "됐고, 끊든 말든 네 사정이야. 나한테 빌린 5만 원이나 날짜 맞춰서 갚아.";
            recoveryMinjae.enterEffects = string.Empty;
        }
        ScenarioV3Line recoveryMinjaeReply = FindLine("d14_recovery_minjae_03");
        if (recoveryMinjaeReply != null)
            recoveryMinjaeReply.text = "알겠어. 날짜 정하면 먼저 알려줄게. 그 앱은 이제 안 들어갈 거야.";

        PatchSeojunRepaymentScene();
        PatchMinjaeRepaymentScene();
        PatchFirstJobNarrative();
        PatchD8EveningTiming();
        PatchManagerNarrative();
        PatchSeoyeonRecoveryContext();
        PatchRepeatedLanguage();
        PatchMultiLenderEndingChain();
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
            scene.condition = "borrowed.seojun=true;debt.seojun>0";
        if (line == null)
            return;

        line.text = "다음 주에 갚는다고 한 거 기억하지? 나도 계속 기다리긴 어려워. 언제 가능한지만 말해줘.";
        line.autoNext = string.Empty;
        line.choiceA = new ScenarioV3Choice
        {
            id = "d10_repay_now",
            text = "지금 갚을 수 있는 만큼 보낸다",
            replyText = string.Empty,
            effects = string.Empty,
            nextSceneId = "d10_seojun_repay_router"
        };
        line.choiceB = new ScenarioV3Choice
        {
            id = "d10_delay_repay",
            text = "조금만 더 기다려 달라고 한다",
            replyText = string.Empty,
            effects = string.Empty,
            nextSceneId = "d10_seojun_delay_thought"
        };
        line.choiceC = null;

        ScenarioV3Line delayThought = FindLine("d10_seojun_followup_02");
        if (delayThought != null)
            delayThought.text = "답장은 금방 쓸 수 있는데, 약속을 또 미룬다는 말을 보내려니 손이 멈췄다.";
        ScenarioV3Line cannotRepay = FindLine("d10_seojun_cannot_repay_01");
        if (cannotRepay != null)
            cannotRepay.text = "갚고 싶지만 지금은 보낼 돈이 없다.... 조금만 더 기다려 달라고 해야겠다.";
    }

    private void PatchMinjaeRepaymentScene()
    {
        ScenarioV3Line dayRouter = FindLine("d10_minjae_router_01");
        if (dayRouter != null)
            dayRouter.enterEffects = "route:d10_minjae_debt if debt.minjae>0 else d10_minjae";

        ScenarioV3Scene scene = database.GetScene("d10_minjae_debt");
        if (scene == null || scene.lines.Count == 0)
            return;
        scene.condition = "debt.minjae>0";

        ScenarioV3Line first = FindLine("d10_minjae_debt_01");
        ScenarioV3Line second = FindLine("d10_minjae_debt_02");
        ScenarioV3Line oldReply = FindLine("d10_minjae_debt_03");
        if (first != null)
            first.text = "주말 알바비 들어오면 내 돈부터 갚는 거지? 언제 보낼 건지는 확실히 말해.";
        if (second != null)
        {
            second.text = "지금 보낼 수 있으면 먼저 보내. 없으면 언제 줄 건지 말하고.";
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
                "민재가 뭐라고 하든 빌린 돈은 갚아야 한다. 지금 보낼 수 있는 것부터 보내자.",
                string.Empty, "d10_minjae_repaid_message_router")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_repaid_message_router", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_repaid_message_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d10_minjae_repaid_full_message if debt.minjae=0 else d10_minjae_repaid_partial_message",
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
                "갚고 싶지만 지금은 보낼 돈이 없다.... 주말 알바비가 들어오면 먼저 갚겠다고 해야겠다.",
                string.Empty, "d10_minjae_delay_message")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_delay_thought", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_delay_thought_01", 1, "Protagonist", "나", "overlay", string.Empty,
                "또 미룬다는 말을 쓰려니 손이 멈췄다.... 그래도 답장은 해야 한다.",
                string.Empty, "d10_minjae_delay_message")));

        AddOrReplaceScene(CreateScene(
            "d10_minjae_delay_message", "debt", "10", "morning", 132,
            CreateLine("d10_minjae_delay_message_01", 1, "Protagonist", "민재", "message", string.Empty,
                "지금은 보낼 돈이 없어. 주말 알바비가 들어오면 먼저 갚을게.",
                string.Empty, string.Empty)));
    }


    private void PatchFirstJobNarrative()
    {
        ScenarioV3Scene scene = database.GetScene("d4_job");
        ScenarioV3Line personalGoal = FindLine("d4_job_01b");
        if (scene != null && personalGoal != null)
            scene.lines.Remove(personalGoal);

        ScenarioV3Line managerPay = FindLine("d4_job_02");
        if (managerPay != null)
            managerPay.text = "그래도 첫날치곤 괜찮았어. 오늘 일당 5만 원은 넣어뒀어. 고생했어.";

        ScenarioV3Line incomeThought = FindLine("d4_job_03");
        if (incomeThought != null)
        {
            incomeThought.text =
                "유니폼에 밴 커피 냄새를 맡으며 입금 알림을 다시 봤다. " +
                "노트북 수리비까지는 아직 멀었지만, 오늘 5만 원은 내가 하루 일해서 채운 돈이었다.";
        }
    }


    private void PatchD8EveningTiming()
    {
        Dictionary<string, ScenarioV3Scene> scenes =
            GetField<Dictionary<string, ScenarioV3Scene>>(database, "scenes");
        if (scenes == null)
            return;

        foreach (ScenarioV3Scene scene in scenes.Values
                     .Where(candidate => candidate != null &&
                                         string.Equals(candidate.trigger, "evening_fill",
                                             StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (scene.lines == null || scene.lines.Count == 0)
                continue;
            if (scene.lines.Any(line => line != null &&
                    line.id.StartsWith("v23_evening_arrival_", StringComparison.OrdinalIgnoreCase)))
                continue;

            List<ScenarioV3Line> originalLines = scene.lines
                .Where(line => line != null)
                .OrderBy(line => line.sequence)
                .ToList();

            foreach (ScenarioV3Line line in originalLines)
            {
                line.enterEffects = RemoveEffect(line.enterEffects, "clock:set=18:00");
                line.enterEffects = RemoveEffect(line.enterEffects, "clock:set=21:00");
                line.sequence += 3;
            }

            bool stayedHome = scene.id.IndexOf("missed", StringComparison.OrdinalIgnoreCase) >= 0;
            string momText = stayedHome
                ? "저녁 먹을 거야? 밥 차려놨어. 식기 전에 먹어."
                : "왔어? 손 씻고 밥부터 먹자.";
            string playerText = stayedHome
                ? "응. 조금 있다 먹을게."
                : "응. 금방 갈게.";

            scene.lines.Clear();
            scene.lines.Add(CreateLine(
                "v23_evening_arrival_" + scene.id + "_01", 1,
                "Mom", "엄마", "dialogue", string.Empty,
                momText, "clock:set=18:00", string.Empty));
            scene.lines.Add(CreateLine(
                "v23_evening_arrival_" + scene.id + "_02", 2,
                "Protagonist", "나", "dialogue", string.Empty,
                playerText, string.Empty, string.Empty));
            scene.lines.Add(CreateLine(
                "v23_evening_arrival_" + scene.id + "_03", 3,
                "System", string.Empty, "router", string.Empty,
                string.Empty, "clock:set=21:00", string.Empty));

            foreach (ScenarioV3Line line in originalLines)
                scene.lines.Add(line);
        }
    }


    private void PatchManagerNarrative()
    {
        ScenarioV3Line d11Reflection = FindLine("d11_evening_01");
        if (d11Reflection != null)
            d11Reflection.text =
                "점장님이 걱정스러운 표정으로 보던 게 자꾸 떠올랐다. 내일은 일단 늦지 않게 가자.";

        ScenarioV3Line d11Decision = FindLine("d11_evening_02");
        if (d11Decision != null)
            d11Decision.text = "무슨 말을 할지는 그때 생각하자. 오늘은 쉬는 게 먼저다.";

        ScenarioV3Line d12Start = FindLine("d12_start_02");
        if (d12Start != null)
            d12Start.text = "어제는 겨우 근무를 마쳤다. 오늘도 늦지 않게 카페부터 가자.";

        ScenarioV3Scene managerScene = database.GetScene("d12_manager_help");
        if (managerScene != null)
        {
            managerScene.lines.Clear();
            managerScene.purpose = "점장이 상태를 확인하고, 돈 문제를 말할지는 플레이어가 직접 정한다.";
            managerScene.lines.Add(CreateLine(
                "d12_manager_help_00a", 1, "CafeManager", "점장님", "dialogue", "manager_worried",
                "요즘 계속 피곤해 보이네. 어제 친구랑 돈 얘기하던 것도 그렇고. 무슨 일 있어?",
                "counter.job_attendance:add=1", string.Empty));

            ScenarioV3Line decision = CreateLine(
                "d12_manager_help_00b", 2, "Protagonist", "나", "dialogue", string.Empty,
                "그 얘기까지 해야 하나....",
                string.Empty, string.Empty);
            decision.choiceA = new ScenarioV3Choice
            {
                id = "v21_manager_tell_truth",
                text = "돈 문제를 사실대로 말한다",
                replyText = string.Empty,
                effects = string.Empty,
                nextSceneId = "v21_manager_tell_truth"
            };
            decision.choiceB = new ScenarioV3Choice
            {
                id = "v21_manager_hide",
                text = "피곤해서 그렇다고 넘긴다",
                replyText = string.Empty,
                effects = string.Empty,
                nextSceneId = "v21_manager_hide"
            };
            managerScene.lines.Add(decision);
        }

        AddOrReplaceScene(CreateScene(
            "v21_manager_tell_truth", "job", "12", "job", 150,
            CreateLine("v21_manager_tell_truth_01", 1, "Protagonist", "나", "dialogue", string.Empty,
                "사실.... 돈 문제로 좀 꼬였어요. 도박도 했고, 친구한테 돈도 빌렸어요.",
                string.Empty, string.Empty),
            CreateLine("v21_manager_tell_truth_02", 2, "CafeManager", "점장님", "dialogue", "manager_worried",
                "아.... 그랬구나. 내가 대신 해결해 줄 수 있는 일은 아니지만, 돈으로 급하게 메우려고 하면 더 커질 수 있어.",
                string.Empty, string.Empty),
            CreateLine("v21_manager_tell_truth_03", 3, "CafeManager", "점장님", "dialogue", "manager_worried",
                "학교 다니니까 선생님이나 부모님한테 먼저 말해봐. 누구한테 얼마를 빌렸는지부터 확인하고.",
                "relation.manager:add=3|flag.manager_advice:set=true", string.Empty)));

        AddOrReplaceScene(CreateScene(
            "v21_manager_hide", "job", "12", "job", 149,
            CreateLine("v21_manager_hide_01", 1, "Protagonist", "나", "dialogue", string.Empty,
                "아니에요.... 요즘 잠을 좀 못 자서 그래요.",
                string.Empty, string.Empty),
            CreateLine("v21_manager_hide_02", 2, "CafeManager", "점장님", "dialogue", "manager_worried",
                "알겠어. 그럼 더 묻진 않을게. 근무하다 힘들면 바로 말해.",
                "relation.manager:add=1|flag.manager_advice:set=false", string.Empty)));

        ScenarioV3Line d12Reflection = FindLine("d12_evening_01");
        if (d12Reflection != null)
            d12Reflection.text =
                "집에 돌아와서도 카페에서 나눈 말이 생각났다. 말하지 않은 건 그대로 남아 있었다.";

        ScenarioV3Line d12Decision = FindLine("d12_evening_02");
        if (d12Decision != null)
            d12Decision.text =
                "내일 학교에 가면 선생님한테 한번 물어볼까.... 어디서부터 손대야 할지는 알고 싶다.";

        ScenarioV3Line distantWarning = FindLine("d12_manager_distant_01");
        if (distantWarning != null)
            distantWarning.text =
                "오늘 나온 건 다행이야. 다만 연락 없이 빠진 날이 있었잖아. 다음에도 그러면 근무를 계속 맡기기 어려워.";

        ScenarioV3Line distantResult = FindLine("d12_manager_distant_02");
        if (distantResult != null)
            distantResult.text =
                "오늘 근무는 그대로 마쳤다. 다음 일정이 잡히면 점장님과 시간을 먼저 확인하기로 했다.";
    }

    private void PatchSeoyeonRecoveryContext()
    {
        ScenarioV3Scene scene = database.GetScene("d14_seoyeon_good");
        ScenarioV3Line original = FindLine("d14_seoyeon_good_01");
        if (scene == null || original == null)
            return;

        string effects = original.enterEffects;
        string next = original.autoNext;
        original.sequence = 3;
        original.text = "그래서 그랬구나.... 그래도 네 역할은 끝까지 했잖아. 선생님한테 말한 것도 잘했어.";
        original.enterEffects = effects;
        original.autoNext = next;

        scene.lines.Clear();
        scene.lines.Add(CreateLine(
            "d14_seoyeon_good_00a", 1, "Seoyeon", "서연", "dialogue", "seoyeon_worried",
            "요즘 계속 힘들어 보였는데.... 무슨 일 있었어?", string.Empty, string.Empty));
        scene.lines.Add(CreateLine(
            "d14_seoyeon_good_00b", 2, "Protagonist", "나", "dialogue", string.Empty,
            "사실 도박 때문에 돈을 잃었어. 혼자 해결해 보려다가 더 꼬여서.... 어제 선생님한테 말했어.",
            string.Empty, string.Empty));
        scene.lines.Add(original);
    }


    private void PatchRepeatedLanguage()
    {
        ScenarioV3Line seoyeonCheck = FindLine("d10_school_checkin_03");
        if (seoyeonCheck != null)
            seoyeonCheck.text =
                "알바 때문만은 아닌 것 같은데.... 지금 말하기 싫으면 괜찮아. 무슨 일 생기면 나한테 한마디만 해.";

        ScenarioV3Line missedSchool = FindLine("d10_school_missed_message_01");
        if (missedSchool != null)
            missedSchool.text =
                "오늘 학교 안 왔더라. 주말 알바 있다 했지? 자세히 말 안 해도 되니까, 무슨 일 없는지만 알려줘.";

        ScenarioV3Line missedJob = FindLine("d11_job_missed_now_01");
        if (missedJob != null)
        {
            missedJob.text =
                "오늘 출근하기로 했는데 연락이 없어서 걱정했어. 다음엔 늦거나 못 올 것 같으면 먼저 알려줘.";
            if (missedJob.choiceA != null)
            {
                missedJob.choiceA.text = "다음부터 미리 말씀드린다";
                missedJob.choiceA.replyText = "죄송해요. 다음부터는 늦기 전에 미리 말씀드릴게요.";
            }
        }

        ScenarioV3Line d5Missed = FindLine("d5_missed_01");
        if (d5Missed != null)
            d5Missed.text =
                "어제에 이어 오늘도 카페에 가지 못했다. 다음 주엔 어떤 얼굴로 문을 열고 들어가야 하지....";

        ScenarioV3Line d12Missed = FindLine("d12_missed_01");
        if (d12Missed != null)
            d12Missed.text =
                "오늘도 결국 카페에 가지 못했다. 점장님한테 뭐라고 해야 할지 생각만 하다 하루가 끝났다.";

        ScenarioV3Line seojunFollowup = FindLine("d10_seojun_followup_01");
        if (seojunFollowup?.choiceB != null)
            seojunFollowup.choiceB.text = "주말 알바비까지 기다려 달라고 한다";

        ScenarioV3Line seojunDelay = FindLine("d10_seojun_followup_02");
        if (seojunDelay != null)
            seojunDelay.text = "답장은 금방 쓸 수 있는데, 보낼 날짜를 또 바꾸려니 손이 멈췄다.";

        ScenarioV3Line seojunCannot = FindLine("d10_seojun_cannot_repay_01");
        if (seojunCannot != null)
            seojunCannot.text =
                "갚고 싶지만 지금은 보낼 돈이 없다.... 주말 알바비가 들어오면 먼저 보내겠다고 해야겠다.";

        ScenarioV3Line minjaeDebtChoice = FindLine("d10_minjae_debt_02");
        if (minjaeDebtChoice?.choiceB != null)
            minjaeDebtChoice.choiceB.text = "알바비가 들어오면 보내겠다고 한다";

        ScenarioV3Line minjaeDelay = FindLine("d10_minjae_delay_thought_01");
        if (minjaeDelay != null)
            minjaeDelay.text =
                "지금 돈이 없다는 말을 또 쓰려니 손이 멈췄다.... 그래도 답은 해야 한다.";

        ScenarioV3Line noHelpSeojunFirst = FindLine("d14_no_help_seojun_01");
        if (noHelpSeojunFirst != null)
            noHelpSeojunFirst.text =
                "미안한데 나도 이제는 이유를 알아야 할 것 같아. 약속한 날짜가 지났는데 아무 설명도 없으면 나도 곤란해.";

        ScenarioV3Line noHelpSeojunReply = FindLine("d14_no_help_seojun_03");
        if (noHelpSeojunReply != null)
            noHelpSeojunReply.text =
                "미안해. 지금은 제대로 설명하기 어렵다. 언제 보낼 수 있는지 확인해서 다시 말할게.";

        ScenarioV3Line projectApology = FindLine("d14_seoyeon_bad_02");
        if (projectApology != null)
            projectApology.text = "내 부분을 제때 못 보내서 네가 대신 했잖아. 미안해.";

        ScenarioV3Line epilogue = FindLine("d14_epilogue_02");
        if (epilogue != null)
            epilogue.text =
                "한 번 멈춘다고 모든 게 원래대로 돌아오지는 않았다. 그래도 이제 문제를 말할 사람이 생겼다.";
    }

    private void PatchMultiLenderEndingChain()
    {
        // Recovery: every person the player actually borrowed from must receive the required
        // message before the ending card. Outstanding debt and "ever borrowed" are separate.
        ScenarioV3Line recoveryRouter = FindLine("d14_recovery_followup_04");
        if (recoveryRouter != null)
        {
            recoveryRouter.enterEffects =
                "route:d14_recovery_seojun if borrowed.seojun=true else v21_recovery_minjae_router";
            recoveryRouter.autoNext = string.Empty;
        }

        AddOrReplaceScene(CreateScene(
            "v21_recovery_minjae_router", "debt", "14", "epilogue", 177,
            CreateLine("v21_recovery_minjae_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d14_recovery_minjae if borrowed.minjae=true else v21_recovery_after_lenders",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_recovery_after_lenders", "debt", "14", "epilogue", 176,
            CreateLine("v21_recovery_after_lenders_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d14_recovery_family if debt>0 else v21_recovery_zero_debt_router",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_recovery_zero_debt_router", "debt", "14", "epilogue", 176,
            CreateLine("v21_recovery_zero_debt_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v22_recovery_repaid_check_seojun", string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_recovery_repaid_check_seojun", "debt", "14", "epilogue", 176,
            CreateLine("v22_recovery_repaid_check_seojun_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_recovery_repaid if borrowed.seojun=true else v22_recovery_repaid_check_minjae",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_recovery_repaid_check_minjae", "debt", "14", "epilogue", 176,
            CreateLine("v22_recovery_repaid_check_minjae_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_recovery_repaid if borrowed.minjae=true else v22_recovery_repaid_check_mom",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_recovery_repaid_check_mom", "debt", "14", "epilogue", 176,
            CreateLine("v22_recovery_repaid_check_mom_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_recovery_repaid if borrowed.mom=true else d14_recovery_no_debt",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_recovery_repaid", "main", "14", "epilogue", 175,
            CreateLine("v21_recovery_repaid_01", 1, "Mom", "엄마", "dialogue", string.Empty,
                "빌린 돈은 다 갚았어도 여기까지 오면서 숨긴 일은 남아 있잖아. 상담은 계속 받아보자.",
                string.Empty, string.Empty),
            CreateLine("v21_recovery_repaid_02", 2, "Protagonist", "나", "dialogue", string.Empty,
                "응. 돈을 갚았다고 다 끝난 척하지 않고, 처음부터 같이 정리해볼게.",
                "relation.mom:add=2", string.Empty),
            CreateLine("v21_recovery_repaid_03", 3, "Protagonist", "나", "narration", string.Empty,
                "갚을 돈은 정리했지만 다시 같은 선택을 하지 않으려면 더 시간이 필요했다.",
                string.Empty, "ending_recovery")));

        ScenarioV3Scene seojunRecovery = database.GetScene("d14_recovery_seojun");
        if (seojunRecovery != null && seojunRecovery.lines.Count > 0)
            seojunRecovery.lines[seojunRecovery.lines.Count - 1].autoNext = "v21_recovery_minjae_router";
        ScenarioV3Scene minjaeRecovery = database.GetScene("d14_recovery_minjae");
        if (minjaeRecovery != null && minjaeRecovery.lines.Count > 0)
            minjaeRecovery.lines[minjaeRecovery.lines.Count - 1].autoNext = "v21_recovery_after_lenders";

        // No-help ending: the same all-lenders requirement applies. If loans were repaid earlier,
        // do not use the old "never borrowed" text; use a neutral repaid-but-still-hiding scene.
        ScenarioV3Line noHelpRouter = FindLine("d14_no_help_messages_04");
        if (noHelpRouter != null)
        {
            noHelpRouter.enterEffects =
                "route:d14_no_help_seojun if borrowed.seojun=true else v21_no_help_minjae_router";
            noHelpRouter.autoNext = string.Empty;
        }

        AddOrReplaceScene(CreateScene(
            "v21_no_help_minjae_router", "debt", "14", "morning", 207,
            CreateLine("v21_no_help_minjae_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d14_no_help_minjae if borrowed.minjae=true else v21_no_help_mom_router",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_no_help_mom_router", "debt", "14", "morning", 206,
            CreateLine("v21_no_help_mom_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d14_no_help_mom if borrowed.mom=true else v21_no_help_after_lenders",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_no_help_after_lenders", "debt", "14", "morning", 205,
            CreateLine("v21_no_help_after_lenders_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:d14_no_help_home if debt>0 else v21_no_help_zero_debt_router",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_no_help_zero_debt_router", "debt", "14", "morning", 205,
            CreateLine("v21_no_help_zero_debt_router_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v22_no_help_repaid_check_seojun", string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_no_help_repaid_check_seojun", "debt", "14", "morning", 205,
            CreateLine("v22_no_help_repaid_check_seojun_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_no_help_repaid_home if borrowed.seojun=true else v22_no_help_repaid_check_minjae",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_no_help_repaid_check_minjae", "debt", "14", "morning", 205,
            CreateLine("v22_no_help_repaid_check_minjae_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_no_help_repaid_home if borrowed.minjae=true else v22_no_help_repaid_check_mom",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v22_no_help_repaid_check_mom", "debt", "14", "morning", 205,
            CreateLine("v22_no_help_repaid_check_mom_01", 1, "System", string.Empty, "router", string.Empty,
                string.Empty, "route:v21_no_help_repaid_home if borrowed.mom=true else d14_no_help_no_debt",
                string.Empty)));
        AddOrReplaceScene(CreateScene(
            "v21_no_help_repaid_home", "main", "14", "night", 200,
            CreateLine("v21_no_help_repaid_home_01", 1, "Protagonist", "나", "narration", string.Empty,
                "빌린 돈은 갚았지만, 왜 그런 선택을 했는지는 누구에게도 제대로 말하지 못했다.",
                string.Empty, string.Empty),
            CreateLine("v21_no_help_repaid_home_02", 2, "Protagonist", "나", "narration", string.Empty,
                "계좌의 빚은 사라졌는데도 잃은 돈 생각은 남아 있었다. 결국 그 앱을 다시 열었다.",
                string.Empty, "ending_no_help")));

        ScenarioV3Scene seojunNoHelp = database.GetScene("d14_no_help_seojun");
        if (seojunNoHelp != null && seojunNoHelp.lines.Count > 0)
            seojunNoHelp.lines[seojunNoHelp.lines.Count - 1].autoNext = "v21_no_help_minjae_router";
        ScenarioV3Scene minjaeNoHelp = database.GetScene("d14_no_help_minjae");
        if (minjaeNoHelp != null && minjaeNoHelp.lines.Count > 0)
            minjaeNoHelp.lines[minjaeNoHelp.lines.Count - 1].autoNext = "v21_no_help_mom_router";
    }

    private static string RemoveEffect(string effects, string target)
    {
        if (string.IsNullOrWhiteSpace(effects))
            return string.Empty;
        return string.Join("|", effects.Split('|')
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 &&
                            !string.Equals(value, target, StringComparison.OrdinalIgnoreCase)));
    }

    private static string AppendEffect(string effects, string addition)
    {
        string cleaned = RemoveEffect(effects, addition);
        return string.IsNullOrWhiteSpace(cleaned) ? addition : cleaned + "|" + addition;
    }

    private void AddExtendedGamblingScenes()
    {
        AddOrReplaceScene(CreateScene(
            "gamble_7", "gambling", "1..14", "cinematic", 300,
            CreateLine("gamble_7_01", 1, "Narrator", string.Empty, "cinematic", string.Empty,
                "다시 넣은 돈에서 20,000원이 늘었다. 바닥까지 갔던 잔액이 오르자 방금 전 손실이 잠깐 잊혔다.",
                "clock:add=120|cash:add=20000|temptation:add=1", string.Empty),
            CreateLine("gamble_7_02", 2, "Protagonist", "나", "dialogue", string.Empty,
                "....한 번만 더 맞으면 이번엔 진짜 되찾을 수 있을 것 같은데.",
                string.Empty, string.Empty)));

        AddOrReplaceScene(CreateScene(
            "gamble_8", "gambling", "1..14", "cinematic", 300,
            CreateLine("gamble_8_01", 1, "Narrator", string.Empty, "cinematic", string.Empty,
                "그 확신을 따라 금액을 키웠지만 결과는 40,000원 손실이었다. 남아 있던 돈도 함께 줄었다.",
                "clock:add=120|cash:add=-40000|temptation:add=2", string.Empty),
            CreateLine("gamble_8_02", 2, "Protagonist", "나", "dialogue", string.Empty,
                "방금 번 것보다 더 크게 잃었다. 그런데도 또 다음 판부터 생각났다.",
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
        PatchMapLauncherGate();
        PatchSchoolTravelButtons();
        PatchRewindButton();
        PatchSimpleButtonDebounce();
    }

    private void PatchGamblingLauncher()
    {
        GameObject launcher = FindSceneObject("Gambling Launcher");
        if (launcher == null)
            return;

        Transform existingProxy = launcher.transform.Find("V21 Gambling Guard");
        if (existingProxy != null)
        {
            gamblingLauncherButton = existingProxy.GetComponent<Button>();
            existingProxy.SetAsLastSibling();
            return;
        }

        // Inspector에 영구 등록된 클릭 이벤트는 RemoveAllListeners로 지워지지 않는다.
        // 투명한 자식 버튼이 먼저 입력을 받아, 제한 상태에서 도박 앱이 함께 열리는 일을 막는다.
        GameObject proxyObject = new GameObject("V21 Gambling Guard", typeof(RectTransform),
            typeof(CanvasRenderer), typeof(Image), typeof(Button));
        proxyObject.layer = launcher.layer;
        proxyObject.transform.SetParent(launcher.transform, false);
        Stretch(proxyObject.GetComponent<RectTransform>());
        proxyObject.transform.SetAsLastSibling();

        Image proxyImage = proxyObject.GetComponent<Image>();
        proxyImage.color = new Color(1f, 1f, 1f, 0.001f);
        proxyImage.raycastTarget = true;

        Button proxyButton = proxyObject.GetComponent<Button>();
        proxyButton.transition = Selectable.Transition.None;
        proxyButton.targetGraphic = proxyImage;
        proxyButton.onClick.AddListener(HandleGamblingLauncher);
        gamblingLauncherButton = proxyButton;
    }

    private void HandleGamblingLauncher()
    {
        if (flow == null || director == null || flow.IsGameEnded || !director.IsGamblingAppUnlocked)
            return;
        if (GetField<bool>(flow, "isTransitioning"))
            return;

        string outgoingContact = GetField<string>(director, "pendingOutgoingContact") ?? string.Empty;
        string pendingBorrowTarget = GetDirectorState("pending.borrow_target");
        bool preparedBorrow = !string.IsNullOrWhiteSpace(pendingBorrowTarget) &&
                              !string.Equals(pendingBorrowTarget, "none", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(pendingBorrowTarget, "false", StringComparison.OrdinalIgnoreCase) &&
                              pendingBorrowTarget != "0";
        bool waitingChoice = GetField<bool>(director, "waitingForMessageChoice");
        bool waitingRead = GetField<bool>(director, "waitingForIncomingMessageRead");
        bool waitingClose = GetField<bool>(director, "waitingForMessageSceneClose");
        bool unread = dialogue != null && dialogue.TotalUnreadCount > 0;

        if (!string.IsNullOrWhiteSpace(outgoingContact) || preparedBorrow || waitingChoice ||
            waitingRead || waitingClose || unread)
        {
            string prompt;
            if (!string.IsNullOrWhiteSpace(outgoingContact))
            {
                prompt = string.Equals(outgoingContact, "서연", StringComparison.OrdinalIgnoreCase)
                    ? "(서연에게 보내기로 한 메시지가 아직 남아 있다.... 먼저 보내고 나서 생각하자.)"
                    : $"({outgoingContact}에게 보내기로 한 메시지가 아직 남아 있다.... 그걸 먼저 정리하자.)";
            }
            else if (preparedBorrow)
            {
                prompt = "(돈을 부탁하려고 정한 메시지가 아직 남아 있다.... 먼저 보내고 나서 생각하자.)";
            }
            else if (waitingChoice || waitingClose)
            {
                prompt = "(아직 답해야 할 메시지가 있다.... 그걸 먼저 정리하는 편이 낫겠다.)";
            }
            else
            {
                prompt = "(확인하지 않은 메시지가 있다. 무슨 내용인지부터 보는 편이 낫겠다.)";
            }

            flow.V3ShowDialogue("나", prompt, () => flow.V3MarkAppAttention(AppType.Message));
            return;
        }

        // 진행 중인 VN 위에 새 도박 장면을 겹치지 않는다.
        if (!string.IsNullOrWhiteSpace(director.ActiveSceneId))
            return;

        if (flow.IsWeekend && !flow.IsJobDone)
        {
            flow.V3ShowDialogue("나", "(출근 시간이 다가오고 있다.... 카페 일정부터 챙기는 편이 낫겠다.)",
                () => flow.V3MarkAppAttention(AppType.Map));
            return;
        }
        if (!flow.IsWeekend && !flow.IsSchoolDone)
        {
            flow.V3ShowDialogue("나", "(아직 학교 일정이 남아 있다.... 늦기 전에 다녀오는 편이 낫겠다.)",
                () => flow.V3MarkAppAttention(AppType.Map));
            return;
        }
        if (!flow.IsWeekend && flow.V3HasStudyToday && !flow.IsHomeworkDone)
        {
            flow.V3ShowDialogue("나", "(오늘 하기로 한 공부가 아직 남아 있다.... 먼저 끝내는 편이 낫겠다.)",
                () => flow.V3MarkAppAttention(AppType.Study));
            return;
        }

        ShowManualChoiceOverlay("한 판 해볼까....",
            new ManualChoiceOption("한다", BeginConfirmedGamble),
            new ManualChoiceOption("하지 않는다", () => { }));
    }

    private void BeginConfirmedGamble()
    {
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


    private void PatchMapLauncherGate()
    {
        foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (button == null || !button.gameObject.scene.IsValid() || patchedMapLauncherButtons.Contains(button))
                continue;

            bool opensMap = false;
            int persistentCount = button.onClick.GetPersistentEventCount();
            for (int index = 0; index < persistentCount; index++)
            {
                if (string.Equals(button.onClick.GetPersistentMethodName(index), "OpenMap", StringComparison.Ordinal))
                {
                    opensMap = true;
                    break;
                }
            }
            if (!opensMap)
                continue;

            patchedMapLauncherButtons.Add(button);
            Transform existingProxy = button.transform.Find("V23 Map Open Guard");
            if (existingProxy != null)
            {
                Button existingButton = existingProxy.GetComponent<Button>();
                if (existingButton != null)
                    patchedMapLauncherButtons.Add(existingButton);
                existingProxy.SetAsLastSibling();
                continue;
            }

            GameObject proxyObject = new GameObject("V23 Map Open Guard", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(Button));
            proxyObject.layer = button.gameObject.layer;
            proxyObject.transform.SetParent(button.transform, false);
            Stretch(proxyObject.GetComponent<RectTransform>());
            proxyObject.transform.SetAsLastSibling();

            Image image = proxyObject.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.001f);
            image.raycastTarget = true;

            Button proxyButton = proxyObject.GetComponent<Button>();
            proxyButton.transition = Selectable.Transition.None;
            proxyButton.targetGraphic = image;
            proxyButton.onClick.AddListener(HandleMapLauncherGate);
            patchedMapLauncherButtons.Add(proxyButton);
        }
    }

    private void HandleMapLauncherGate()
    {
        if (flow == null || appWindow == null || GetField<bool>(flow, "isTransitioning"))
            return;

        if (ShouldBlockMapForDay1MomMessage())
        {
            dialogue?.PreferConversation(SpeakerType.Mom);
            flow.V3MarkAppAttention(AppType.Message);
            flow.V3ShowDialogue("나", "(엄마에게서 온 메시지를 먼저 확인하자.)", null);
            return;
        }

        appWindow.OpenApp(AppType.Map);
    }

    private bool ShouldBlockMapForDay1MomMessage()
    {
        if (flow == null || director == null || flow.CurrentDay != 1)
            return false;
        if (!string.Equals(flow.CurrentLocation, "학교", StringComparison.Ordinal))
            return false;

        bool available = string.Equals(GetDirectorState("flag.d1_mom_message_available"), "true",
            StringComparison.OrdinalIgnoreCase);
        bool read = string.Equals(GetDirectorState("flag.d1_mom_message_read"), "true",
            StringComparison.OrdinalIgnoreCase);
        return available && !read;
    }

    private void PatchSchoolTravelButtons()
    {
        foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (button == null || !button.gameObject.scene.IsValid())
                continue;
            if (patchedSchoolButtons.Contains(button))
                continue;

            string label = button.GetComponentInChildren<TMP_Text>(true)?.text ?? string.Empty;
            bool strongSchoolName = button.name.IndexOf("School", StringComparison.OrdinalIgnoreCase) >= 0;
            bool looksLikeSchool = strongSchoolName || label.Trim() == "학교" || label.Contains("학교로");
            // Runtime-created map buttons are not always nested under an object literally named Map.
            // An explicit School object name is sufficient; text-only matches still require a map ancestor.
            if (!looksLikeSchool || (!strongSchoolName && !HasMapAncestor(button.transform)))
                continue;

            patchedSchoolButtons.Add(button);

            // Inspector-persistent UnityEvent listeners cannot be removed reliably at runtime.
            // Put a transparent child button over the original map button so the old TravelTo call
            // never fires before the player closes the late-arrival dialogue.
            Transform existingProxy = button.transform.Find("V21 School Travel Proxy");
            if (existingProxy != null)
                continue;

            GameObject proxyObject = new GameObject("V21 School Travel Proxy", typeof(RectTransform),
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
            patchedSchoolButtons.Add(proxyButton);
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

        int arrivalHour = flow.CurrentHour + flow.GetTravelHours("학교");
        bool late = !flow.IsWeekend && !flow.IsSchoolDone && flow.CurrentHour < 16 && arrivalHour > 10;
        if (late && lateMapCueShownDay != flow.CurrentDay)
        {
            lateMapCueShownDay = flow.CurrentDay;
            // Keep the map open. After closing this dialogue the player presses the school button again.
            flow.V3ShowDialogue("나", "(늦었지만 지금이라도 학교에 가는 편이 낫겠다.)", null);
            return;
        }

        flow.TravelTo("학교");
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
            if (button == null || !patchedDebounceButtons.Add(button))
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


    private void BlockDay1MapUntilMomMessageRead()
    {
        // V23.2: 지도 앱이 열린 뒤 닫는 사후 차단은 사용하지 않는다.
        // PatchMapLauncherGate가 OpenMap 호출 전에 입력을 가로챈다.
    }

    private void TrackLateMapCue()
    {
        if (appWindow == null || flow == null)
            return;

        AppType? currentApp = appWindow.CurrentAppType;
        bool mapJustOpened = currentApp == AppType.Map && lastObservedApp != AppType.Map;
        lastObservedApp = currentApp;
        if (!mapJustOpened || lateMapCueShownDay == flow.CurrentDay)
            return;
        if (flow.IsWeekend || flow.IsSchoolDone || flow.CurrentLocation != "집" || flow.CurrentHour >= 16)
            return;

        int arrivalHour = flow.CurrentHour + flow.GetTravelHours("학교");
        if (arrivalHour <= 10)
            return;

        lateMapCueShownDay = flow.CurrentDay;
        flow.V3ShowDialogue("나", "(벌써 늦었지만, 지금이라도 학교에 가는 편이 낫겠다.)", null);
    }

    // ---------------------------------------------------------------------
    // Borrowing: normal sleep -> next-day tablet choice
    // ---------------------------------------------------------------------

    private void TrackExplicitBorrowRequest()
    {
        bool pending = string.Equals(GetDirectorState("pending.borrow_menu"), "true", StringComparison.OrdinalIgnoreCase);
        bool deferred = string.Equals(GetDirectorState("flag.borrow_deferred"), "true", StringComparison.OrdinalIgnoreCase);
        int rememberedDay = GetDirectorInt("v22.borrow_requested_day");
        if (pending && (deferred || rememberedDay > 0))
        {
            if (!explicitBorrowPending)
            {
                int storedDay = rememberedDay;
                if (storedDay <= 0)
                {
                    // A fresh request is made during the night. When loading a save already at the
                    // next morning, infer the previous day. A leftover request first discovered in
                    // the middle of the day is stale and must not suddenly open at noon.
                    if (flow.CurrentHour >= 18)
                        storedDay = flow.CurrentDay;
                    else if (flow.CurrentHour <= 10)
                        storedDay = Mathf.Max(0, flow.CurrentDay - 1);
                    else
                    {
                        ClearDeferredBorrowRequest();
                        return;
                    }
                    SetDirectorState("v22.borrow_requested_day", storedDay.ToString(CultureInfo.InvariantCulture));
                }
                explicitBorrowRequestDay = storedDay;
                explicitBorrowPending = true;
            }
        }

        // Old source contains an emergency flag that used to force the day forward.
        // The data patch no longer sets it, and this guard prevents stale values from an older save/run.
        if (GetField<bool>(director, "pendingBorrowMorningAdvance"))
            SetField(director, "pendingBorrowMorningAdvance", false);
    }

    private void ExpireBorrowActionsOutsideMorning()
    {
        if (flow == null || dialogue == null)
            return;

        string target = (GetDirectorState("pending.borrow_target") ?? "none").Trim();
        bool hasPreparedMessage = !string.Equals(target, "none", StringComparison.OrdinalIgnoreCase) ||
                                  HasBorrowActionChoices();

        // A deferred request is only actionable from 07:00 through 10:00 on the following day.
        // Before 07:00 the intention is kept silently for the coming morning. After 10:00, both
        // the request and any unsent borrow-message buttons expire so they cannot reappear at noon.
        if (flow.CurrentHour > 10)
        {
            if (explicitBorrowPending || hasPreparedMessage ||
                string.Equals(GetDirectorState("pending.borrow_menu"), "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetDirectorState("flag.borrow_deferred"), "true", StringComparison.OrdinalIgnoreCase))
            {
                ClearDeferredBorrowRequest();
            }
            return;
        }

        if (flow.CurrentHour < 7 && hasPreparedMessage)
        {
            SetDirectorState("pending.borrow_target", "none");
            RemoveBorrowActionChoices();
            InvokePrivate(director, "Save");
        }
    }

    private bool HasBorrowActionChoices()
    {
        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null)
            return false;

        foreach (ChatChannel channel in channels.Values)
        {
            if (channel == null)
                continue;
            if (channel.eventChoices != null && channel.eventChoices.Any(IsBorrowActionChoice))
                return true;
            if (channel.pendingChoiceSets != null && channel.pendingChoiceSets.Any(set =>
                    set != null && set.Any(IsBorrowActionChoice)))
                return true;
        }
        return false;
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
        if (restoringCheckpoint)
            return;

        if (flow.CurrentDay != lastDay)
        {
            borrowOverlayShownForCurrentDay = false;
            lateMapCueShownDay = -1;
            postJobRepaymentPromptedDay = -1;

            // 최신 Director는 날짜 전환 때 이미 VN 로그를 비운다. 구버전에서 남아 있으면
            // 직전 날짜의 접두 구간만 제거하고 새 날짜 첫 대사는 보존한다.
            if (preservedDialogueLog.Count > 0 && current.Count >= preservedDialogueLog.Count &&
                current.Take(preservedDialogueLog.Count).SequenceEqual(preservedDialogueLog))
            {
                current.RemoveRange(0, preservedDialogueLog.Count);
            }

            // 잔액이 0원이라는 이유만으로 차용 메뉴를 자동 생성하지 않는다.
            if (!explicitBorrowPending &&
                string.Equals(GetDirectorState("flag.late_wake_today"), "true", StringComparison.OrdinalIgnoreCase))
            {
                SetDirectorState("pending.borrow_menu", "false");
            }

            if (!flow.IsWeekend && flow.CurrentHour >= 10 && !flow.IsSchoolDone)
                flow.V3MarkAppAttention(AppType.Map);

            lastDay = flow.CurrentDay;
        }

        preservedDialogueLog = new List<string>(current);
    }

    private void TryShowDeferredBorrowChoice()
    {
        if (borrowOverlayShownForCurrentDay || choiceOverlay == null || choiceOverlay.activeSelf)
            return;

        // The choice belongs to the morning after the player explicitly deferred borrowing.
        // It must never surface on the same night or later at noon/afternoon. A real late wake
        // still uses 10:00, so the valid window is 07:00 through 10:00 inclusive.
        if (!explicitBorrowPending)
            return;
        if (explicitBorrowRequestDay < 0)
            explicitBorrowRequestDay = GetDirectorInt("v22.borrow_requested_day");
        if (flow.CurrentDay <= explicitBorrowRequestDay)
            return;
        if (flow.CurrentHour < 7)
            return;
        if (flow.CurrentHour > 10)
        {
            ClearDeferredBorrowRequest();
            return;
        }
        if (!IsDirectorIdle() || flow.CurrentLocation != "집")
            return;

        ScenarioV3Scene scene = database.GetScene("borrow_choice");
        if (scene == null || scene.lines.Count == 0)
            return;

        borrowOverlayShownForCurrentDay = true;
        explicitBorrowPending = false;
        explicitBorrowRequestDay = -1;
        SetDirectorState("v22.borrow_requested_day", "0");
        SetDirectorState("pending.borrow_menu", "false");
        SetDirectorState("flag.borrow_deferred", "false");
        SetField(director, "activeScene", scene);
        SetField(director, "activeLineIndex", 0);
        appWindow?.CloseCurrentApp();

        ScenarioV3Line line = scene.lines[0];
        ShowChoiceOverlay(line, "어젯밤 생각해 둔 연락을 정해야 한다. 누구에게 부탁할까.",
            SpeakerType.Unknown);
        InvokePrivate(director, "Save");
    }

    private void ClearDeferredBorrowRequest()
    {
        explicitBorrowPending = false;
        explicitBorrowRequestDay = -1;
        SetDirectorState("v22.borrow_requested_day", "0");
        SetDirectorState("pending.borrow_menu", "false");
        SetDirectorState("flag.borrow_deferred", "false");
        SetDirectorState("pending.borrow_target", "none");
        RemoveBorrowActionChoices();
        InvokePrivate(director, "Save");
    }

    private void RemoveBorrowActionChoices()
    {
        if (dialogue == null)
            return;
        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null)
            return;

        foreach (ChatChannel channel in channels.Values)
        {
            if (channel == null)
                continue;
            channel.eventChoices?.RemoveAll(choice => IsBorrowActionChoice(choice));
            if (channel.pendingChoiceSets == null || channel.pendingChoiceSets.Count == 0)
                continue;
            var rebuilt = new Queue<List<Choice>>();
            foreach (List<Choice> set in channel.pendingChoiceSets)
            {
                List<Choice> kept = set == null
                    ? new List<Choice>()
                    : set.Where(choice => !IsBorrowActionChoice(choice)).ToList();
                if (kept.Count > 0)
                    rebuilt.Enqueue(kept);
            }
            channel.pendingChoiceSets = rebuilt;
        }
        InvokePrivate(dialogue, "ClearChoices");
        dialogue.UpdateAllProfileUI();
    }

    private static bool IsBorrowActionChoice(Choice choice)
    {
        return choice != null && !string.IsNullOrWhiteSpace(choice.scenarioAction) &&
               choice.scenarioAction.StartsWith("v3-borrow-", StringComparison.OrdinalIgnoreCase);
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
    // Per-lender debt and voluntary repayment after a completed weekend shift
    // ---------------------------------------------------------------------

    private void InitializeLenderDebtState()
    {
        if (flow == null || director == null)
            return;

        bool alreadyInitialized = string.Equals(GetDirectorState("v21.debt_state_initialized"), "true",
            StringComparison.OrdinalIgnoreCase);
        if (!alreadyInitialized)
        {
            bool mom = IsBorrowedFrom("mom");
            bool seojun = IsBorrowedFrom("seojun");
            bool minjae = IsBorrowedFrom("minjae");
            var borrowed = new List<string>();
            if (mom) borrowed.Add("mom");
            if (seojun) borrowed.Add("seojun");
            if (minjae) borrowed.Add("minjae");

            int remaining = Mathf.Max(0, flow.CurrentDebt);
            var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["mom"] = 0,
                ["seojun"] = 0,
                ["minjae"] = 0
            };

            // A fresh V21 run always has 50,000 won per accepted loan. For an older save, allocate
            // the currently recorded owner first, then the other accepted lenders without changing
            // the total debt shown by GameFlowManager.
            string owner = (GetDirectorState("debt_owner") ?? string.Empty).Trim().ToLowerInvariant();
            if (borrowed.Remove(owner))
                borrowed.Insert(0, owner);
            foreach (string key in borrowed)
            {
                int amount = Mathf.Min(50000, remaining);
                assigned[key] = amount;
                remaining -= amount;
            }

            SetDirectorState("debt.mom", assigned["mom"].ToString(CultureInfo.InvariantCulture));
            SetDirectorState("debt.seojun", assigned["seojun"].ToString(CultureInfo.InvariantCulture));
            SetDirectorState("debt.minjae", assigned["minjae"].ToString(CultureInfo.InvariantCulture));
            SetDirectorState("v21.debt_state_initialized", "true");
        }

        lenderDebtStateInitialized = true;
        observedBorrowedMom = IsBorrowedFrom("mom");
        observedBorrowedSeojun = IsBorrowedFrom("seojun");
        observedBorrowedMinjae = IsBorrowedFrom("minjae");
    }

    private void SynchronizeLenderDebtState()
    {
        if (!lenderDebtStateInitialized ||
            !string.Equals(GetDirectorState("v21.debt_state_initialized"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            InitializeLenderDebtState();
        }

        bool mom = IsBorrowedFrom("mom");
        bool seojun = IsBorrowedFrom("seojun");
        bool minjae = IsBorrowedFrom("minjae");

        bool changed = false;
        if (mom && !observedBorrowedMom)
        {
            AddLenderDebt("mom", 50000);
            changed = true;
        }
        if (seojun && !observedBorrowedSeojun)
        {
            AddLenderDebt("seojun", 50000);
            changed = true;
        }
        if (minjae && !observedBorrowedMinjae)
        {
            AddLenderDebt("minjae", 50000);
            changed = true;
        }

        observedBorrowedMom = mom;
        observedBorrowedSeojun = seojun;
        observedBorrowedMinjae = minjae;
        if (changed)
        {
            SelectRemainingDebtOwner();
            InvokePrivate(director, "Save");
        }
    }

    private bool IsBorrowedFrom(string key)
    {
        return string.Equals(GetDirectorState("borrowed." + key), "true", StringComparison.OrdinalIgnoreCase);
    }

    private int GetLenderDebt(string key)
    {
        return Mathf.Max(0, GetDirectorInt("debt." + key));
    }

    private void AddLenderDebt(string key, int amount)
    {
        SetDirectorState("debt." + key,
            Mathf.Max(0, GetLenderDebt(key) + amount).ToString(CultureInfo.InvariantCulture));
    }

    private int RepaySpecificLender(string key, string displayName)
    {
        int lenderDebt = GetLenderDebt(key);
        if (lenderDebt <= 0 || flow.V3BankCash <= 0 || flow.CurrentDebt <= 0)
            return 0;

        int totalBefore = flow.CurrentDebt;
        int otherDebt = Mathf.Max(0, totalBefore - lenderDebt);

        // GameFlowManager's public repayment method pays against its single total-debt field. Limit
        // that field to the selected lender for the duration of the call, then restore the others.
        SetField(flow, "debt", lenderDebt);
        int repaid = flow.V3RepayAvailableDebt(displayName + "에게 빌린 돈 상환");
        int lenderRemaining = Mathf.Max(0, lenderDebt - repaid);
        SetDirectorState("debt." + key, lenderRemaining.ToString(CultureInfo.InvariantCulture));
        SetField(flow, "debt", otherDebt + lenderRemaining);

        SelectRemainingDebtOwner();
        flow.V3Refresh();
        InvokePrivate(director, "Save");
        return repaid;
    }

    private void SelectRemainingDebtOwner()
    {
        string next = GetLenderDebt("seojun") > 0 ? "seojun"
            : GetLenderDebt("minjae") > 0 ? "minjae"
            : GetLenderDebt("mom") > 0 ? "mom"
            : "none";
        SetDirectorState("debt_owner", next);
    }

    private List<LenderInfo> GetOutstandingLenders()
    {
        var result = new List<LenderInfo>();
        if (IsBorrowedFrom("seojun") && GetLenderDebt("seojun") > 0)
            result.Add(new LenderInfo("seojun", "서준", SpeakerType.Joonho));
        if (IsBorrowedFrom("minjae") && GetLenderDebt("minjae") > 0)
            result.Add(new LenderInfo("minjae", "민재", SpeakerType.Friend));
        if (IsBorrowedFrom("mom") && GetLenderDebt("mom") > 0)
            result.Add(new LenderInfo("mom", "엄마", SpeakerType.Mom));
        return result;
    }

    private void TryOfferPostJobRepayment()
    {
        string promptStateKey = "v21.post_job_repay_prompted." + flow.CurrentDay.ToString(CultureInfo.InvariantCulture);
        if (postJobRepaymentPromptedDay == flow.CurrentDay ||
            string.Equals(GetDirectorState(promptStateKey), "true", StringComparison.OrdinalIgnoreCase) ||
            choiceOverlay == null || choiceOverlay.activeSelf)
            return;
        if (!flow.IsWeekend || flow.CurrentLocation != "집" || flow.CurrentHour < 21)
            return;
        if (!string.Equals(GetDirectorState("schedule.job"), "complete", StringComparison.OrdinalIgnoreCase))
            return;
        if (flow.CurrentDebt <= 0 || flow.V3BankCash <= 0 || !IsDirectorIdle() ||
            director.HasPendingMessageAction || dialogue.TotalUnreadCount > 0 || HasChatActionChoices())
            return;

        List<LenderInfo> lenders = GetOutstandingLenders();
        if (lenders.Count == 0)
            return;

        postJobRepaymentPromptedDay = flow.CurrentDay;
        SetDirectorState(promptStateKey, "true");
        InvokePrivate(director, "Save");
        ShowManualChoiceOverlay("알바비가 들어왔다. 빌린 돈부터 갚을까?",
            new ManualChoiceOption("갚는다", () => ShowPostJobLenderSelection(lenders)),
            new ManualChoiceOption("지금은 미룬다", () => { }));
    }

    private void ShowPostJobLenderSelection(List<LenderInfo> lenders)
    {
        if (lenders == null || lenders.Count == 0)
            return;
        if (lenders.Count == 1)
        {
            RepayAfterJob(lenders[0]);
            return;
        }

        var options = new List<ManualChoiceOption>();
        foreach (LenderInfo lender in lenders.Take(3))
        {
            LenderInfo captured = lender;
            options.Add(new ManualChoiceOption(captured.displayName + "에게 갚는다",
                () => RepayAfterJob(captured)));
        }
        if (options.Count < 3)
            options.Add(new ManualChoiceOption("지금은 미룬다", () => { }));
        ShowManualChoiceOverlay("누구에게 먼저 갚을까?", options.ToArray());
    }

    private void RepayAfterJob(LenderInfo lender)
    {
        if (lender == null)
            return;

        int repaid = RepaySpecificLender(lender.key, lender.displayName);
        if (repaid <= 0)
        {
            flow.V3ShowDialogue("나", "(갚고 싶지만 지금 보낼 수 있는 돈이 없다....)", null);
            return;
        }

        int remaining = GetLenderDebt(lender.key);
        string message = remaining <= 0
            ? $"방금 {repaid:N0}원 보냈어. 늦어서 미안해."
            : $"방금 {repaid:N0}원 보냈어. 남은 {remaining:N0}원도 날짜 정해서 갚을게.";
        AddOutgoingChatMessage(lender.speaker, lender.displayName, message);
        flow.V3ShowDialogue("나",
            $"({lender.displayName}에게 {repaid:N0}원을 보내고 메시지를 남겼다.)", null);
    }

    private void AddOutgoingChatMessage(SpeakerType speaker, string displayName, string message)
    {
        if (dialogue == null || string.IsNullOrWhiteSpace(message))
            return;

        dialogue.EnsureConversation(speaker, displayName);
        Dictionary<SpeakerType, ChatChannel> channels =
            GetField<Dictionary<SpeakerType, ChatChannel>>(dialogue, "channels");
        if (channels == null || !channels.TryGetValue(speaker, out ChatChannel channel) || channel == null)
            return;

        channel.messageHistory.Add(new ChatMessageEntry { isPlayer = true, text = message });
        channel.lastMessage = message;
        dialogue.PreferConversation(speaker);
        dialogue.UpdateProfileUI(speaker);
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
        if (!dialogue.IsConversationOpen(speaker))
            return;
        dialogue.DismissEventChoices(speaker);

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

        manualChoiceMode = false;
        manualChoiceActions.Clear();
        choiceOverlayLine = line;
        choiceOverlayMessageSpeaker = messageSpeaker;
        choiceOverlayBusy = false;
        choiceOverlaySpeaker.text = "나";
        choiceOverlayBody.text = FormatThought(body);
        PlaceChoiceOverlayButtons(Mathf.Min(choices.Count, choiceOverlayButtons.Count));

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

    private void ShowManualChoiceOverlay(string body, params ManualChoiceOption[] options)
    {
        if (choiceOverlay == null || options == null || options.Length == 0 || choiceOverlay.activeSelf)
            return;

        manualChoiceMode = true;
        manualChoiceActions.Clear();
        choiceOverlayLine = null;
        choiceOverlayMessageSpeaker = SpeakerType.Unknown;
        choiceOverlayBusy = false;
        choiceOverlaySpeaker.text = "나";
        choiceOverlayBody.text = FormatThought(body);
        PlaceChoiceOverlayButtons(Mathf.Min(options.Length, choiceOverlayButtons.Count));

        for (int index = 0; index < choiceOverlayButtons.Count; index++)
        {
            Button button = choiceOverlayButtons[index];
            bool visible = index < options.Length && options[index] != null;
            button.gameObject.SetActive(visible);
            button.onClick.RemoveAllListeners();
            if (!visible)
                continue;

            ManualChoiceOption option = options[index];
            manualChoiceActions.Add(option.action);
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = option.label;
            int capturedIndex = manualChoiceActions.Count - 1;
            button.onClick.AddListener(() => ResolveManualChoice(capturedIndex));
        }

        choiceOverlay.SetActive(true);
        choiceOverlay.transform.SetAsLastSibling();
    }

    private void PlaceChoiceOverlayButtons(int visibleCount)
    {
        visibleCount = Mathf.Clamp(visibleCount, 1, choiceOverlayButtons.Count);
        float height = visibleCount == 1 ? 0.36f : visibleCount == 2 ? 0.29f : 0.23f;
        float gap = visibleCount == 3 ? 0.035f : 0.055f;
        float top = 0.86f;

        for (int index = 0; index < choiceOverlayButtons.Count; index++)
        {
            RectTransform rect = choiceOverlayButtons[index].GetComponent<RectTransform>();
            float yMax = top - index * (height + gap);
            float yMin = yMax - height;
            rect.anchorMin = new Vector2(0.63f, yMin);
            rect.anchorMax = new Vector2(0.96f, yMax);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TMP_Text label = choiceOverlayButtons[index].GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = 23f;
                label.fontSizeMax = 31f;
                label.textWrappingMode = TextWrappingModes.Normal;
                label.alignment = TextAlignmentOptions.Center;
            }
        }
    }

    private void ResolveManualChoice(int index)
    {
        if (!manualChoiceMode || choiceOverlayBusy || index < 0 || index >= manualChoiceActions.Count)
            return;

        choiceOverlayBusy = true;
        Action action = manualChoiceActions[index];
        choiceOverlay.SetActive(false);
        manualChoiceActions.Clear();
        manualChoiceMode = false;
        choiceOverlayBusy = false;
        action?.Invoke();
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
            string ownerKey = seojunRepay ? "seojun" : "minjae";
            string ownerName = seojunRepay ? "서준" : "민재";
            int repaid = RepaySpecificLender(ownerKey, ownerName);
            SetDirectorState("last_repayment", repaid.ToString(CultureInfo.InvariantCulture));
            if (seojunRepay && repaid > 0)
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
        boxRect.anchorMax = new Vector2(0.88f, 0.50f);
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
        bodyRect.anchorMin = new Vector2(0f, 0.12f);
        bodyRect.anchorMax = new Vector2(0.60f, 1f);
        bodyRect.offsetMin = new Vector2(34f, 20f);
        bodyRect.offsetMax = new Vector2(-18f, -72f);

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
    // Tablet-first dialogue input and right-side choices
    // ---------------------------------------------------------------------

    private void InstallTouchDialogueControls()
    {
        GameObject novelPanel = GetField<GameObject>(director, "novelPanel");
        if (novelPanel != null)
            novelTapAdvanceSurface = EnsurePanelTapButton(novelPanel, HandleNovelTap);

        GameObject narrationPanel = GetField<GameObject>(flow, "narrationPanel");
        if (narrationPanel != null)
            tabletTapAdvanceSurface = EnsurePanelTapButton(narrationPanel, HandleTabletNarrationTap);

        MaintainTouchDialogueControls();
        ApplyRightSideChoiceLayout();
    }

    private static Button EnsurePanelTapButton(GameObject panel, UnityEngine.Events.UnityAction action)
    {
        if (panel == null)
            return null;

        Image graphic = panel.GetComponent<Image>();
        if (graphic == null)
            graphic = panel.AddComponent<Image>();
        graphic.raycastTarget = true;

        Button button = panel.GetComponent<Button>();
        if (button == null)
            button = panel.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.targetGraphic = graphic;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);
        return button;
    }

    private void MaintainTouchDialogueControls()
    {
        if (novelTapAdvanceSurface == null)
        {
            GameObject novelPanel = GetField<GameObject>(director, "novelPanel");
            if (novelPanel != null)
                novelTapAdvanceSurface = EnsurePanelTapButton(novelPanel, HandleNovelTap);
        }
        if (tabletTapAdvanceSurface == null)
        {
            GameObject narrationPanel = GetField<GameObject>(flow, "narrationPanel");
            if (narrationPanel != null)
                tabletTapAdvanceSurface = EnsurePanelTapButton(narrationPanel, HandleTabletNarrationTap);
        }

        Button novelContinue = GetField<Button>(director, "continueButton");
        Button tabletContinue = GetField<Button>(flow, "narrationContinueButton");
        HideContinueButton(novelContinue, ref novelContinueCanvasGroup);
        HideContinueButton(tabletContinue, ref tabletContinueCanvasGroup);
    }

    private static void HideContinueButton(Button button, ref CanvasGroup group)
    {
        if (button == null)
        {
            group = null;
            return;
        }

        // UnityEngine.Object has a special "destroyed object == null" rule, while the C# ??
        // operator only checks the raw CLR reference. A cached CanvasGroup from a rebuilt Continue
        // button can therefore look non-null to ?? but still throw MissingComponentException.
        // Re-resolve the component against the current button every time the cache is stale.
        if (group == null || group.gameObject != button.gameObject)
        {
            group = button.gameObject.GetComponent<CanvasGroup>();
            if (group == null)
                group = button.gameObject.AddComponent<CanvasGroup>();
        }

        if (group == null)
            return;

        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;
    }

    private void HandleNovelTap()
    {
        if (Time.unscaledTime < nextDialogueTapAt || director == null)
            return;
        GameObject historyPanel = GetField<GameObject>(director, "historyPanel");
        if (historyPanel != null && historyPanel.activeInHierarchy)
            return;
        if (choiceOverlay != null && choiceOverlay.activeInHierarchy)
            return;

        Button choiceA = GetField<Button>(director, "choiceAButton");
        Button choiceB = GetField<Button>(director, "choiceBButton");
        Button choiceC = GetField<Button>(director, "choiceCButton");
        if (IsVisibleChoice(choiceA) || IsVisibleChoice(choiceB) || IsVisibleChoice(choiceC))
            return;

        Button continueButton = GetField<Button>(director, "continueButton");
        if (continueButton == null || !continueButton.gameObject.activeInHierarchy)
            return;
        nextDialogueTapAt = Time.unscaledTime + 0.16f;
        continueButton.onClick.Invoke();
    }

    private void HandleTabletNarrationTap()
    {
        if (Time.unscaledTime < nextDialogueTapAt || flow == null)
            return;
        if (choiceOverlay != null && choiceOverlay.activeInHierarchy)
            return;
        Button continueButton = GetField<Button>(flow, "narrationContinueButton");
        if (continueButton == null || !continueButton.gameObject.activeInHierarchy)
            return;
        nextDialogueTapAt = Time.unscaledTime + 0.16f;
        continueButton.onClick.Invoke();
    }

    private static bool IsVisibleChoice(Button button)
    {
        return button != null && button.gameObject.activeInHierarchy &&
               !string.IsNullOrWhiteSpace(button.GetComponentInChildren<TMP_Text>(true)?.text);
    }

    private void ApplyRightSideChoiceLayout()
    {
        Button[] buttons =
        {
            GetField<Button>(director, "choiceAButton"),
            GetField<Button>(director, "choiceBButton"),
            GetField<Button>(director, "choiceCButton")
        };
        List<Button> visible = buttons.Where(IsVisibleChoice).ToList();
        if (visible.Count > 0)
        {
            float height = visible.Count == 1 ? 0.36f : visible.Count == 2 ? 0.29f : 0.23f;
            float gap = visible.Count == 3 ? 0.035f : 0.055f;
            float top = 0.88f;
            for (int i = 0; i < visible.Count; i++)
            {
                RectTransform rect = visible[i].GetComponent<RectTransform>();
                float yMax = top - i * (height + gap);
                rect.anchorMin = new Vector2(0.64f, yMax - height);
                rect.anchorMax = new Vector2(0.96f, yMax);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                TMP_Text label = visible[i].GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.enableAutoSizing = true;
                    label.fontSizeMin = 22f;
                    label.fontSizeMax = 31f;
                    label.textWrappingMode = TextWrappingModes.Normal;
                    label.alignment = TextAlignmentOptions.Center;
                }
            }
        }

        TMP_Text body = GetField<TMP_Text>(director, "bodyText");
        if (body != null)
        {
            RectTransform rect = body.rectTransform;
            rect.anchorMin = new Vector2(0f, 0.12f);
            rect.anchorMax = new Vector2(visible.Count > 0 ? 0.61f : 1f, 1f);
            rect.offsetMin = new Vector2(34f, 20f);
            rect.offsetMax = new Vector2(visible.Count > 0 ? -18f : -34f, -72f);
        }

        if (choiceOverlay != null && choiceOverlay.activeInHierarchy)
        {
            int manualVisible = choiceOverlayButtons.Count(button => button != null && button.gameObject.activeInHierarchy);
            if (manualVisible > 0)
                PlaceChoiceOverlayButtons(manualVisible);
        }
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
        choiceOverlayMessageSpeaker = SpeakerType.Unknown;
        manualChoiceMode = false;
        manualChoiceActions.Clear();
        explicitBorrowPending = string.Equals(GetDirectorState("pending.borrow_menu"), "true",
            StringComparison.OrdinalIgnoreCase);
        explicitBorrowRequestDay = GetDirectorInt("v22.borrow_requested_day");
        if (explicitBorrowPending && explicitBorrowRequestDay <= 0 && flow.CurrentHour <= 10)
            explicitBorrowRequestDay = Mathf.Max(0, flow.CurrentDay - 1);
        borrowOverlayShownForCurrentDay = false;
        postJobRepaymentPromptedDay = -1;
        lenderDebtStateInitialized = false;
        InitializeLenderDebtState();
        lastDay = flow.CurrentDay;
        lateMapCueShownDay = -1;
        lastObservedApp = appWindow?.CurrentAppType;
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // ---------------------------------------------------------------------
    // QA shortcuts (never compiled into a non-development release build)
    // ---------------------------------------------------------------------

    private void HandleQaShortcuts()
    {
#if ENABLE_INPUT_SYSTEM
        UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.f8Key.wasPressedThisFrame)
            PrepareOvernightQaState(false);
        else if (keyboard.f9Key.wasPressedThisFrame)
            PrepareOvernightQaState(true);
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            PrepareOvernightQaState(false);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.F9))
            PrepareOvernightQaState(true);
#endif
    }

    private void PrepareOvernightQaState(bool weekendMorning)
    {
        if (flow == null || director == null || !IsDirectorIdle() ||
            (choiceOverlay != null && choiceOverlay.activeSelf))
        {
            flow?.V3ShowDialogue("QA", "(진행 중인 대사나 선택지를 먼저 끝낸 뒤 다시 눌러 주세요.)", null);
            return;
        }

        // F8: 2일차 목요일 06:00 -> 한 판 뒤 3일차 평일 아침 검증
        // F9: 10일차 금요일 06:00 -> 한 판 뒤 11일차 주말 아침 검증
        int targetDay = weekendMorning ? 10 : 2;
        appWindow?.CloseCurrentApp();
        choiceOverlay?.SetActive(false);
        dialogue.ResetScenarioConversations();
        notifications?.Clear();
        explicitBorrowPending = false;
        explicitBorrowRequestDay = -1;
        borrowOverlayShownForCurrentDay = false;
        flow.V3RestoreRun(targetDay, 6, "집", 100000, 0);

        SetDirectorState("schedule.school", "complete");
        SetDirectorState("schedule.homework", "complete");
        SetDirectorState("schedule.job", "complete");
        SetDirectorState("schedule.sleep", "pending");
        SetDirectorState("day_finalized", "0");
        SetDirectorState("counter.gamble_sessions", "0");
        SetDirectorState("pending.gamble_attention", "true");
        SetDirectorState("pending.borrow_menu", "false");
        SetDirectorState("v22.borrow_requested_day", "0");
        SetDirectorState("pending.borrow_target", "none");
        SetDirectorState("flag.borrow_deferred", "false");
        SetDirectorState("flag.gambled_late", "false");
        SetDirectorState("flag.late_wake_today", "false");
        SetDirectorState("flag.gambling_started", "false");
        SetDirectorState("flag.gambling_app_unlocked", "true");
        SetDirectorState("borrowed.mom", "false");
        SetDirectorState("borrowed.seojun", "false");
        SetDirectorState("borrowed.minjae", "false");
        SetDirectorState("debt.mom", "0");
        SetDirectorState("debt.seojun", "0");
        SetDirectorState("debt.minjae", "0");
        SetDirectorState("debt_owner", "none");
        SetField(director, "pendingLateWakeAfterGambling", false);
        SetField(director, "pendingBorrowMorningAdvance", false);

        HashSet<string> seen = GetField<HashSet<string>>(director, "seenScenes");
        seen?.RemoveWhere(value =>
            value.StartsWith("sys_late_gamble_morning", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("gamble_1", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("late_first_gamble_return", StringComparison.OrdinalIgnoreCase));

        lenderDebtStateInitialized = false;
        InitializeLenderDebtState();
        lastDay = targetDay;
        lateMapCueShownDay = -1;
        postJobRepaymentPromptedDay = -1;
        flow.V3SetGamblingUnlocked(true, true);
        flow.V3SetGamblingAttention(true);
        flow.V3Refresh();
        InvokePrivate(director, "Save");

        string result = weekendMorning
            ? "F9 주말 밤샘 QA 준비 완료. 지금 도박 앱을 눌러 ‘한다’를 선택하면 11일차 토요일 오전 흐름을 확인할 수 있습니다."
            : "F8 평일 밤샘 QA 준비 완료. 지금 도박 앱을 눌러 ‘한다’를 선택하면 3일차 금요일 오전 흐름을 확인할 수 있습니다.";
        flow.V3ShowDialogue("QA", result, null);
    }
#endif

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

    private sealed class ManualChoiceOption
    {
        public readonly string label;
        public readonly Action action;

        public ManualChoiceOption(string label, Action action)
        {
            this.label = label ?? string.Empty;
            this.action = action;
        }
    }

    private sealed class LenderInfo
    {
        public readonly string key;
        public readonly string displayName;
        public readonly SpeakerType speaker;

        public LenderInfo(string key, string displayName, SpeakerType speaker)
        {
            this.key = key ?? string.Empty;
            this.displayName = displayName ?? string.Empty;
            this.speaker = speaker;
        }
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
