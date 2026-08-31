using System;
using System.Collections;
using System.Collections.Generic;
using Dobak.App.Casino;
using Dobak.Manager;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class GameFlowManager : MonoBehaviour
{
    public static GameFlowManager Instance { get; private set; }

    private const int FinalDay = 14;
    private const int DayStartHour = 7;
    private const int CollapseFailureLimit = 3;
    private const int DebtEndingThreshold = 150000;
    private const int SchoolOpeningHour = 8;
    private const int SchoolArrivalDeadline = 10;
    private const int JobStartHour = 8;
    private const int JobEndHour = 16;
    private const int JobDailyWage = 80000;
    private const int MinimumSleepHours = 5;
    private const int ShortSleepEndingLimit = 3;
    private const int CashOutEndingBalance = 10000;
    private const int CashOutEndingAttempts = 3;

    private readonly HashSet<string> sentMessages = new HashSet<string>();
    private readonly HashSet<string> firedScenarioEvents = new HashSet<string>();
    private readonly Dictionary<string, int> lastScenarioEventDay = new Dictionary<string, int>();

    private QuizManager quizManager;
    private NotificationManager notificationManager;
    private AppWindow appWindow;
    private CoinManager coinManager;
    private DialogueManager dialogueManager;
    private ScenarioMessageTable scenarioMessages;

    private TMP_Text moneyText;
    private TMP_Text feedbackText;
    private GameObject feedbackPanel;
    private CanvasGroup feedbackGroup;
    private TMP_Text fadeCaption;
    private Button sleepButton;
    private Button helpButton;
    private Button loanButton;
    private Button repayDebtButton;
    private Button cashOutButton;
    private CanvasGroup fadeGroup;
    private GameObject endPanel;
    private TMP_Text endTitleText;
    private TMP_Text endBodyText;
    private Button restartButton;
    private GameObject narrationPanel;
    private TMP_Text narrationTitleText;
    private TMP_Text narrationBodyText;
    private Button narrationContinueButton;
    private Coroutine feedbackCoroutine;
    private GameObject actionBar;
    private GameObject gamblingAppIcon;
    private GameObject borrowChoicePanel;
    private Button momBorrowButton;
    private Button friendBorrowButton;
    private TMP_Text snsStatusText;
    private RawImage snsFeedImage;
    private readonly List<TMP_Text> dateTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> clockTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> locationTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> homeChecklistLines = new List<TMP_Text>();

    private int currentDay = 1;
    private int currentHour = DayStartHour;
    private int scheduleFailureDays;
    private int consecutiveShortSleepDays;
    private int debt;
    private int gambleRounds;
    private int gambleLosses;
    private int cashOutAttempts;
    private int casinoChargesToday;
    private int snsHoursToday;
    private string currentLocation = "집";
    private string activeStoryEvent = "";
    private readonly Queue<(string title, string body)> narrationQueue = new Queue<(string title, string body)>();

    private bool schoolDone;
    private bool homeworkDone;
    private bool jobDone;
    private bool sleepDone;
    private bool invitationResolved;
    private bool gamblingUnlocked;
    private bool isTransitioning;
    private bool gameEnded;
    private bool momBorrowRequested;
    private bool friendBorrowRequested;

    public bool IsWeekend => GetWeekdayIndex(currentDay) >= 5;
    public bool IsGameEnded => gameEnded;
    public bool IsGamblingUnlocked => gamblingUnlocked;
    public bool CanRequestHelp => gamblingUnlocked && debt > 0 && !gameEnded;
    public bool CanRepayDebt => debt > 0 && !gameEnded && coinManager != null && coinManager.BankCash > 0;
    public int CurrentDebt => debt;
    public int CurrentDay => currentDay;
    public int CurrentHour => currentHour;
    public string CurrentLocation => currentLocation;
    public bool IsHomeworkDone => homeworkDone;
    public int ConsecutiveShortSleepDays => consecutiveShortSleepDays;
    public string ActiveStoryEvent => activeStoryEvent;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterSceneBootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "TabletUI")
            return;

        if (FindAnyObjectByType<GameFlowManager>() != null)
            return;

        new GameObject("Game Flow Manager").AddComponent<GameFlowManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private IEnumerator Start()
    {
        yield return null;

        quizManager = FindAnyObjectByType<QuizManager>(FindObjectsInactive.Include);
        notificationManager = FindAnyObjectByType<NotificationManager>(FindObjectsInactive.Include);
        dialogueManager = FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
        appWindow = FindAnyObjectByType<AppWindow>();
        coinManager = CoinManager.Instance ?? FindAnyObjectByType<CoinManager>();
        scenarioMessages = ScenarioMessageTable.Load();

        ApplyKoreanFont();
        BindExistingStatusText();
        CreateRuntimeUI();
        gamblingAppIcon = FindSceneObject("BrowserApp");
        SetGamblingAppVisibility(false);

        if (appWindow != null)
            appWindow.AppChanged += OnAppChanged;

        if (quizManager != null)
        {
            quizManager.DailyQuizCompleted += CompleteHomework;
            quizManager.ConfigureForDay(currentDay, !IsWeekend);
        }

        Dobak.App.Casino.SlotMachine.SlotMachineManager.SpinResolved += OnGambleResolved;

        if (coinManager != null)
        {
            coinManager.OnBankCashChanged += OnBankCashChanged;
            coinManager.OnCasinoCashChanged += OnCasinoCashChanged;
            coinManager.OnCasinoChargeCompleted += OnCasinoChargeCompleted;
        }

        StartNewDay(false);
        yield return new WaitForSeconds(0.8f);
        TriggerScenario("game_start");
    }

    private void OnDestroy()
    {
        if (quizManager != null)
            quizManager.DailyQuizCompleted -= CompleteHomework;

        Dobak.App.Casino.SlotMachine.SlotMachineManager.SpinResolved -= OnGambleResolved;

        if (coinManager != null)
        {
            coinManager.OnBankCashChanged -= OnBankCashChanged;
            coinManager.OnCasinoCashChanged -= OnCasinoCashChanged;
            coinManager.OnCasinoChargeCompleted -= OnCasinoChargeCompleted;
        }

        if (appWindow != null)
            appWindow.AppChanged -= OnAppChanged;

        if (Instance == this)
            Instance = null;
    }

    public bool CanSpendTime(int hours)
    {
        return !gameEnded && !isTransitioning && hours > 0;
    }

    public void SpendTime(int hours, string reason)
    {
        if (!CanSpendTime(hours))
            return;

        AdvanceHours(hours);
        ShowFeedback($"{reason}: {hours}시간이 지났다.");
    }

    public void TravelTo(string rawLocation)
    {
        if (gameEnded || isTransitioning)
            return;

        string location = NormalizeLocation(rawLocation);
        int travelHours = GetTravelHours(location);

        if (location == "학교" && !IsWeekend && !schoolDone)
        {
            int arrivalHour = currentHour + travelHours;
            if (arrivalHour < SchoolOpeningHour)
            {
                ShowFeedback("학교에는 오전 8시부터 갈 수 있습니다.");
                return;
            }

            if (arrivalHour > SchoolArrivalDeadline)
            {
                ShowFeedback("오전 10시까지 도착할 수 없어 오늘은 등교할 수 없습니다.");
                TriggerScenario("school_late");
                return;
            }
        }

        else if (location == "카페" && IsWeekend && !jobDone)
        {
            int arrivalHour = currentHour + travelHours;
            if (arrivalHour < JobStartHour)
            {
                ShowFeedback("알바는 오전 8시에 시작합니다.");
                return;
            }

            if (arrivalHour > JobStartHour)
            {
                ShowFeedback("오전 8시 출근 시간을 지나 오늘은 근무할 수 없습니다.");
                TriggerScenario("job_late");
                return;
            }
        }

        if (location == currentLocation)
        {
            ShowFeedback($"현재 {location}에 있습니다.");
            appWindow?.CloseCurrentApp();
            return;
        }

        StartCoroutine(FadeTransition($"{location}(으)로 이동 중", () =>
        {
            currentLocation = location;
            AdvanceHours(travelHours);

            if (location == "학교" && !IsWeekend && !schoolDone)
            {
                schoolDone = true;
                AdvanceHours(7);
                TriggerScenario("school_complete");
            }
            else if (location == "카페" && IsWeekend && !jobDone)
            {
                jobDone = true;
                AdvanceHours(JobEndHour - JobStartHour);
                coinManager?.AddBankCash(JobDailyWage, "카페 아르바이트 일당");
                currentLocation = "집";
                TriggerScenario("job_complete", new Dictionary<string, string>
                {
                    ["wage"] = JobDailyWage.ToString("N0")
                });
            }

            appWindow?.CloseCurrentApp();
            RefreshUI();
        }));
    }

    public void ResolveInvitation(bool accepted)
    {
        if (invitationResolved || gameEnded)
            return;

        invitationResolved = true;
        gamblingUnlocked = accepted;

        if (!accepted)
        {
            TriggerScenario("invitation_decline");
            return;
        }

        coinManager?.AddCasinoCredit(5000);
        SetGamblingAppVisibility(true);
        TriggerScenario("invitation_accept");
    }

    public void RequestHelp()
    {
        if (!CanRequestHelp)
            return;

        TriggerScenario("help_requested");
    }

    public void RepayDebt()
    {
        if (gameEnded || debt <= 0 || coinManager == null)
            return;

        int repayment = Mathf.Min(debt, coinManager.BankCash);
        if (repayment <= 0)
        {
            ShowFeedback("상환할 수 있는 통장 잔액이 없다.");
            return;
        }

        if (!coinManager.TrySpendBankCash(repayment, "빌린 돈 상환", TransactionScope.DebtRepayment))
            return;

        debt -= repayment;
        TriggerScenario("debt_repaid");
        ShowFeedback(debt == 0
            ? $"{repayment:N0}원을 갚아 빌린 돈을 모두 정리했다."
            : $"{repayment:N0}원을 갚았다. 아직 갚을 돈이 남아 있다.");
        RefreshUI();
    }

    private void CompleteHomework(int correctAnswers, int totalQuestions)
    {
        if (homeworkDone || IsWeekend || gameEnded)
            return;

        homeworkDone = true;
        AdvanceHours(2);
        TriggerScenario("homework_complete");
    }

    public bool CanOpenStudy()
    {
        if (gameEnded)
            return false;

        if (!IsWeekend && !schoolDone)
        {
            ShowFeedback("학교 수업을 마친 뒤 오늘의 숙제를 풀 수 있습니다.");
            TriggerScenario("study_locked");
            return false;
        }

        return true;
    }

    public int GetTravelHours(string rawLocation)
    {
        string location = NormalizeLocation(rawLocation);
        return location == currentLocation ? 0 : 1;
    }

    private void Sleep()
    {
        if (gameEnded || isTransitioning)
            return;

        int sleepHours = GetSleepHoursUntilSeven(currentHour);
        sleepDone = true;
        RefreshUI();

        StartCoroutine(FadeTransition("하루를 마무리하는 중", () =>
        {
            currentLocation = "집";
            CompleteDayAtSeven(true, sleepHours);
        }, 0.55f));
    }

    public static int GetSleepHoursUntilSeven(int hour)
    {
        int normalizedHour = ((hour % 24) + 24) % 24;
        int hours = normalizedHour < DayStartHour
            ? DayStartHour - normalizedHour
            : 24 - normalizedHour + DayStartHour;
        return Mathf.Clamp(hours, 1, 24);
    }

    private void RegisterSleep(int hours)
    {
        if (hours < MinimumSleepHours)
        {
            consecutiveShortSleepDays++;
            TriggerScenario("short_sleep", new Dictionary<string, string> { ["hours"] = hours.ToString() });
        }
        else
        {
            consecutiveShortSleepDays = 0;
        }

        if (consecutiveShortSleepDays >= ShortSleepEndingLimit)
            TriggerScenario("ending_sleep");
    }

    private void CompleteDayAtSeven(bool slept, int sleepHours)
    {
        TriggerScenario("day_end", new Dictionary<string, string>
        {
            ["dinner_success"] = (currentLocation == "집" && currentHour <= 21).ToString().ToLowerInvariant()
        });
        activeStoryEvent = "";

        bool requiredDone = IsWeekend ? jobDone : schoolDone && homeworkDone;
        if (!requiredDone)
        {
            scheduleFailureDays++;
            SendScheduleWarning();
        }

        RegisterSleep(slept ? sleepHours : 0);
        if (gameEnded)
            return;

        if (scheduleFailureDays >= CollapseFailureLimit)
        {
            TriggerScenario("ending_collapse");
            return;
        }

        if (debt >= DebtEndingThreshold)
        {
            TriggerScenario("ending_debt");
            return;
        }

        currentDay++;
        if (currentDay > FinalDay)
        {
            TriggerScenario("ending_recovery");
            return;
        }

        currentHour = DayStartHour;
        StartNewDay(true);

        TriggerScenario("day_transition", new Dictionary<string, string>
        {
            ["slept"] = slept.ToString().ToLowerInvariant(),
            ["hours"] = sleepHours.ToString()
        });
    }

    private void StartNewDay(bool sendDailyMessage)
    {
        schoolDone = false;
        homeworkDone = false;
        jobDone = false;
        sleepDone = false;
        casinoChargesToday = 0;
        snsHoursToday = 0;

        quizManager?.ConfigureForDay(currentDay, !IsWeekend);

        if (sendDailyMessage)
            TriggerScenario("day_start");

        RefreshUI();
    }

    private void AdvanceHours(int hours)
    {
        int remaining = Mathf.Max(0, hours);
        bool wasEjectedFromSchool = false;
        while (remaining > 0 && !gameEnded)
        {
            int untilBoundary = GetHoursUntilDayBoundary(currentHour);
            int normalizedHour = ((currentHour % 24) + 24) % 24;
            if (currentLocation == "학교" && normalizedHour >= 20)
            {
                currentLocation = "집";
                wasEjectedFromSchool = true;
            }

            if (currentLocation == "학교" && normalizedHour >= DayStartHour && normalizedHour < 20)
            {
                int untilSchoolCloses = 20 - normalizedHour;
                if (remaining >= untilSchoolCloses && untilSchoolCloses < untilBoundary)
                {
                    currentHour = 20;
                    remaining -= untilSchoolCloses;
                    currentLocation = "집";
                    wasEjectedFromSchool = true;
                    continue;
                }
            }

            if (remaining < untilBoundary)
            {
                currentHour = (currentHour + remaining) % 24;
                remaining = 0;
                break;
            }

            remaining -= untilBoundary;
            currentHour = DayStartHour;
            sleepDone = false;
            CompleteDayAtSeven(false, 0);
        }

        RefreshUI();
        if (wasEjectedFromSchool && !gameEnded)
            TriggerScenario("school_closed");
        if (!gameEnded)
            TriggerScenario("time_changed");
    }

    public static int GetHoursUntilDayBoundary(int hour)
    {
        int normalizedHour = ((hour % 24) + 24) % 24;
        return normalizedHour < DayStartHour
            ? DayStartHour - normalizedHour
            : 24 - normalizedHour + DayStartHour;
    }

    private void SendScheduleWarning()
    {
        TriggerScenario("schedule_missed");
    }

    private void OnGambleResolved(bool won, int payout)
    {
        gambleRounds++;
        if (!won)
            gambleLosses++;
        TriggerScenario("gamble_resolved", new Dictionary<string, string>
        {
            ["won"] = won.ToString().ToLowerInvariant(),
            ["payout"] = payout.ToString()
        });
    }

    private void ShowBorrowChoices()
    {
        if (gameEnded || coinManager == null || coinManager.BankCash > 0 || borrowChoicePanel == null)
            return;

        momBorrowButton.interactable = !momBorrowRequested;
        friendBorrowButton.interactable = !friendBorrowRequested;
        borrowChoicePanel.SetActive(true);
        borrowChoicePanel.transform.SetAsLastSibling();
    }

    private void AskMomForMoney()
    {
        if (momBorrowRequested)
            return;

        momBorrowRequested = true;
        borrowChoicePanel.SetActive(false);
        TriggerScenario("borrow_mom_request");
        appWindow?.OpenMessage();
    }

    private void AskFriendForMoney()
    {
        if (friendBorrowRequested)
            return;

        friendBorrowRequested = true;
        borrowChoicePanel.SetActive(false);
        TriggerScenario("borrow_friend_request");
        appWindow?.OpenMessage();
    }

    public void ResolveMomLoan(bool accepted)
    {
        if (accepted)
        {
            coinManager?.AddBankCash(15000, "엄마에게 빌린 돈");
            debt += 15000;
            TriggerScenario("borrow_mom_result", new Dictionary<string, string> { ["accepted"] = "true" });
            UnlockHelpStory();
        }
        else
        {
            TriggerScenario("borrow_mom_result", new Dictionary<string, string> { ["accepted"] = "false" });
        }

        RefreshUI();
    }

    public void ResolveFriendLoan(bool accepted)
    {
        if (accepted)
        {
            coinManager?.AddBankCash(25000, "민재에게 빌린 돈");
            debt += 25000;
            TriggerScenario("borrow_friend_result", new Dictionary<string, string> { ["accepted"] = "true" });
            UnlockHelpStory();
        }
        else
        {
            TriggerScenario("borrow_friend_result", new Dictionary<string, string> { ["accepted"] = "false" });
        }

        RefreshUI();
    }

    private void UnlockHelpStory()
    {
        TriggerScenario("debt_created");

        if (debt >= DebtEndingThreshold)
            TriggerScenario("ending_debt");
    }

    public bool CanAttemptCashOut => !gameEnded && coinManager != null && coinManager.CasinoCash > 0;
    public bool WillCashOutScam => CanAttemptCashOut &&
                                   (coinManager.CasinoCash >= CashOutEndingBalance ||
                                    cashOutAttempts + 1 >= CashOutEndingAttempts);

    public void AttemptCashOut()
    {
        if (gameEnded || coinManager == null)
            return;

        if (!CanAttemptCashOut)
        {
            ShowFeedback("환전할 사이트 포인트가 없다.");
            return;
        }

        cashOutAttempts++;
        if (coinManager.CasinoCash >= CashOutEndingBalance || cashOutAttempts >= CashOutEndingAttempts)
        {
            TriggerScenario("cashout_scam");
            return;
        }

        if (!coinManager.TryCashOutCasino(out int points, out int won))
            return;

        TriggerScenario("cashout_success", new Dictionary<string, string>
        {
            ["points"] = points.ToString("N0"),
            ["won"] = won.ToString("N0")
        });
        ShowFeedback($"{points:N0}P가 {won:N0}원으로 정상 환전되었다.");
        FindAnyObjectByType<CasinoUIManager>(FindObjectsInactive.Include)?.ReturnToHomeAfterCashOut();
        RefreshUI();
    }

    private void OnBankCashChanged(int value)
    {
        TriggerScenario("bank_changed");
        RefreshUI();
    }

    private void OnCasinoChargeCompleted(int won, int points)
    {
        casinoChargesToday++;
        TriggerScenario("casino_charge");

        RefreshUI();
    }

    private void OnCasinoCashChanged(int value)
    {
        RefreshUI();
    }

    private void OnAppChanged(AppType? openedApp)
    {
        if (actionBar != null)
            actionBar.SetActive(openedApp == null);
        if (openedApp != null && borrowChoicePanel != null)
            borrowChoicePanel.SetActive(false);

        if (openedApp == null)
            return;

        if (openedApp == AppType.Map)
            TriggerScenario("app_map_open");
        else if (openedApp == AppType.Message)
            TriggerScenario("app_message_open");
        else if (openedApp == AppType.Bank)
            TriggerScenario("app_bank_open");
        else if (openedApp == AppType.SNS)
            TriggerScenario("app_sns_open");
    }

    private void SetGamblingAppVisibility(bool visible)
    {
        if (gamblingAppIcon != null)
            gamblingAppIcon.SetActive(visible);
    }

    public void StartInvitationRetempt()
    {
        if (invitationResolved || gameEnded)
            return;
        TriggerScenario("invitation_retempt");
    }

    public void ContinueInvitation()
    {
        if (!invitationResolved && !gameEnded)
            TriggerScenario("invitation_detail");
    }

    public void ExecuteScenarioAction(string action)
    {
        const string triggerPrefix = "trigger:";
        if (string.IsNullOrWhiteSpace(action) ||
            !action.StartsWith(triggerPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        string trigger = action.Substring(triggerPrefix.Length).Trim();
        if (trigger.Length > 0)
            TriggerScenario(trigger);
    }

    private void ShowNextNarration()
    {
        if (narrationPanel == null || isTransitioning || narrationPanel.activeSelf || narrationQueue.Count == 0)
            return;

        (string title, string body) = narrationQueue.Dequeue();
        narrationTitleText.text = title;
        narrationBodyText.text = body;
        narrationPanel.SetActive(true);
        narrationPanel.transform.SetAsLastSibling();
    }

    private void CloseNarration()
    {
        if (narrationPanel == null)
            return;

        narrationPanel.SetActive(false);
        ShowNextNarration();
    }

    private void SendOnce(string key, string title, string message, SpeakerType speaker)
    {
        if (!sentMessages.Add(key))
            return;

        var data = new NotificationData
        {
            title = title,
            message = message,
            appType = AppType.Message,
            speakerType = speaker
        };

        if (notificationManager != null)
            notificationManager.SendNotification(data);
        else
            dialogueManager?.ReceiveNotificationMessage(speaker, title, message);
    }

    private IEnumerator FadeTransition(string caption, Action midpoint, float hold = 0.3f)
    {
        if (fadeGroup == null)
        {
            midpoint?.Invoke();
            yield break;
        }

        isTransitioning = true;
        fadeGroup.blocksRaycasts = true;
        fadeCaption.text = caption;

        yield return FadeTo(1f, 0.3f);
        midpoint?.Invoke();
        yield return new WaitForSeconds(hold);
        yield return FadeTo(0f, 0.35f);

        fadeGroup.blocksRaycasts = false;
        isTransitioning = false;
        RefreshUI();
        ShowNextNarration();
    }

    private IEnumerator FadeTo(float target, float duration)
    {
        float start = fadeGroup.alpha;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            fadeGroup.alpha = Mathf.Lerp(start, target, elapsed / duration);
            yield return null;
        }

        fadeGroup.alpha = target;
    }

    private void EndGame(string title, string body)
    {
        if (gameEnded)
            return;

        gameEnded = true;
        narrationQueue.Clear();
        if (narrationPanel != null)
            narrationPanel.SetActive(false);
        if (feedbackCoroutine != null)
        {
            StopCoroutine(feedbackCoroutine);
            feedbackCoroutine = null;
        }
        if (feedbackGroup != null)
            feedbackGroup.alpha = 0f;
        notificationManager?.Clear();

        if (endPanel != null)
        {
            endTitleText.text = title;
            endBodyText.text = body + "\n\n도박 문제 예방·상담 1336";
            endPanel.SetActive(true);
            endPanel.transform.SetAsLastSibling();
        }

        RefreshUI();
    }

    public void RestartGame()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    private void RefreshUI()
    {
        string weekday = GetWeekdayName(currentDay);
        string meridiem = currentHour < 12 ? "오전" : "오후";
        int displayHour = currentHour % 12;
        if (displayHour == 0)
            displayHour = 12;

        string clock = $"{meridiem} {displayHour}:00";

        foreach (TMP_Text text in dateTexts)
            text.text = $"{currentDay}일차 · {weekday}";
        foreach (TMP_Text text in clockTexts)
            text.text = clock;
        foreach (TMP_Text text in locationTexts)
            text.text = $"현재위치 : {currentLocation}";

        UpdateHomeChecklist();

        if (sleepButton != null)
            sleepButton.interactable = !gameEnded && !isTransitioning;
        if (helpButton != null)
        {
            helpButton.gameObject.SetActive(CanRequestHelp);
            helpButton.interactable = CanRequestHelp;
        }
        if (loanButton != null)
        {
            bool canAskSomeone = !momBorrowRequested || !friendBorrowRequested;
            loanButton.gameObject.SetActive(!gameEnded && coinManager != null && coinManager.BankCash <= 0 && canAskSomeone);
            loanButton.interactable = debt < DebtEndingThreshold;
        }
        if (repayDebtButton != null)
        {
            repayDebtButton.gameObject.SetActive(CanRepayDebt);
            repayDebtButton.interactable = CanRepayDebt;
        }
        if (cashOutButton != null)
        {
            cashOutButton.gameObject.SetActive(false);
            cashOutButton.interactable = CanAttemptCashOut;
        }
    }

    private void BindExistingStatusText()
    {
        dateTexts.Clear();
        clockTexts.Clear();
        locationTexts.Clear();
        homeChecklistLines.Clear();

        TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
        foreach (TMP_Text text in texts)
        {
            if (!text.gameObject.scene.IsValid())
                continue;

            if (text.gameObject.name == "Date_Text")
                dateTexts.Add(text);
            if (text.gameObject.name == "Time_Text" || text.gameObject.name == "OverBarTime_Text" || text.text.Contains("99:99"))
                clockTexts.Add(text);
            if (text.text.Contains("현재위치") || text.text.Contains("현재 위치"))
                locationTexts.Add(text);
            if (text.gameObject.activeInHierarchy && text.gameObject.name.StartsWith("Daliy_Text"))
                homeChecklistLines.Add(text);
        }

        homeChecklistLines.Sort((left, right) => right.rectTransform.anchoredPosition.y.CompareTo(left.rectTransform.anchoredPosition.y));

        RefreshUI();
    }

    private void UpdateHomeChecklist()
    {
        if (homeChecklistLines.Count == 0)
            return;

        string[] lines = IsWeekend
            ? new[]
            {
                $"{Mark(jobDone)} 알바 가기  (08:00~16:00 · 카페)",
                $"{Mark(sleepDone)} 잠자기  (최소 5시간)",
                CanRequestHelp ? "[  ] 도움 요청  (빚이 생긴 지금 선택 가능)" : "",
                ""
            }
            : new[]
            {
                $"{Mark(schoolDone)} 학교 가기  (오전 10시 도착 마감)",
                $"{Mark(homeworkDone)} 숙제하기  (공부 · 5문제)",
                $"{Mark(sleepDone)} 잠자기  (최소 5시간)",
                CanRequestHelp ? "[  ] 도움 요청  (빚이 생긴 지금 선택 가능)" : ""
            };

        for (int i = 0; i < homeChecklistLines.Count; i++)
            homeChecklistLines[i].text = i < lines.Length ? lines[i] : "";
    }

    private void CreateRuntimeUI()
    {
        Canvas canvas = FindRootCanvas();
        if (canvas == null)
            return;

        TMP_FontAsset font = FindPreferredFont();

        CreateSnsApp(canvas, font);
        CreateSnsHomeIcon(font);
        CreateSettingsApp(canvas, font);

        GameObject panel = CreatePanel("Daily Action Bar", canvas.transform, new Color(0.035f, 0.075f, 0.13f, 0.92f));
        actionBar = panel;
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.zero;
        panelRect.pivot = Vector2.zero;
        panelRect.anchoredPosition = new Vector2(200f, 125f);
        panelRect.sizeDelta = new Vector2(750f, 90f);

        moneyText = CreateText("Money Text", panel.transform, font, 20, FontStyles.Normal, new Color(0.64f, 0.8f, 0.92f));
        moneyText.gameObject.SetActive(false);

        sleepButton = CreateButton("Sleep Button", panel.transform, font, "잠자기", new Color(0.16f, 0.45f, 0.78f));
        SetRect(sleepButton.GetComponent<RectTransform>(), new Vector2(20f, -16f), new Vector2(220f, 58f));
        sleepButton.onClick.AddListener(Sleep);

        helpButton = CreateButton("Help Button", panel.transform, font, "도움 요청", new Color(0.16f, 0.58f, 0.48f));
        SetRect(helpButton.GetComponent<RectTransform>(), new Vector2(255f, -16f), new Vector2(220f, 58f));
        helpButton.onClick.AddListener(RequestHelp);

        loanButton = CreateButton("Loan Button", panel.transform, font, "돈 부탁", new Color(0.72f, 0.36f, 0.18f));
        SetRect(loanButton.GetComponent<RectTransform>(), new Vector2(490f, -16f), new Vector2(115f, 58f));
        loanButton.onClick.AddListener(ShowBorrowChoices);

        repayDebtButton = CreateButton("Repay Debt Button", panel.transform, font, "빌린 돈 갚기", new Color(0.18f, 0.5f, 0.42f));
        SetRect(repayDebtButton.GetComponent<RectTransform>(), new Vector2(490f, -16f), new Vector2(220f, 58f));
        repayDebtButton.onClick.AddListener(RepayDebt);

        cashOutButton = CreateButton("Cashout Button", panel.transform, font, "환전 시도", new Color(0.66f, 0.28f, 0.3f));
        SetRect(cashOutButton.GetComponent<RectTransform>(), new Vector2(615f, -16f), new Vector2(115f, 58f));
        cashOutButton.onClick.AddListener(AttemptCashOut);

        borrowChoicePanel = CreatePanel("Borrow Choice Panel", canvas.transform, new Color(0.04f, 0.08f, 0.14f, 0.98f));
        RectTransform borrowRect = borrowChoicePanel.GetComponent<RectTransform>();
        borrowRect.anchorMin = new Vector2(0.5f, 0f);
        borrowRect.anchorMax = new Vector2(0.5f, 0f);
        borrowRect.pivot = new Vector2(0.5f, 0f);
        borrowRect.anchoredPosition = new Vector2(0f, 220f);
        borrowRect.sizeDelta = new Vector2(620f, 170f);

        TMP_Text borrowTitle = CreateText("Borrow Title", borrowChoicePanel.transform, font, 25, FontStyles.Bold, Color.white);
        borrowTitle.text = "누구에게 돈을 부탁할까?";
        borrowTitle.alignment = TextAlignmentOptions.Center;
        SetRect(borrowTitle.rectTransform, new Vector2(20f, -15f), new Vector2(580f, 38f));

        momBorrowButton = CreateButton("Ask Mom Button", borrowChoicePanel.transform, font, "엄마", new Color(0.24f, 0.48f, 0.72f));
        SetRect(momBorrowButton.GetComponent<RectTransform>(), new Vector2(20f, -70f), new Vector2(180f, 70f));
        momBorrowButton.onClick.AddListener(AskMomForMoney);

        friendBorrowButton = CreateButton("Ask Friend Button", borrowChoicePanel.transform, font, "민재", new Color(0.22f, 0.54f, 0.48f));
        SetRect(friendBorrowButton.GetComponent<RectTransform>(), new Vector2(220f, -70f), new Vector2(180f, 70f));
        friendBorrowButton.onClick.AddListener(AskFriendForMoney);

        Button closeBorrowButton = CreateButton("Close Borrow Button", borrowChoicePanel.transform, font, "닫기", new Color(0.32f, 0.36f, 0.42f));
        SetRect(closeBorrowButton.GetComponent<RectTransform>(), new Vector2(420f, -70f), new Vector2(180f, 70f));
        closeBorrowButton.onClick.AddListener(() => borrowChoicePanel.SetActive(false));
        borrowChoicePanel.SetActive(false);

        feedbackPanel = CreatePanel("Action Feedback Toast", canvas.transform, new Color(0.03f, 0.05f, 0.09f, 0.9f));
        RectTransform toastRect = feedbackPanel.GetComponent<RectTransform>();
        toastRect.anchorMin = new Vector2(0.5f, 0f);
        toastRect.anchorMax = new Vector2(0.5f, 0f);
        toastRect.pivot = new Vector2(0.5f, 0f);
        toastRect.anchoredPosition = new Vector2(0f, 220f);
        toastRect.sizeDelta = new Vector2(760f, 66f);
        feedbackGroup = feedbackPanel.AddComponent<CanvasGroup>();
        feedbackGroup.alpha = 0f;
        feedbackGroup.blocksRaycasts = false;

        feedbackText = CreateText("Action Feedback", feedbackPanel.transform, font, 24, FontStyles.Bold, Color.white);
        feedbackText.alignment = TextAlignmentOptions.Center;
        Stretch(feedbackText.rectTransform);

        GameObject fade = CreatePanel("Screen Fade", canvas.transform, Color.black);
        RectTransform fadeRect = fade.GetComponent<RectTransform>();
        Stretch(fadeRect);
        fadeGroup = fade.AddComponent<CanvasGroup>();
        fadeGroup.alpha = 0f;
        fadeGroup.blocksRaycasts = false;
        fadeCaption = CreateText("Fade Caption", fade.transform, font, 32, FontStyles.Bold, Color.white);
        fadeCaption.alignment = TextAlignmentOptions.Center;
        Stretch(fadeCaption.rectTransform);

        narrationPanel = CreatePanel("Narration Dialogue", canvas.transform, new Color(0f, 0f, 0f, 0.42f));
        Stretch(narrationPanel.GetComponent<RectTransform>());

        GameObject narrationBox = CreatePanel("Narration Box", narrationPanel.transform, new Color(0.035f, 0.065f, 0.11f, 1f));
        RectTransform narrationBoxRect = narrationBox.GetComponent<RectTransform>();
        narrationBoxRect.anchorMin = new Vector2(0.16f, 0.12f);
        narrationBoxRect.anchorMax = new Vector2(0.84f, 0.42f);
        narrationBoxRect.offsetMin = narrationBoxRect.offsetMax = Vector2.zero;

        narrationTitleText = CreateText("Narration Speaker", narrationBox.transform, font, 28, FontStyles.Bold, new Color(0.45f, 0.72f, 1f));
        narrationTitleText.alignment = TextAlignmentOptions.Left;
        SetRect(narrationTitleText.rectTransform, new Vector2(42f, -28f), new Vector2(180f, 46f));

        narrationBodyText = CreateText("Narration Body", narrationBox.transform, font, 31, FontStyles.Normal, Color.white);
        narrationBodyText.textWrappingMode = TextWrappingModes.Normal;
        narrationBodyText.alignment = TextAlignmentOptions.TopLeft;
        RectTransform narrationBodyRect = narrationBodyText.rectTransform;
        narrationBodyRect.anchorMin = new Vector2(0f, 0f);
        narrationBodyRect.anchorMax = new Vector2(1f, 1f);
        narrationBodyRect.offsetMin = new Vector2(42f, 72f);
        narrationBodyRect.offsetMax = new Vector2(-210f, -82f);

        narrationContinueButton = CreateButton("Narration Continue Button", narrationBox.transform, font, "계속", new Color(0.16f, 0.45f, 0.78f));
        RectTransform narrationButtonRect = narrationContinueButton.GetComponent<RectTransform>();
        narrationButtonRect.anchorMin = new Vector2(1f, 0f);
        narrationButtonRect.anchorMax = new Vector2(1f, 0f);
        narrationButtonRect.pivot = new Vector2(1f, 0f);
        narrationButtonRect.anchoredPosition = new Vector2(-34f, 28f);
        narrationButtonRect.sizeDelta = new Vector2(150f, 62f);
        narrationContinueButton.onClick.AddListener(CloseNarration);
        narrationPanel.SetActive(false);

        endPanel = CreatePanel("Ending Panel", canvas.transform, new Color(0.025f, 0.05f, 0.09f, 1f));
        Stretch(endPanel.GetComponent<RectTransform>());
        endTitleText = CreateText("Ending Title", endPanel.transform, font, 58, FontStyles.Bold, Color.white);
        endTitleText.alignment = TextAlignmentOptions.Center;
        RectTransform titleRect = endTitleText.rectTransform;
        titleRect.anchorMin = new Vector2(0.15f, 0.58f);
        titleRect.anchorMax = new Vector2(0.85f, 0.75f);
        titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;
        endBodyText = CreateText("Ending Body", endPanel.transform, font, 28, FontStyles.Normal, new Color(0.82f, 0.88f, 0.94f));
        endBodyText.alignment = TextAlignmentOptions.Center;
        endBodyText.textWrappingMode = TextWrappingModes.Normal;
        RectTransform bodyRect = endBodyText.rectTransform;
        bodyRect.anchorMin = new Vector2(0.2f, 0.27f);
        bodyRect.anchorMax = new Vector2(0.8f, 0.57f);
        bodyRect.offsetMin = bodyRect.offsetMax = Vector2.zero;

        restartButton = CreateButton("Restart Button", endPanel.transform, font, "처음부터 다시 시작", new Color(0.16f, 0.45f, 0.78f));
        RectTransform restartRect = restartButton.GetComponent<RectTransform>();
        restartRect.anchorMin = new Vector2(0.5f, 0.16f);
        restartRect.anchorMax = new Vector2(0.5f, 0.16f);
        restartRect.pivot = new Vector2(0.5f, 0.5f);
        restartRect.anchoredPosition = Vector2.zero;
        restartRect.sizeDelta = new Vector2(360f, 72f);
        restartButton.onClick.AddListener(RestartGame);
        endPanel.SetActive(false);

        panel.transform.SetAsLastSibling();
        feedbackPanel.transform.SetAsLastSibling();
        fade.transform.SetAsLastSibling();
    }

    private void ShowFeedback(string message)
    {
        if (feedbackText == null)
            return;

        if (feedbackCoroutine != null)
            StopCoroutine(feedbackCoroutine);
        feedbackCoroutine = StartCoroutine(FeedbackRoutine(message));
    }

    private IEnumerator FeedbackRoutine(string message)
    {
        feedbackText.text = message;
        feedbackGroup.alpha = 1f;
        yield return new WaitForSeconds(1.8f);

        float elapsed = 0f;
        while (elapsed < 0.35f)
        {
            elapsed += Time.deltaTime;
            feedbackGroup.alpha = 1f - elapsed / 0.35f;
            yield return null;
        }

        feedbackGroup.alpha = 0f;
        feedbackCoroutine = null;
    }

    private void CreateSettingsApp(Canvas canvas, TMP_FontAsset font)
    {
        Transform appArea = FindSceneObject("AppUi")?.transform ?? canvas.transform;
        GameObject settings = CreatePanel("Runtime Settings App", appArea, new Color(0.94f, 0.95f, 0.97f, 1f));
        Stretch(settings.GetComponent<RectTransform>());

        TMP_Text title = CreateText("Settings Title", settings.transform, font, 46, FontStyles.Bold, new Color(0.08f, 0.1f, 0.15f));
        title.text = "설정";
        SetRect(title.rectTransform, new Vector2(100f, -115f), new Vector2(520f, 70f));

        TMP_Text info = CreateText("Settings Info", settings.transform, font, 28, FontStyles.Normal, new Color(0.18f, 0.21f, 0.27f));
        info.text = "공짜 5천원\n청소년 도박 예방 시뮬레이션\n\n플레이 기록은 기기에 저장되지 않습니다.\n도박 문제 예방·상담 1336";
        info.textWrappingMode = TextWrappingModes.Normal;
        SetRect(info.rectTransform, new Vector2(100f, -230f), new Vector2(1100f, 360f));

        Button clearNotifications = CreateButton("Clear Notifications Button", settings.transform, font, "알림 기록 지우기", new Color(0.16f, 0.45f, 0.78f));
        SetRect(clearNotifications.GetComponent<RectTransform>(), new Vector2(100f, -630f), new Vector2(360f, 76f));
        clearNotifications.onClick.AddListener(() =>
        {
            notificationManager?.Clear();
            ShowFeedback("알림 기록을 지웠습니다.");
        });

        settings.SetActive(false);
        appWindow?.RegisterRuntimeApp(AppType.Setting, settings);

        Button settingButton = FindSceneObject("Setting_Btn")?.GetComponent<Button>();
        if (settingButton != null && appWindow != null)
        {
            settingButton.onClick.RemoveAllListeners();
            settingButton.onClick.AddListener(appWindow.OpenSetting);
        }
    }

    private void TriggerScenario(string trigger, Dictionary<string, string> context = null)
    {
        if (scenarioMessages == null || gameEnded && !trigger.StartsWith("ending_", StringComparison.OrdinalIgnoreCase))
            return;

        var selectionGroups = new Dictionary<string, List<ScenarioEventDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (ScenarioEventDefinition definition in scenarioMessages.GetCandidates(trigger))
        {
            if (!CanFireScenario(definition, context))
                continue;

            if (string.IsNullOrWhiteSpace(definition.selection))
            {
                FireScenario(definition, context);
                if (gameEnded)
                    return;
            }
            else
            {
                if (!selectionGroups.TryGetValue(definition.selection, out List<ScenarioEventDefinition> group))
                {
                    group = new List<ScenarioEventDefinition>();
                    selectionGroups.Add(definition.selection, group);
                }
                group.Add(definition);
            }
        }

        foreach (List<ScenarioEventDefinition> group in selectionGroups.Values)
        {
            int oldestDay = int.MaxValue;
            foreach (ScenarioEventDefinition definition in group)
            {
                int lastDay = lastScenarioEventDay.TryGetValue(definition.id, out int value) ? value : int.MinValue;
                oldestDay = Math.Min(oldestDay, lastDay);
            }

            var oldest = new List<ScenarioEventDefinition>();
            foreach (ScenarioEventDefinition definition in group)
            {
                int lastDay = lastScenarioEventDay.TryGetValue(definition.id, out int value) ? value : int.MinValue;
                if (lastDay == oldestDay)
                    oldest.Add(definition);
            }

            if (oldest.Count > 0)
                FireScenario(oldest[UnityEngine.Random.Range(0, oldest.Count)], context);
        }
    }

    private bool CanFireScenario(ScenarioEventDefinition definition, Dictionary<string, string> context)
    {
        string onceKey = definition.once?.ToLowerInvariant() switch
        {
            "game" => definition.id,
            "day" => $"{definition.id}_{currentDay}",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(onceKey) && firedScenarioEvents.Contains(onceKey))
            return false;
        if (!EvaluateScenarioCondition(definition.condition, context))
            return false;
        return definition.chance >= 1f || UnityEngine.Random.value <= definition.chance;
    }

    private void FireScenario(ScenarioEventDefinition definition, Dictionary<string, string> context)
    {
        string onceKey = definition.once?.ToLowerInvariant() switch
        {
            "game" => definition.id,
            "day" => $"{definition.id}_{currentDay}",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(onceKey))
            firedScenarioEvents.Add(onceKey);
        lastScenarioEventDay[definition.id] = currentDay;

        if (definition.stateKey == "active_story")
            activeStoryEvent = definition.stateValue;

        foreach (ScenarioMessage step in definition.steps)
        {
            string title = ExpandScenarioText(step.title, context);
            string body = ExpandScenarioText(step.message, context);
            if (string.Equals(step.delivery, "ending", StringComparison.OrdinalIgnoreCase))
            {
                EndGame(title, body);
                return;
            }

            if (string.Equals(step.delivery, "narration", StringComparison.OrdinalIgnoreCase))
                QueueNarration(title, body);
            else
                SendOnce($"scenario_{definition.id}_{currentDay}_{step.sequence}_{sentMessages.Count}", title, body, step.speaker);

            List<Choice> choices = BuildScenarioChoices(step);
            if (choices.Count > 0)
                dialogueManager?.SetEventChoices(step.speaker, choices);
        }
    }

    private static List<Choice> BuildScenarioChoices(ScenarioMessage step)
    {
        var choices = new List<Choice>();
        AddScenarioChoice(choices, step.choiceA, step.actionA);
        AddScenarioChoice(choices, step.choiceB, step.actionB);
        return choices;
    }

    private static void AddScenarioChoice(List<Choice> choices, string text, string actionName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(actionName))
            return;

        bool isScenarioTrigger = actionName.StartsWith("trigger:", StringComparison.OrdinalIgnoreCase);
        ChoiceAction action = ChoiceAction.None;
        if (!isScenarioTrigger && !Enum.TryParse(actionName, true, out action))
            return;

        choices.Add(new Choice
        {
            choiceText = text,
            nextDialogueID = -1,
            action = action,
            scenarioAction = isScenarioTrigger ? actionName : string.Empty,
            openApp = action == ChoiceAction.AcceptGambling,
            targetApp = AppType.Browser
        });
    }

    private bool EvaluateScenarioCondition(string expression, Dictionary<string, string> context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        foreach (string rawClause in expression.Split(';'))
        {
            string clause = rawClause.Trim();
            if (clause.Length == 0)
                continue;

            string[] operators = { ">=", "<=", "!=", ">", "<", "=" };
            string selected = null;
            int operatorIndex = -1;
            foreach (string candidate in operators)
            {
                operatorIndex = clause.IndexOf(candidate, StringComparison.Ordinal);
                if (operatorIndex > 0)
                {
                    selected = candidate;
                    break;
                }
            }
            if (selected == null)
                return false;

            string key = clause.Substring(0, operatorIndex).Trim();
            string expected = clause.Substring(operatorIndex + selected.Length).Trim();
            string actual = GetScenarioValue(key, context);
            if (!CompareScenarioValue(actual, expected, selected))
                return false;
        }
        return true;
    }

    private string GetScenarioValue(string key, Dictionary<string, string> context)
    {
        if (context != null && context.TryGetValue(key, out string supplied))
            return supplied;
        return key.ToLowerInvariant() switch
        {
            "day" => currentDay.ToString(),
            "day_mod_3" => (currentDay % 3).ToString(),
            "hour" => currentHour.ToString(),
            "weekend" => IsWeekend.ToString().ToLowerInvariant(),
            "school_done" => schoolDone.ToString().ToLowerInvariant(),
            "homework_done" => homeworkDone.ToString().ToLowerInvariant(),
            "job_done" => jobDone.ToString().ToLowerInvariant(),
            "gambling_unlocked" => gamblingUnlocked.ToString().ToLowerInvariant(),
            "invitation_resolved" => invitationResolved.ToString().ToLowerInvariant(),
            "rounds" => gambleRounds.ToString(),
            "losses" => gambleLosses.ToString(),
            "charges" => casinoChargesToday.ToString(),
            "cashout_attempts" => cashOutAttempts.ToString(),
            "debt" => debt.ToString(),
            "bank_cash" => (coinManager?.BankCash ?? 0).ToString(),
            "casino_cash" => (coinManager?.CasinoCash ?? 0).ToString(),
            "sns_hours" => snsHoursToday.ToString(),
            "schedule_failures" => scheduleFailureDays.ToString(),
            "short_sleep_days" => consecutiveShortSleepDays.ToString(),
            "location" => currentLocation,
            "active_story" => activeStoryEvent,
            _ => string.Empty
        };
    }

    private static bool CompareScenarioValue(string actual, string expected, string operation)
    {
        if (double.TryParse(actual, out double actualNumber) && double.TryParse(expected, out double expectedNumber))
        {
            return operation switch
            {
                "=" => Math.Abs(actualNumber - expectedNumber) < 0.0001,
                "!=" => Math.Abs(actualNumber - expectedNumber) >= 0.0001,
                ">" => actualNumber > expectedNumber,
                "<" => actualNumber < expectedNumber,
                ">=" => actualNumber >= expectedNumber,
                "<=" => actualNumber <= expectedNumber,
                _ => false
            };
        }

        int comparison = string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
        return operation switch
        {
            "=" => comparison == 0,
            "!=" => comparison != 0,
            _ => false
        };
    }

    private string ExpandScenarioText(string value, Dictionary<string, string> context)
    {
        string result = value ?? string.Empty;
        if (context != null)
            foreach (KeyValuePair<string, string> pair in context)
                result = result.Replace("{" + pair.Key + "}", pair.Value);
        return result;
    }

    private void QueueNarration(string title, string body)
    {
        narrationQueue.Enqueue((string.IsNullOrWhiteSpace(title) ? "나" : title, body));
        ShowNextNarration();
    }

    private void CreateSnsApp(Canvas canvas, TMP_FontAsset font)
    {
        Transform appArea = FindSceneObject("AppUi")?.transform ?? canvas.transform;
        GameObject sns = CreatePanel("Runtime SNS App", appArea, new Color(0.965f, 0.97f, 0.98f, 1f));
        Stretch(sns.GetComponent<RectTransform>());

        GameObject header = CreatePanel("SNS Header", sns.transform, new Color(0.1f, 0.14f, 0.2f, 1f));
        RectTransform headerRect = header.GetComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = Vector2.zero;
        headerRect.sizeDelta = new Vector2(0f, 130f);

        TMP_Text title = CreateText("SNS Title", header.transform, font, 44, FontStyles.Bold, Color.white);
        title.text = "SNS";
        title.alignment = TextAlignmentOptions.Left;
        SetRect(title.rectTransform, new Vector2(70f, -38f), new Vector2(400f, 62f));

        TMP_Text feedTitle = CreateText("SNS Feed Title", sns.transform, font, 38, FontStyles.Bold, new Color(0.08f, 0.12f, 0.18f));
        feedTitle.text = "추천 영상";
        SetRect(feedTitle.rectTransform, new Vector2(100f, -200f), new Vector2(500f, 64f));

        GameObject video = CreatePanel("SNS Video Preview", sns.transform, new Color(0.13f, 0.18f, 0.25f, 1f));
        RectTransform videoRect = video.GetComponent<RectTransform>();
        videoRect.anchorMin = new Vector2(0.29f, 0.4f);
        videoRect.anchorMax = new Vector2(0.71f, 0.84f);
        videoRect.offsetMin = videoRect.offsetMax = Vector2.zero;

        GameObject feedArt = new GameObject("SNS Feed Art", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        feedArt.layer = 5;
        feedArt.transform.SetParent(video.transform, false);
        snsFeedImage = feedArt.GetComponent<RawImage>();
        snsFeedImage.texture = Resources.Load<Texture2D>("TestAssets/Gemini_Generated_Image_39o0xn39o0xn39o0");
        snsFeedImage.color = Color.white;
        snsFeedImage.raycastTarget = false;
        Stretch(snsFeedImage.rectTransform);

        TMP_Text videoText = CreateText("SNS Video Text", video.transform, font, 34, FontStyles.Bold, Color.white);
        videoText.text = "오늘 올라온 영상과 짧은 게시물";
        videoText.alignment = TextAlignmentOptions.Center;
        Stretch(videoText.rectTransform);

        TMP_Text prompt = CreateText("SNS Time Prompt", sns.transform, font, 30, FontStyles.Normal, new Color(0.2f, 0.24f, 0.3f));
        prompt.text = "얼마나 시청할까?";
        prompt.alignment = TextAlignmentOptions.Center;
        RectTransform promptRect = prompt.rectTransform;
        promptRect.anchorMin = new Vector2(0.2f, 0.33f);
        promptRect.anchorMax = new Vector2(0.8f, 0.39f);
        promptRect.offsetMin = promptRect.offsetMax = Vector2.zero;

        int[] hours = { 1, 2, 3, 5 };
        for (int i = 0; i < hours.Length; i++)
        {
            int selectedHours = hours[i];
            Button button = CreateButton($"SNS {selectedHours} Hour Button", sns.transform, font, $"{selectedHours}시간", new Color(0.2f, 0.52f, 0.34f));
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.24f);
            rect.anchorMax = new Vector2(0.5f, 0.24f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2((i - 1.5f) * 230f, 0f);
            rect.sizeDelta = new Vector2(190f, 76f);
            button.onClick.AddListener(() => WatchSns(selectedHours));
        }

        snsStatusText = CreateText("SNS Status", sns.transform, font, 25, FontStyles.Normal, new Color(0.3f, 0.34f, 0.4f));
        snsStatusText.text = "시청 시간은 되돌릴 수 없습니다.";
        snsStatusText.alignment = TextAlignmentOptions.Center;
        RectTransform statusRect = snsStatusText.rectTransform;
        statusRect.anchorMin = new Vector2(0.15f, 0.11f);
        statusRect.anchorMax = new Vector2(0.85f, 0.18f);
        statusRect.offsetMin = statusRect.offsetMax = Vector2.zero;

        sns.SetActive(false);
        appWindow?.RegisterRuntimeApp(AppType.SNS, sns);
    }

    private void CreateSnsHomeIcon(TMP_FontAsset font)
    {
        GameObject icon = FindSceneObject("Button (6)");
        if (icon == null || appWindow == null)
            return;

        icon.name = "SNSApp";
        Image slotImage = icon.GetComponent<Image>();
        if (slotImage != null)
        {
            slotImage.color = Color.clear;
            slotImage.raycastTarget = true;
        }

        Transform oldVisual = icon.transform.Find("SNS Icon Visual");
        RawImage image;
        if (oldVisual != null)
        {
            image = oldVisual.GetComponent<RawImage>();
        }
        else
        {
            GameObject visual = new GameObject("SNS Icon Visual", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            visual.layer = icon.layer;
            visual.transform.SetParent(icon.transform, false);
            visual.transform.SetAsFirstSibling();
            image = visual.GetComponent<RawImage>();
            Stretch(image.rectTransform);
        }

        image.texture = Resources.Load<Texture2D>("SNS/sns_icon");
        image.uvRect = new Rect(0.235f, 0.235f, 0.53f, 0.53f);
        image.color = Color.white;
        image.raycastTarget = false;

        Button button = icon.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.targetGraphic = slotImage;
        button.onClick.AddListener(appWindow.OpenSNS);

        TMP_Text label = icon.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.name = "SNS Icon Label";
            if (label.font == null)
                label.font = font;
            label.fontSize = 24f;
            label.text = "SNS";
            label.color = new Color(0.196f, 0.196f, 0.196f, 1f);
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.anchoredPosition = new Vector2(0f, -92f);
            labelRect.sizeDelta = new Vector2(0f, -116.8f);
        }
    }

    public void WatchSns(int hours)
    {
        if (!CanSpendTime(hours) || (hours != 1 && hours != 2 && hours != 3 && hours != 5))
            return;

        int startHour = currentHour;
        bool nearDayBoundary = GetHoursUntilDayBoundary(currentHour) <= hours;
        snsHoursToday += hours;
        AdvanceHours(hours);
        if (gameEnded)
            return;

        bool late = startHour >= 22 || startHour < DayStartHour || currentHour >= 22 || currentHour < DayStartHour;
        TriggerScenario("sns_watch", new Dictionary<string, string>
        {
            ["hours"] = hours.ToString(),
            ["late"] = late.ToString().ToLowerInvariant(),
            ["near_day_boundary"] = nearDayBoundary.ToString().ToLowerInvariant()
        });
        int index = hours == 1 ? 0 : hours == 2 ? 1 : hours == 3 ? 2 : 3;
        string[] feedImages =
        {
            "TestAssets/Gemini_Generated_Image_39o0xn39o0xn39o0",
            "TestAssets/Gemini_Generated_Image_4e11dw4e11dw4e11",
            "TestAssets/Gemini_Generated_Image_k9p8g0k9p8g0k9p8"
        };
        if (snsFeedImage != null)
            snsFeedImage.texture = Resources.Load<Texture2D>(feedImages[(snsHoursToday + index) % feedImages.Length]);
        if (snsStatusText != null)
            snsStatusText.text = $"오늘 SNS를 본 시간  {snsHoursToday}시간";

    }

    private static string NormalizeLocation(string raw)
    {
        if (raw == "1" || raw.Contains("학교")) return "학교";
        if (raw == "2" || raw.Contains("카페") || raw.Contains("알바")) return "카페";
        if (raw == "3" || raw.Contains("집")) return "집";
        return string.IsNullOrWhiteSpace(raw) ? "집" : raw;
    }

    private static string Mark(bool completed) => completed ? "[완료]" : "[  ]";
    private static int GetWeekdayIndex(int day) => (day - 1) % 7;

    private static string GetWeekdayName(int day)
    {
        string[] names = { "월요일", "화요일", "수요일", "목요일", "금요일", "토요일", "일요일" };
        return names[GetWeekdayIndex(day)];
    }

    private static GameObject CreatePanel(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    private static Canvas FindRootCanvas()
    {
        Canvas fallback = null;
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            fallback ??= canvas;
            if (canvas.transform.parent == null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return canvas;
        }

        return fallback;
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

    private static TMP_FontAsset FindPreferredFont()
    {
        TMP_FontAsset fallback = null;
        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null)
                continue;

            fallback ??= font;
            if (font.name.Contains("NotoSansKR-Regular"))
                return font;
        }

        return fallback ?? FindAnyObjectByType<TMP_Text>()?.font;
    }

    private static void ApplyKoreanFont()
    {
        TMP_FontAsset font = FindPreferredFont();
        if (font == null)
            return;

        foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text != null && text.gameObject.scene.IsValid())
                text.font = font;
        }
    }

    private static TMP_Text CreateText(string name, Transform parent, TMP_FontAsset font, float size, FontStyles style, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }

    private static Button CreateButton(string name, Transform parent, TMP_FontAsset font, string label, Color color)
    {
        GameObject go = CreatePanel(name, parent, color);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        ColorBlock colors = button.colors;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.12f);
        colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
        button.colors = colors;

        TMP_Text text = CreateText("Label", go.transform, font, 23, FontStyles.Bold, Color.white);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        Stretch(text.rectTransform);
        return button;
    }

    private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
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
