using System.Collections.Generic;
using System.Text;
using UnityEngine;

// 스핀 1회에 대한 기록
public struct SpinRecord
{
    public int roundNumber;
    public int betAmount;
    public bool wasNaturalWin;   // 조작 없이 순수 확률로는 당첨이었는지
    public bool wasSuppressed;   // 조작으로 인해 당첨이 무효화됐는지
    public string symbolName;    // 원래 나왔어야 할 심볼 (당첨/조작 여부와 무관하게 로그용)
}

// 세션(예: 50판) 동안의 모든 스핀을 기록하고, 종료 시 플레이어에게 보여줄 리포트를 생성한다.
public class SessionTracker
{
    private readonly List<SpinRecord> records = new List<SpinRecord>();

    public int RoundCount => records.Count;

    public void LogSpin(int roundNumber, int betAmount, bool wasNaturalWin, bool wasSuppressed, string symbolName)
    {
        records.Add(new SpinRecord
        {
            roundNumber = roundNumber,
            betAmount = betAmount,
            wasNaturalWin = wasNaturalWin,
            wasSuppressed = wasSuppressed,
            symbolName = symbolName
        });
    }

    public void Reset()
    {
        records.Clear();
    }

    // 세션 종료 시 플레이어에게 보여줄 요약 리포트 텍스트 생성
    public string GenerateReport()
    {
        int totalSpins = records.Count;
        int naturalWins = 0;
        int suppressedWins = 0;

        var tierSuppressed = new Dictionary<string, int>();
        var tierNaturalWins = new Dictionary<string, int>();

        foreach (var r in records)
        {
            if (r.wasNaturalWin) naturalWins++;
            if (r.wasSuppressed) suppressedWins++;

            string tier = BetOddsModifier.GetBetTierLabel(r.betAmount);

            if (r.wasNaturalWin)
            {
                tierNaturalWins.TryGetValue(tier, out int nw);
                tierNaturalWins[tier] = nw + 1;
            }
            if (r.wasSuppressed)
            {
                tierSuppressed.TryGetValue(tier, out int sw);
                tierSuppressed[tier] = sw + 1;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== 이번 세션 리포트 ===");
        sb.AppendLine($"총 {totalSpins}판 플레이");
        sb.AppendLine($"원래 확률대로면 당첨이었던 횟수: {naturalWins}회");
        sb.AppendLine($"그 중 강제로 '꽝'으로 조작된 횟수: {suppressedWins}회");

        if (naturalWins > 0)
        {
            float suppressRate = (float)suppressedWins / naturalWins * 100f;
            sb.AppendLine($"→ 당첨이었어야 할 결과 중 {suppressRate:F1}%가 조작으로 사라졌습니다.");
        }

        sb.AppendLine();
        sb.AppendLine("[베팅 구간별 조작 내역]");
        foreach (var tier in tierNaturalWins.Keys)
        {
            int nw = tierNaturalWins.TryGetValue(tier, out int a) ? a : 0;
            int sw = tierSuppressed.TryGetValue(tier, out int b) ? b : 0;
            string rate = nw > 0 ? $"{(float)sw / nw * 100f:F1}%" : "0%";
            sb.AppendLine($"- {tier}: 원래 당첨 {nw}회 중 {sw}회 조작됨 ({rate})");
        }

        sb.AppendLine();
        sb.AppendLine("실제 카지노/불법 사행성 기기에서도 이런 방식으로");
        sb.AppendLine("베팅액에 따라 몰래 확률을 바꾸는 조작이 존재할 수 있습니다.");
        sb.AppendLine("이런 조작은 눈으로 확인할 방법이 없다는 점이 가장 위험합니다.");

        return sb.ToString();
    }
}
