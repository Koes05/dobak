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

    [Header("문제 데이터")]
    [SerializeField] private QuizData[] quizzes;

    [Header("다음 문제까지 대기 시간")]
    [SerializeField] private float nextQuestionDelay = 1.0f;

    // 현재 문제 번호
    private int currentIndex = 0;

    // 답을 이미 선택했는지 확인
    private bool isAnswerLocked = false;


    private void Start()
    {
        // 버튼 연결
        for (int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;

            answerButtons[i].onClick.AddListener(() =>
            {
                CheckAnswer(index);
            });
        }

        LoadQuestion();
    }


    //========================================
    // 문제 불러오기
    //========================================

    private void LoadQuestion()
    {
        QuizData quiz = quizzes[currentIndex];

        // 새로운 문제가 시작됐으므로
        // 다시 답을 선택할 수 있도록 잠금 해제
        isAnswerLocked = false;

        // 문제 표시
        questionText.text = quiz.question;

        // 선택지 표시
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i]
                .GetComponentInChildren<TMP_Text>()
                .text = quiz.choices[i];

            // 버튼도 다시 활성화
            answerButtons[i].interactable = true;
        }

        // 결과 텍스트 초기화
        resultText.text = "";
    }


    //========================================
    // 답 확인
    //========================================

    private void CheckAnswer(int selectedIndex)
    {
        // 이미 답을 선택했다면 무시
        if (isAnswerLocked)
            return;

        // 답 선택 잠금
        isAnswerLocked = true;

        QuizData quiz = quizzes[currentIndex];

        // 정답 확인
        if (selectedIndex == quiz.answerIndex)
        {
            resultText.text = "정답입니다!";
        }
        else
        {
            resultText.text = "오답입니다!";
        }

        // 모든 버튼 잠그기
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i].interactable = false;
        }

        // 잠깐 결과를 보여준 뒤 다음 문제
        Invoke(nameof(NextQuestion), nextQuestionDelay);
    }


    //========================================
    // 다음 문제
    //========================================

    private void NextQuestion()
    {
        currentIndex++;

        // 마지막 문제면 처음으로
        if (currentIndex >= quizzes.Length)
        {
            currentIndex = 0;
        }

        LoadQuestion();
    }
}