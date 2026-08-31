using System;
using System.Collections;
using System.Collections.Generic;
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
    private const int DebtEndingThreshold = 15000;

    private readonly HashSet<string> sentMessages = new HashSet<string>();

    private QuizManager quizManager;
    private NotificationManager notificationManager;
    private AppWindow appWindow;
    private CoinManager coinManager;

    private TMP_Text moneyText;
    private TMP_Text feedbackText;
    private TMP_Text fadeCaption;
    private Button sleepButton;
    private Button helpButton;
    private Button loanButton;
    private Button cashOutButton;
    private CanvasGroup fadeGroup;
    private GameObject endPanel;
    private TMP_Text endTitleText;
    private TMP_Text endBodyText;
    private Coroutine feedbackCoroutine;
    private GameObject actionBar;
    private readonly List<TMP_Text> dateTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> clockTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> locationTexts = new List<TMP_Text>();
    private readonly List<TMP_Text> homeChecklistLines = new List<TMP_Text>();

    private int currentDay = 1;
    private int currentHour = DayStartHour;
    private int scheduleFailureDays;
    private int sleepDebt;
    private int debt;
    private int gambleRounds;
    private int gambleLosses;
    private string currentLocation = "집";

    private bool schoolDone;
    private bool homeworkDone;
    private bool jobDone;
    private bool sleepDone;
    private bool invitationResolved;
    private bool gamblingUnlocked;
    private bool isTransitioning;
    private bool gameEnded;

    public bool IsWeekend => GetWeekdayIndex(currentDay) >= 5;
    public bool IsGameEnded => gameEnded;
    public bool IsGamblingUnlocked => gamblingUnlocked;
    public int CurrentDay => currentDay;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (SceneManager.GetActiveScene().name != "TabletUI")
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

        quizManager = FindAnyObjectByType<QuizManager>();
        notificationManager = FindAnyObjectByType<NotificationManager>();
        appWindow = FindAnyObjectByType<AppWindow>();
        coinManager = CoinManager.Instance ?? FindAnyObjectByType<CoinManager>();

        BindExistingStatusText();
        CreateRuntimeUI();

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
        }

        StartNewDay(false);
        yield return new WaitForSeconds(0.8f);
        SendInitialInvitation();
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
        StartCoroutine(FadeTransition($"{location}(으)로 이동 중", () =>
        {
            currentLocation = location;
            AdvanceHours(1);

            if (location == "학교" && !IsWeekend && !schoolDone)
            {
                schoolDone = true;
                AdvanceHours(7);
                SendOnce("school_done", "학교", "수업이 끝났습니다. 오늘 숙제를 확인하세요.", SpeakerType.Unknown);
            }
            else if (location == "카페" && IsWeekend && !jobDone)
            {
                jobDone = true;
                AdvanceHours(6);
                coinManager?.AddBankCash(60, "Cafe part-time wage");
                SendOnce($"job_{currentDay}", "은행 알림", "카페 아르바이트 급여 60원이 입금되었습니다.", SpeakerType.Unknown);
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
            EndGame("위험 차단", "링크를 열지 않았다. 공짜라는 말에 시간을 빼앗기지 않고 평범한 일상을 이어 갔다.");
            return;
        }

        SendOnce("welcome", "사이트 알림", "무료 체험 포인트 5,000P가 지급되었습니다.", SpeakerType.Scammer);
        ShowFeedback("도박 사이트가 열렸다. 이용 여부는 계속 선택할 수 있다.");
    }

    public void RequestHelp()
    {
        if (gameEnded)
            return;

        SendOnce("help", "상담 선생님", "혼자 해결하지 않아도 괜찮아. 지금 상황부터 같이 정리해 보자.", SpeakerType.Unknown);
        EndGame("도움 요청", "상황을 숨기지 않고 도움을 요청했다. 남은 빚과 일정, 이용 기록을 함께 정리하기 시작했다.");
    }

    private void CompleteHomework(int correctAnswers, int totalQuestions)
    {
        if (homeworkDone || IsWeekend || gameEnded)
            return;

        homeworkDone = true;
        AdvanceHours(2);
        ShowFeedback($"숙제 완료: {totalQuestions}문제 중 {correctAnswers}문제 정답");
        SendOnce($"homework_{currentDay}", "과제 제출", "오늘의 숙제가 제출되었습니다.", SpeakerType.Unknown);
    }

    private void Sleep()
    {
        if (gameEnded || isTransitioning)
            return;

        int sleepHours = currentHour < DayStartHour
            ? DayStartHour - currentHour
            : 24 - currentHour + DayStartHour;

        sleepHours = Mathf.Clamp(sleepHours, 1, 12);
        sleepDone = true;
        RefreshUI();

        StartCoroutine(FadeTransition("하루를 마무리하는 중", () =>
        {
            currentLocation = "집";

            if (sleepHours < 7)
                sleepDebt += 7 - sleepHours;
            else
                sleepDebt = Mathf.Max(0, sleepDebt - 1);

            FinishDay();
        }, 0.55f));
    }

    private void FinishDay()
    {
        bool requiredDone = IsWeekend ? jobDone : schoolDone && homeworkDone;
        if (!requiredDone)
        {
            scheduleFailureDays++;
            SendScheduleWarning();
        }

        if (!sleepDone)
            sleepDebt += 7;

        if (scheduleFailureDays >= CollapseFailureLimit)
        {
            EndGame("일상 붕괴", "필수 일정과 수면을 반복해서 놓쳤다. 학교와 알바, 가족과의 일상이 더는 유지되지 않았다.");
            return;
        }

        if (debt >= DebtEndingThreshold)
        {
            EndGame("빚", "갚아야 할 돈이 감당할 수 없는 수준까지 커졌다. 더 빌리는 것으로는 해결할 수 없었다.");
            return;
        }

        currentDay++;
        if (currentDay > FinalDay)
        {
            EndGame("일상 회복", "도박을 접했지만 시간을 다시 일상에 사용했다. 필요한 일정과 관계를 지키며 14일을 마쳤다.");
            return;
        }

        currentHour = DayStartHour;
        StartNewDay(true);
    }

    private void StartNewDay(bool sendDailyMessage)
    {
        schoolDone = false;
        homeworkDone = false;
        jobDone = false;
        sleepDone = false;

        quizManager?.ConfigureForDay(currentDay, !IsWeekend);

        if (sendDailyMessage)
        {
            if (IsWeekend)
                SendOnce($"shift_{currentDay}", "카페 매니저", "오늘 오후 근무가 있습니다. 늦지 않게 와 주세요.", SpeakerType.Unknown);
            else if (UnityEngine.Random.value < 0.45f)
                SendEverydayMessage();

            if (gamblingUnlocked && currentDay >= 2 && UnityEngine.Random.value < 0.3f)
                SendOnce($"site_push_{currentDay}", "사이트 알림", "출석 보상이 곧 만료됩니다. 시간 제한을 강조하는 알림에 주의하세요.", SpeakerType.Scammer);
        }

        RefreshUI();
    }

    private void AdvanceHours(int hours)
    {
        currentHour += Mathf.Max(0, hours);

        while (currentHour >= 24 && !gameEnded)
        {
            currentHour -= 24;
            sleepDone = false;

            bool requiredDone = IsWeekend ? jobDone : schoolDone && homeworkDone;
            if (!requiredDone)
            {
                scheduleFailureDays++;
                SendScheduleWarning();
            }

            sleepDebt += 7;
            currentDay++;

            if (scheduleFailureDays >= CollapseFailureLimit)
            {
                EndGame("일상 붕괴", "밤을 넘겨 가며 시간을 사용했고 필수 일정과 수면이 무너졌다.");
                return;
            }

            if (currentDay > FinalDay)
            {
                EndGame("일상 회복", "14일 동안 선택한 시간의 결과를 확인했다.");
                return;
            }

            StartNewDay(true);
        }

        RefreshUI();
    }

    private void SendScheduleWarning()
    {
        if (IsWeekend)
        {
            SendOnce($"miss_job_{currentDay}", "카페 매니저", "오늘 근무 시간인데 연락이 되지 않네요. 다음 근무는 조정이 필요합니다.", SpeakerType.Unknown);
        }
        else if (!schoolDone)
        {
            SendOnce($"miss_school_{currentDay}", "담임 선생님", "오늘 등교하지 않았습니다. 무슨 일이 있는지 확인해 주세요.", SpeakerType.Unknown);
        }
        else if (!homeworkDone)
        {
            SendOnce($"miss_homework_{currentDay}", "담임 선생님", "오늘 숙제가 제출되지 않았습니다. 내일까지 확인해 주세요.", SpeakerType.Unknown);
        }
    }

    private void OnGambleResolved(bool won, int payout)
    {
        gambleRounds++;
        if (!won)
            gambleLosses++;

        if (gambleRounds == 1)
            SendOnce("friend_after_first", "민재", "진짜 들어갔네? 무료 포인트로만 하면 손해 볼 건 없잖아.", SpeakerType.Friend);
        else if (won && gambleRounds % 2 == 0)
            SendOnce($"friend_win_{gambleRounds}", "민재", "포인트 늘었네. 이럴 때 조금 더 해보는 거지.", SpeakerType.Friend);
        else if (!won && gambleLosses % 2 == 0)
            SendOnce($"friend_loss_{gambleLosses}", "민재", "처음엔 원래 잘 안 맞아. 조금만 더 하면 돌아올 수도 있어.", SpeakerType.Friend);

        if (gambleRounds >= 3)
            SendOnce("bank_repeat", "은행 알림", "평소와 다른 반복 이체가 확인되었습니다. 거래 내역을 확인하세요.", SpeakerType.Unknown);
    }

    private void BorrowMoney()
    {
        if (gameEnded || coinManager == null || coinManager.BankCash > 0)
            return;

        coinManager.AddBankCash(3000, "Fictional emergency loan");
        debt += 4500;
        SendOnce($"loan_{debt}", "대출 알림", "3,000원이 입금되었습니다. 갚아야 할 금액은 4,500원입니다.", SpeakerType.Scammer);
        RefreshUI();

        if (debt >= DebtEndingThreshold)
            EndGame("빚", "대출을 반복하면서 갚아야 할 금액이 감당할 수 없는 수준까지 커졌다.");
    }

    private void AttemptCashOut()
    {
        if (gameEnded || coinManager == null || coinManager.CasinoCash < 1000)
            return;

        SendOnce("cashout", "사이트 알림", "고액 환전을 위해 추가 보증금이 필요합니다.", SpeakerType.Scammer);
        EndGame("먹튀", "환전을 요청하자 추가 입금을 요구했고, 잠시 뒤 계정에 접속할 수 없게 되었다.");
    }

    private void OnBankCashChanged(int value)
    {
        if (value <= 20)
            SendOnce("low_balance", "은행 알림", "계좌 잔액이 거의 남지 않았습니다.", SpeakerType.Unknown);

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
    }

    private void SendInitialInvitation()
    {
        SendOnce("initial", "민재", "야, 가입하면 무료 포인트를 준다는 곳을 찾았어. 링크 보내줄까?", SpeakerType.Friend);
    }

    private void SendEverydayMessage()
    {
        int index = UnityEngine.Random.Range(0, 3);
        if (index == 0)
            SendOnce($"daily_{currentDay}", "준호", "오늘 수업 끝나고 같이 편의점 갈래?", SpeakerType.Unknown);
        else if (index == 1)
            SendOnce($"daily_{currentDay}", "서연", "내일 제출할 숙제 어디까지 했어? 모르는 문제 있으면 같이 보자.", SpeakerType.Unknown);
        else
            SendOnce($"daily_{currentDay}", "엄마", "오늘은 다 같이 저녁 먹자. 시간 맞으면 바로 와.", SpeakerType.Mom);
    }

    private void SendOnce(string key, string title, string message, SpeakerType speaker)
    {
        if (!sentMessages.Add(key) || notificationManager == null)
            return;

        notificationManager.SendNotification(new NotificationData
        {
            title = title,
            message = message,
            appType = AppType.Message,
            speakerType = speaker
        });
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
        if (endPanel != null)
        {
            endTitleText.text = title;
            endBodyText.text = body + "\n\n도박 문제 예방·상담 1336";
            endPanel.SetActive(true);
            endPanel.transform.SetAsLastSibling();
        }

        RefreshUI();
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

        if (moneyText != null)
        {
            int bank = coinManager != null ? coinManager.BankCash : 0;
            int casino = coinManager != null ? coinManager.CasinoCash : 0;
            moneyText.text = $"통장 {bank:N0}  ·  사이트 {casino:N0}  ·  빚 {debt:N0}  ·  일정 누락 {scheduleFailureDays}/{CollapseFailureLimit}";
        }

        if (sleepButton != null)
            sleepButton.interactable = !gameEnded && !isTransitioning;
        if (helpButton != null)
            helpButton.interactable = !gameEnded;
        if (loanButton != null)
        {
            loanButton.gameObject.SetActive(!gameEnded && coinManager != null && coinManager.BankCash <= 0);
            loanButton.interactable = debt < DebtEndingThreshold;
        }
        if (cashOutButton != null)
            cashOutButton.gameObject.SetActive(!gameEnded && coinManager != null && coinManager.CasinoCash >= 1000);
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
                $"{Mark(jobDone)} 알바 가기  (지도 · 카페)",
                $"{Mark(sleepDone)} 잠자기",
                "",
                ""
            }
            : new[]
            {
                $"{Mark(schoolDone)} 학교 가기  (지도 · 학교)",
                $"{Mark(homeworkDone)} 숙제하기  (공부 · 5문제)",
                $"{Mark(sleepDone)} 잠자기",
                ""
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

        GameObject panel = CreatePanel("Daily Action Bar", canvas.transform, new Color(0.035f, 0.075f, 0.13f, 0.92f));
        actionBar = panel;
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.zero;
        panelRect.pivot = Vector2.zero;
        panelRect.anchoredPosition = new Vector2(200f, 125f);
        panelRect.sizeDelta = new Vector2(750f, 130f);

        moneyText = CreateText("Money Text", panel.transform, font, 20, FontStyles.Normal, new Color(0.64f, 0.8f, 0.92f));
        SetRect(moneyText.rectTransform, new Vector2(20f, -12f), new Vector2(710f, 30f));

        sleepButton = CreateButton("Sleep Button", panel.transform, font, "잠자기", new Color(0.16f, 0.45f, 0.78f));
        SetRect(sleepButton.GetComponent<RectTransform>(), new Vector2(20f, -56f), new Vector2(220f, 58f));
        sleepButton.onClick.AddListener(Sleep);

        helpButton = CreateButton("Help Button", panel.transform, font, "도움 요청", new Color(0.16f, 0.58f, 0.48f));
        SetRect(helpButton.GetComponent<RectTransform>(), new Vector2(255f, -56f), new Vector2(220f, 58f));
        helpButton.onClick.AddListener(RequestHelp);

        loanButton = CreateButton("Loan Button", panel.transform, font, "돈 빌리기", new Color(0.72f, 0.36f, 0.18f));
        SetRect(loanButton.GetComponent<RectTransform>(), new Vector2(490f, -56f), new Vector2(115f, 58f));
        loanButton.onClick.AddListener(BorrowMoney);

        cashOutButton = CreateButton("Cashout Button", panel.transform, font, "환전 시도", new Color(0.66f, 0.28f, 0.3f));
        SetRect(cashOutButton.GetComponent<RectTransform>(), new Vector2(615f, -56f), new Vector2(115f, 58f));
        cashOutButton.onClick.AddListener(AttemptCashOut);

        feedbackText = CreateText("Action Feedback", canvas.transform, font, 24, FontStyles.Bold, Color.white);
        feedbackText.alignment = TextAlignmentOptions.Center;
        feedbackText.color = new Color(1f, 1f, 1f, 0f);
        RectTransform feedbackRect = feedbackText.rectTransform;
        feedbackRect.anchorMin = new Vector2(0.5f, 0f);
        feedbackRect.anchorMax = new Vector2(0.5f, 0f);
        feedbackRect.pivot = new Vector2(0.5f, 0f);
        feedbackRect.anchoredPosition = new Vector2(0f, 105f);
        feedbackRect.sizeDelta = new Vector2(900f, 64f);

        GameObject fade = CreatePanel("Screen Fade", canvas.transform, Color.black);
        RectTransform fadeRect = fade.GetComponent<RectTransform>();
        Stretch(fadeRect);
        fadeGroup = fade.AddComponent<CanvasGroup>();
        fadeGroup.alpha = 0f;
        fadeGroup.blocksRaycasts = false;
        fadeCaption = CreateText("Fade Caption", fade.transform, font, 32, FontStyles.Bold, Color.white);
        fadeCaption.alignment = TextAlignmentOptions.Center;
        Stretch(fadeCaption.rectTransform);

        endPanel = CreatePanel("Ending Panel", canvas.transform, new Color(0.025f, 0.05f, 0.09f, 0.98f));
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
        endPanel.SetActive(false);

        panel.transform.SetAsLastSibling();
        feedbackText.transform.SetAsLastSibling();
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
        feedbackText.color = Color.white;
        yield return new WaitForSeconds(1.8f);

        float elapsed = 0f;
        while (elapsed < 0.35f)
        {
            elapsed += Time.deltaTime;
            Color color = feedbackText.color;
            color.a = 1f - elapsed / 0.35f;
            feedbackText.color = color;
            yield return null;
        }

        feedbackCoroutine = null;
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

    private static TMP_FontAsset FindPreferredFont()
    {
        TMP_FontAsset fallback = null;
        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null)
                continue;

            fallback ??= font;
            if (font.name.Contains("서울사이버대학체"))
                return font;
        }

        return fallback ?? FindAnyObjectByType<TMP_Text>()?.font;
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
