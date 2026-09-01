using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class QuizManager : MonoBehaviour
{
    public event Action<int, int> DailyQuizCompleted;

    [Header("문제 표시")]
    [SerializeField] private TMP_Text questionText;

    [Header("선택 버튼")]
    [SerializeField] private Button[] answerButtons;

    [Header("결과 표시")]
    [SerializeField] private TMP_Text resultText;
    [SerializeField] private Image resultBox;

    [Header("문제 데이터")]
    [SerializeField] private QuizData[] quizzes;

    [Header("하루에 풀 문제 수")]
    [SerializeField] private int dailyQuestionCount = 5;

    [Header("다음 문제까지 대기 시간")]
    [SerializeField] private float nextQuestionDelay = 1.0f;

    [Header("테스트용")]
    [SerializeField] private Button resetButton;

    [Header("문제 진행도")]
    [SerializeField] private TMP_Text progressText;


    //==================================================
    // 현재 상태
    //==================================================

    // 오늘 출제할 문제들의 실제 번호
    private List<int> dailyQuestionIndices = new List<int>();

    // 오늘의 문제 중 현재 몇 번째인지
    private int currentIndex = 0;

    // 답을 이미 선택했는지
    private bool isAnswerLocked = false;

    // 오늘 문제를 모두 풀었는지
    private bool isDailyQuizFinished = false;
    private int correctAnswerCount;
    private int configuredDay = -1;
    private bool isWeekday = true;
    private bool listenersBound;
    private bool completionReported;
    private GameObject answerHeaderObject;
    private GameObject answerPanelObject;
    private AudioSource feedbackAudioSource;
    private AudioClip correctClip;
    private AudioClip wrongClip;


    //==================================================
    // 시작
    //==================================================

    private void Start()
    {
        feedbackAudioSource = gameObject.AddComponent<AudioSource>();
        feedbackAudioSource.playOnAwake = false;
        correctClip = Resources.Load<AudioClip>("Audio/SFX/quiz_correct");
        wrongClip = Resources.Load<AudioClip>("Audio/SFX/quiz_wrong");

        ResolveSceneLabels();
        BindButtonListeners();

        // 테스트용 초기화 버튼
        if (resetButton != null)
        {
            resetButton.onClick.AddListener(ResetDailyQuiz);
        }

        if (configuredDay < 0)
            ConfigureForDay(1, true);
    }

    private void ResolveSceneLabels()
    {
        foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true))
        {
            if (label.gameObject.name == "Q_Text")
                label.gameObject.SetActive(false);
            else if (label.gameObject.name == "Question_Text" && label != questionText)
            {
                answerHeaderObject = label.gameObject;
                answerPanelObject = label.transform.parent.gameObject;
            }
        }
    }

    private void BindButtonListeners()
    {
        if (listenersBound)
            return;

        listenersBound = true;
        for (int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;
            answerButtons[i].onClick.AddListener(() => CheckAnswer(index));
        }
    }

    public void ConfigureForDay(int day, bool weekday)
    {
        if (configuredDay == day && isWeekday == weekday)
            return;

        CancelInvoke(nameof(NextQuestion));
        configuredDay = day;
        isWeekday = weekday;

        if (!isWeekday)
        {
            ShowWeekendState();
            return;
        }

        CreateDailyQuiz();
    }


    //==================================================
    // 오늘의 문제 5개 랜덤 선택
    //==================================================

    private void CreateDailyQuiz()
    {
        dailyQuestionIndices.Clear();

        currentIndex = 0;
        isDailyQuizFinished = false;
        isAnswerLocked = false;
        correctAnswerCount = 0;
        completionReported = false;

        // 전체 문제의 번호를 임시 리스트에 넣기
        List<int> availableIndices = new List<int>();

        for (int i = 0; i < quizzes.Length; i++)
        {
            availableIndices.Add(i);
        }

        // 날짜마다 다른 순서지만 같은 날 다시 열면 순서가 유지된다.
        System.Random random = new System.Random(configuredDay * 7919 + quizzes.Length * 31);
        for (int i = 0; i < availableIndices.Count; i++)
        {
            int randomIndex = random.Next(i, availableIndices.Count);

            int temp = availableIndices[i];
            availableIndices[i] = availableIndices[randomIndex];
            availableIndices[randomIndex] = temp;
        }

        // 최대 5개 선택
        int count = Mathf.Min(
            dailyQuestionCount,
            availableIndices.Count
        );

        for (int i = 0; i < count; i++)
        {
            dailyQuestionIndices.Add(availableIndices[i]);
        }

        if (dailyQuestionIndices.Count == 0)
        {
            ShowEmptyState();
            return;
        }

        LoadQuestion();
    }


    //==================================================
    // 문제 불러오기
    //==================================================

    private void LoadQuestion()
    {
        // 오늘 문제를 모두 풀었으면 종료
        if (currentIndex >= dailyQuestionIndices.Count)
        {
            FinishDailyQuiz();
            return;
        }

        // 현재 문제가 몇 번째 문제인지 가져오기
        int quizIndex = dailyQuestionIndices[currentIndex];

        QuizData quiz = quizzes[quizIndex];

        // 답 선택 가능
        isAnswerLocked = false;

        if (answerPanelObject != null)
            answerPanelObject.SetActive(true);
        if (answerHeaderObject != null)
            answerHeaderObject.SetActive(true);

        // 문제 표시
        questionText.text = quiz.question;
        
        // 진행도 표시
        if (progressText != null)
            progressText.text = $"오늘의 숙제  {currentIndex + 1} / {dailyQuestionIndices.Count}";


        // 선택지 표시
        for (int i = 0; i < answerButtons.Length; i++)
        {
            // 선택지가 존재하는 경우
            if (i < quiz.choices.Length)
            {
                answerButtons[i].gameObject.SetActive(true);

                answerButtons[i]
                    .GetComponentInChildren<TMP_Text>()
                    .text = quiz.choices[i];

                answerButtons[i].interactable = true;
            }
            else
            {
                // 선택지가 없는 버튼은 숨김
                answerButtons[i].gameObject.SetActive(false);
            }
        }

        // 결과 텍스트 초기화
        if (resultText != null)
        {
            resultText.gameObject.SetActive(false);
            resultText.text = "";
        }
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
    }


    //==================================================
    // 답 확인
    //==================================================

    private void CheckAnswer(int selectedIndex)
    {
        // 이미 답을 선택했다면 무시
        if (isAnswerLocked)
            return;

        // 오늘 문제가 끝난 상태라면 무시
        if (isDailyQuizFinished)
            return;

        // 답 선택 잠금
        isAnswerLocked = true;

        // 현재 문제 가져오기
        int quizIndex = dailyQuestionIndices[currentIndex];

        QuizData quiz = quizzes[quizIndex];
        if (answerPanelObject != null)
            answerPanelObject.SetActive(true);

        if (resultText != null)
            resultText.gameObject.SetActive(true);
        if (resultBox != null)
            resultBox.gameObject.SetActive(true);

        // 정답 확인
        if (selectedIndex == quiz.answerIndex)
        {
            correctAnswerCount++;
            if (correctClip != null)
                feedbackAudioSource.PlayOneShot(correctClip, 0.48f);
            if (resultText != null)
                resultText.text = "정답입니다!";
            if (resultBox != null)
                resultBox.color = new Color(0.7f, 1f, 0.7f);
        }
        else
        {
            if (wrongClip != null)
                feedbackAudioSource.PlayOneShot(wrongClip, 0.44f);
            if (resultText != null)
                resultText.text = $"오답입니다. 정답: {quiz.choices[quiz.answerIndex]}";
            if (resultBox != null)
                resultBox.color = new Color(1f, 0.7f, 0.7f);
        }

        // 모든 버튼 잠그기
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i].interactable = false;
        }

        // 잠깐 결과를 보여준 뒤 다음 문제
        Invoke(nameof(NextQuestion), nextQuestionDelay);
    }


    //==================================================
    // 다음 문제
    //==================================================

    private void NextQuestion()
    {
        currentIndex++;

        // 5문제를 모두 풀었다면 종료
        if (currentIndex >= dailyQuestionIndices.Count)
        {
            FinishDailyQuiz();
            return;
        }

        // 다음 문제
        LoadQuestion();
    }


    //==================================================
    // 오늘의 문제 완료
    //==================================================

    private void FinishDailyQuiz()
    {
        isDailyQuizFinished = true;
        isAnswerLocked = true;

        // 문제 영역에 완료 메시지
        questionText.text = $"오늘의 숙제를 끝냈습니다!\n정답 {correctAnswerCount} / {dailyQuestionIndices.Count}\n\n2시간이 지났습니다.";

        // 결과 텍스트 제거
        if (progressText != null)
            progressText.text = "오늘 숙제 완료";
        if (resultText != null)
        {
            resultText.text = "";
            resultText.gameObject.SetActive(false);
        }
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        if (answerPanelObject != null)
            answerPanelObject.SetActive(false);

        // 완료 화면에서는 이전 문제의 선택지를 남기지 않는다.
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i].interactable = false;
            answerButtons[i].gameObject.SetActive(false);
        }

        if (!completionReported)
        {
            completionReported = true;
            DailyQuizCompleted?.Invoke(correctAnswerCount, dailyQuestionIndices.Count);
        }

        Debug.Log($"{configuredDay}일차 퀴즈 완료: {correctAnswerCount}/{dailyQuestionIndices.Count}");
    }

    private void ShowWeekendState()
    {
        isDailyQuizFinished = true;
        isAnswerLocked = true;
        dailyQuestionIndices.Clear();
        questionText.text = "주말에는 새로운 숙제가 없습니다.\n오늘은 알바 일정을 확인하세요.";
        if (progressText != null)
            progressText.text = "주말";
        if (resultText != null)
        {
            resultText.text = "";
            resultText.gameObject.SetActive(false);
        }
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        if (answerPanelObject != null)
            answerPanelObject.SetActive(false);

        foreach (Button button in answerButtons)
            button.gameObject.SetActive(false);
    }

    private void ShowEmptyState()
    {
        isDailyQuizFinished = true;
        isAnswerLocked = true;
        questionText.text = "등록된 문제가 없습니다.";
        if (progressText != null)
            progressText.text = "0 / 0";
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        if (answerPanelObject != null)
            answerPanelObject.SetActive(false);
        foreach (Button button in answerButtons)
            button.gameObject.SetActive(false);
    }


    //==================================================
    // 하루 문제 초기화
    //==================================================

    public void ResetDailyQuiz()
    {
        // 혹시 예약된 다음 문제 실행이 있다면 취소
        CancelInvoke(nameof(NextQuestion));

        // 새로운 랜덤 5문제 생성
        if (isWeekday)
            CreateDailyQuiz();
        else
            ShowWeekendState();

        Debug.Log("오늘의 퀴즈를 초기화했습니다.");
    }
}
