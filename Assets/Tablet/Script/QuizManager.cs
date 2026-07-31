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



    // 현재 문제 번호
    private int currentIndex = 0;



    private void Start()
    {
        // 버튼 연결
        for(int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;

            answerButtons[i].onClick.AddListener(() =>
            {
                CheckAnswer(index);
            });
        }


        LoadQuestion();
    }



    // 문제 불러오기
    private void LoadQuestion()
    {
        QuizData quiz = quizzes[currentIndex];


        // 문제 표시
        questionText.text = quiz.question;


        // 선택지 표시
        for(int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i]
                .GetComponentInChildren<TMP_Text>()
                .text = quiz.choices[i];
        }


        resultText.text = "";
    }




    // 답 확인
    private void CheckAnswer(int selectedIndex)
    {
        QuizData quiz = quizzes[currentIndex];


        if(selectedIndex == quiz.answerIndex)
        {
            resultText.text = "정답입니다!";
        }
        else
        {
            resultText.text = "오답입니다!";
        }


        // 잠깐 보여주고 다음 문제
        Invoke(nameof(NextQuestion),1.0f);
    }



    // 다음 문제
    private void NextQuestion()
    {
        currentIndex++;


        // 마지막 문제면 처음으로
        if(currentIndex >= quizzes.Length)
        {
            currentIndex = 0;
        }


        LoadQuestion();
    }
}