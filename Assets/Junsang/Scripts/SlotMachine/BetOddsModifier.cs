using UnityEngine;

// 베팅액이 커질수록 "원래 당첨이었던 결과를 강제로 무효화할 확률"을 계산한다.
// 교육/시뮬레이션 목적: 실제 사행성 기기에서 발생할 수 있는 조작 패턴을 체험시키기 위한 장치.
// 이 모듈이 적용되는 게임은 반드시 세션 종료 시 SessionTracker의 리포트를 통해
// 플레이어에게 "당신의 베팅이 이렇게 조작되었다"는 사실을 명시적으로 공개해야 한다.
public static class BetOddsModifier
{
    [Tooltip("이 베팅액 이하에서는 조작이 전혀 없음 (공정한 확률)")]
    public const int BaselineBet = 10;

    [Tooltip("이 베팅액 이상이면 최대 억제 확률에 도달")]
    public const int HighBetThreshold = 100;

    [Tooltip("최대로 적용되는 당첨 무효화 확률 (0~1)")]
    public const float MaxSuppressionChance = 0.6f;

    // betAmount가 클수록 0 -> MaxSuppressionChance로 선형 증가하는 억제 확률 반환
    public static float GetSuppressionChance(int betAmount)
    {
        if (betAmount <= BaselineBet) return 0f;

        float t = Mathf.InverseLerp(BaselineBet, HighBetThreshold, betAmount);
        return Mathf.Lerp(0f, MaxSuppressionChance, t);
    }

    // 베팅액을 사람이 읽기 쉬운 구간(저/중/고)으로 분류 - 리포트 집계용
    public static string GetBetTierLabel(int betAmount)
    {
        if (betAmount <= BaselineBet) return "저액 베팅";
        if (betAmount >= HighBetThreshold) return "고액 베팅";
        return "중간 베팅";
    }
}
