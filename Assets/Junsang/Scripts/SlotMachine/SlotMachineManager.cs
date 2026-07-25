using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using TMPro;

public class SlotMachineManager : MonoBehaviour
{
    [Header("참조")]
    [SerializeField] private SymbolDatabase symbolDatabase;
    [SerializeField] private ReelUI[] reels;          // 3개 등록 (좌 -> 우 순서)
    [SerializeField] private Button spinButton;
    [SerializeField] private TMP_Text creditText;          // TextMeshPro 쓰면 TMP_Text로 교체
    [SerializeField] private TMP_Text betText;
    [SerializeField] private TMP_Text resultText;

    [Header("게임 설정")]
    [SerializeField] private int betAmount = 10;

    [Header("연출 타이밍")]
    [Tooltip("릴 하나가 도는 기본 시간(초)")]
    [SerializeField] private float baseSpinDuration = 1.0f;
    [Tooltip("릴마다 정지 타이밍을 살짝 늦춰서 왼쪽부터 순서대로 멈추는 느낌을 줌")]
    [SerializeField] private float perReelDelay = 0.4f;

    private bool isSpinning = false;
    private int currentRound = 0;
    private readonly SessionTracker sessionTracker = new SessionTracker();

    private void OnEnable()
    {
        if(CoinManager.Instance == null) return;

        CoinManager.Instance.OnCasinoCashChanged += UpdateUI;
    }

    private void OnDisable()
    {
        if(CoinManager.Instance == null) return;

        CoinManager.Instance.OnCasinoCashChanged -= UpdateUI;
    }

    private void Start()
    {
        foreach (var reel in reels)
            reel.Init(symbolDatabase);

        spinButton.onClick.AddListener(OnSpinButtonPressed);

        UpdateUI(CoinManager.Instance.CasinoCash);
    }

    private void OnSpinButtonPressed()
    {
        if (isSpinning) return;

        bool canSpin = CoinManager.Instance.TryBetCasino(betAmount);

        if (!canSpin)
        {
            resultText.text = "크레딧이 부족합니다";
            return;
        }

        StartCoroutine(SpinAllReels());
    }

    private IEnumerator SpinAllReels()
    {
        isSpinning = true;
        spinButton.interactable = false;
        resultText.text = "";

        // 1. 순수 확률(가중치)로 결과를 뽑는다
        SlotSymbol[] finalResults = new SlotSymbol[reels.Length];
        for (int i = 0; i < reels.Length; i++)
            finalResults[i] = symbolDatabase.GetRandomWeightedSymbol();

        bool wasNaturalWin = IsAllSame(finalResults);
        bool wasSuppressed = false;

        // 2. 원래 당첨이었다면, 베팅액에 비례한 확률로 강제로 무효화(조작)한다
        if (wasNaturalWin)
        {
            float suppressionChance = BetOddsModifier.GetSuppressionChance(betAmount);
            if (Random.value < suppressionChance)
            {
                wasSuppressed = true;
                BreakWinningResult(finalResults);
            }
        }

        // 3. 세션 로그에 기록 (원래 결과의 심볼 이름은 로그용으로 조작 전 심볼을 남긴다)
        currentRound++;
        sessionTracker.LogSpin(
            currentRound,
            betAmount,
            wasNaturalWin,
            wasSuppressed,
            finalResults[0] != null ? finalResults[0].symbolName : "-"
        );

        // 4. 릴 스핀 연출 (조작이 반영된 최종 결과를 그대로 보여줌 - 시각적으로도 '꽝'으로 보임)
        int reelsFinished = 0;
        for (int i = 0; i < reels.Length; i++)
        {
            int index = i;
            float duration = baseSpinDuration + perReelDelay * i;
            reels[index].Spin(duration, finalResults[index], () => { reelsFinished++; });
        }

        float totalWait = baseSpinDuration + perReelDelay * (reels.Length - 1);
        yield return new WaitForSeconds(totalWait + 0.1f);

        EvaluateResult(finalResults);

        isSpinning = false;
    }

    private bool IsAllSame(SlotSymbol[] results)
    {
        for (int i = 1; i < results.Length; i++)
        {
            if (results[i] != results[0]) return false;
        }
        return true;
    }

    // 당첨 결과를 강제로 깨뜨린다: 마지막 릴 심볼을 첫 심볼과 다른 것으로 교체
    private void BreakWinningResult(SlotSymbol[] results)
    {
        SlotSymbol original = results[0];
        SlotSymbol replacement;

        do
        {
            replacement = symbolDatabase.GetRandomWeightedSymbol();
        } while (replacement == original && symbolDatabase.symbols.Count > 1);

        results[results.Length - 1] = replacement;
    }

    private void EvaluateResult(SlotSymbol[] results)
    {
        bool allSame = IsAllSame(results);

        if (allSame)
        {
            int payout = Mathf.RoundToInt(betAmount * results[0].payoutMultiplier);
            resultText.text = $"당첨! {results[0].symbolName} x3 -> +{payout}";
            CoinManager.Instance.AddCasinoCredit(payout);
        }
        else
        {
            resultText.text = "꽝";
        }

        spinButton.interactable = true;

        UpdateUI(CoinManager.Instance.CasinoCash);
    }

    private void UpdateUI(int cash)
    {
        creditText.text = $"Cash: ${cash}";
        betText.text = $"BET: {betAmount} ({BetOddsModifier.GetBetTierLabel(betAmount)})";
    }
}
