using System;

[Serializable]
public class QuizData
{
    // 문제 내용
    public string question;

    // 선택지 4개
    public string[] choices = new string[4];

    // 정답 번호
    // 0 = 첫 번째 선택지
    // 1 = 두 번째 선택지
    // 2 = 세 번째 선택지
    // 3 = 네 번째 선택지
    public int answerIndex;
}