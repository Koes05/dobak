using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using TMPro;
using Dobak.Manager;

namespace Dobak.App.Casino.SlotMachine
{
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

        [Header("확률 설정")]
        [SerializeField] private float baseWinChance = 0.1f; // 기본 당첨 확률 (10%)
        [SerializeField] private float winChanceDecay = 0.02f; // 당첨될 때마다 줄어드는 확률 (2%)
        private float currentWinChance;


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
            if (CoinManager.Instance == null) return;

            CoinManager.Instance.OnCasinoCashChanged += UpdateUI;
        }

        private void OnDisable()
        {
            if (CoinManager.Instance == null) return;

            CoinManager.Instance.OnCasinoCashChanged -= UpdateUI;
        }

        private void Start()
        {
            foreach (var reel in reels)
                reel.Init(symbolDatabase);

            spinButton.onClick.AddListener(OnSpinButtonPressed);

            UpdateUI(CoinManager.Instance.CasinoCash);

            currentWinChance = baseWinChance; // 시작 시 기본 확률로 초기화
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

            // 1. 당첨 여부를 확률로 결정
            bool forceWin = Random.value < currentWinChance;

            SlotSymbol[] finalResults = new SlotSymbol[reels.Length];
            if (forceWin)
            {
                // 강제로 당첨 결과 생성
                SlotSymbol winSymbol = symbolDatabase.GetRandomWeightedSymbol();
                for (int i = 0; i < reels.Length; i++)
                    finalResults[i] = winSymbol;
            }
            else
            {
                // 일반 랜덤 결과
                for (int i = 0; i < reels.Length; i++)
                    finalResults[i] = symbolDatabase.GetRandomWeightedSymbol();
            }

            // 2. 세션 로그 기록
            currentRound++;
            sessionTracker.LogSpin(
                currentRound,
                betAmount,
                forceWin,
                false,
                finalResults[0] != null ? finalResults[0].symbolName : "-"
            );

            // 3. 릴 스핀 연출
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

        private void EvaluateResult(SlotSymbol[] results)
        {
            bool allSame = IsAllSame(results);

            if (allSame)
            {
                int payout = Mathf.RoundToInt(betAmount * results[0].payoutMultiplier);
                resultText.text = $"당첨! {results[0].symbolName} x3 -> +{payout}";
                CoinManager.Instance.AddCasinoCredit(payout);

                // 당첨 시 확률 감소
                currentWinChance = Mathf.Max(0f, currentWinChance - winChanceDecay);
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
            creditText.text = $"잔액: {cash} 원";
            betText.text = $"베팅: {betAmount} ({BetOddsModifier.GetBetTierLabel(betAmount)})";
        }
    }
}
