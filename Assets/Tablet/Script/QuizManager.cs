using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class QuizManager : MonoBehaviour
{
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


    //==================================================
    // 시작
    //==================================================

    private void Start()
    {
        // 선택 버튼 연결
        for (int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;

            answerButtons[i].onClick.AddListener(() =>
            {
                CheckAnswer(index);
            });
        }

        // 테스트용 초기화 버튼
        if (resetButton != null)
        {
            resetButton.onClick.AddListener(ResetDailyQuiz);
        }

        // 처음 시작할 때 오늘의 문제 5개 뽑기
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

        // 전체 문제의 번호를 임시 리스트에 넣기
        List<int> availableIndices = new List<int>();

        for (int i = 0; i < quizzes.Length; i++)
        {
            availableIndices.Add(i);
        }

        // 랜덤하게 섞기
        for (int i = 0; i < availableIndices.Count; i++)
        {
            int randomIndex = Random.Range(i, availableIndices.Count);

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

        // 첫 문제 불러오기
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

        // 문제 표시
        questionText.text = quiz.question;
        
        // 진행도 표시
        progressText.text = $"남은 문제: {currentIndex + 1} / {dailyQuestionIndices.Count}";


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
        resultText.text = "";
        resultBox.color = Color.white;
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

        // 정답 확인
        if (selectedIndex == quiz.answerIndex)
        {
            resultText.text = "정답입니다!";
            resultBox.color = new Color(0.7f, 1f, 0.7f);
        }
        else
        {
            resultText.text = "오답입니다!";
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
        questionText.text = "오늘의 문제는 모두 풀었습니다!";

        // 결과 텍스트 제거
        resultText.text = "";
        resultBox.color = Color.white;

        // 선택 버튼 전부 잠금
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i].interactable = false;
        }

        Debug.Log("오늘의 퀴즈 완료!");
    }


    //==================================================
    // 하루 문제 초기화
    //==================================================

    public void ResetDailyQuiz()
    {
        // 혹시 예약된 다음 문제 실행이 있다면 취소
        CancelInvoke(nameof(NextQuestion));

        // 새로운 랜덤 5문제 생성
        CreateDailyQuiz();

        Debug.Log("오늘의 퀴즈를 초기화했습니다.");
    }
}