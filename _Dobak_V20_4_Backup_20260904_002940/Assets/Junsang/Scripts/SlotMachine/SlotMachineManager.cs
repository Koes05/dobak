using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System;
using TMPro;
using Dobak.Manager;

namespace Dobak.App.Casino.SlotMachine
{
    public class SlotMachineManager : MonoBehaviour
    {
        public static event Action<bool, int> SpinResolved;

        [Header("참조")]
        [SerializeField] private SymbolDatabase symbolDatabase;
        [SerializeField] private ReelUI[] reels;          // 3개 등록 (좌 -> 우 순서)
        [SerializeField] private Button spinButton;
        [SerializeField] private TMP_Text creditText;          // TextMeshPro 쓰면 TMP_Text로 교체
        [SerializeField] private TMP_Text betText;
        [SerializeField] private TMP_Text resultText;

        [Header("게임 설정")]
        [SerializeField] private int betAmount = 10;
        [SerializeField] private int[] betOptions = { 100, 500, 1000 };

        [Header("연출 타이밍")]
        [Tooltip("릴 하나가 도는 기본 시간(초)")]
        [SerializeField] private float baseSpinDuration = 1.0f;
        [Tooltip("릴마다 정지 타이밍을 살짝 늦춰서 왼쪽부터 순서대로 멈추는 느낌을 줌")]
        [SerializeField] private float perReelDelay = 0.4f;

        private bool isSpinning = false;
        private bool stakeCommitted;
        private int currentRound = 0;
        private readonly SessionTracker sessionTracker = new SessionTracker();
        private int betOptionIndex;
        private Button decreaseBetButton;
        private Button increaseBetButton;
        private GameObject winOverlay;
        private CanvasGroup winOverlayGroup;
        private TMP_Text winOverlayText;

        public int CurrentRound => currentRound;
        public int CurrentBetAmount => betAmount;

        public static int CalculatePayout(int bet, float multiplier)
        {
            return Mathf.Max(0, Mathf.RoundToInt(bet * Mathf.Max(2f, multiplier)));
        }

        private void OnEnable()
        {
            if (CoinManager.Instance == null) return;

            CoinManager.Instance.OnCasinoCashChanged += UpdateUI;
            if (spinButton != null)
                spinButton.interactable = true;
            UpdateUI(CoinManager.Instance.CasinoCash);
        }

        private void OnDisable()
        {
            if (CoinManager.Instance != null)
                CoinManager.Instance.OnCasinoCashChanged -= UpdateUI;
            ResetInterruptedSpin();
        }

        private void Start()
        {
            foreach (var reel in reels)
                reel.Init(symbolDatabase);

            spinButton.onClick.AddListener(OnSpinButtonPressed);

            betOptionIndex = FindClosestBetOption(betAmount);
            betAmount = betOptions[betOptionIndex];
            CreateBetControls();
            CreateWinOverlay();

            if (resultText != null)
                resultText.text = "결과를 기다리는 중";

            UpdateUI(CoinManager.Instance.CasinoCash);
        }

        private void OnSpinButtonPressed()
        {
            if (isSpinning) return;

            if (GameFlowManager.Instance != null && !GameFlowManager.Instance.IsGamblingUnlocked)
            {
                resultText.text = "메시지의 초대를 먼저 확인하세요";
                return;
            }

            if (GameFlowManager.Instance != null && !GameFlowManager.Instance.CanSpendTime(1))
            {
                resultText.text = "지금은 진행할 수 없습니다";
                return;
            }

            bool canSpin = CoinManager.Instance.TryBetCasino(betAmount);

            if (!canSpin)
            {
                resultText.text = "크레딧이 부족합니다";
                return;
            }

            stakeCommitted = true;
            GameFlowManager.Instance?.SpendTime(1, "도박 한 판");

            if (GameFlowManager.Instance != null && GameFlowManager.Instance.IsGameEnded)
            {
                ResetInterruptedSpin();
                resultText.text = "하루가 끝나 이번 판은 취소되었습니다";
                return;
            }

            StartCoroutine(SpinAllReels());
        }

        private IEnumerator SpinAllReels()
        {
            isSpinning = true;
            spinButton.interactable = false;
            SetBetControlsInteractable(false);
            resultText.text = "";

            // 1. 순수 확률(가중치)로 결과를 뽑는다
            SlotSymbol[] finalResults = new SlotSymbol[reels.Length];
            for (int i = 0; i < reels.Length; i++)
                finalResults[i] = symbolDatabase.GetRandomWeightedSymbol();

            int upcomingRound = currentRound + 1;
            float sessionWinBoost = GetSessionWinBoost(upcomingRound);
            if (!IsAllSame(finalResults) && UnityEngine.Random.value < sessionWinBoost)
            {
                SlotSymbol boostedResult = symbolDatabase.GetRandomWeightedSymbol();
                for (int i = 0; i < finalResults.Length; i++)
                    finalResults[i] = boostedResult;
            }

            bool wasNaturalWin = IsAllSame(finalResults);
            bool wasSuppressed = false;

            // 2. 원래 당첨이었다면, 베팅액에 비례한 확률로 강제로 무효화(조작)한다
            if (wasNaturalWin)
            {
                float suppressionChance = BetOddsModifier.GetSuppressionChance(betAmount);
                if (UnityEngine.Random.value < suppressionChance)
                {
                    wasSuppressed = true;
                    BreakWinningResult(finalResults);
                }
            }

            // 3. 릴 스핀 연출 (조작이 반영된 최종 결과를 그대로 보여줌 - 시각적으로도 '꽝'으로 보임)
            int reelsFinished = 0;
            for (int i = 0; i < reels.Length; i++)
            {
                int index = i;
                float duration = baseSpinDuration + perReelDelay * i;
                reels[index].Spin(duration, finalResults[index], () => { reelsFinished++; });
            }

            float totalWait = baseSpinDuration + perReelDelay * (reels.Length - 1);
            yield return new WaitForSeconds(totalWait + 0.1f);

            // 앱을 닫아 중단된 판은 코루틴이 여기까지 오지 않으므로 회차에 포함하지 않는다.
            currentRound++;
            sessionTracker.LogSpin(
                currentRound,
                betAmount,
                wasNaturalWin,
                wasSuppressed,
                finalResults[0] != null ? finalResults[0].symbolName : "-"
            );

            EvaluateResult(finalResults);

            isSpinning = false;
            UpdateUI(CoinManager.Instance.CasinoCash);
        }

        private bool IsAllSame(SlotSymbol[] results)
        {
            for (int i = 1; i < results.Length; i++)
            {
                if (results[i] != results[0]) return false;
            }
            return true;
        }

        public static float GetSessionWinBoost(int round)
        {
            if (round >= 10) return 0.12f;
            if (round >= 5) return 0.06f;
            return 0f;
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
            int payout = 0;
            stakeCommitted = false;

            if (allSame)
            {
                float multiplier = Mathf.Max(2f, results[0].payoutMultiplier);
                payout = CalculatePayout(betAmount, multiplier);
                int profit = payout - betAmount;
                resultText.text = $"{multiplier:0.#}배 당첨! {payout:N0}P 지급 (순이익 +{profit:N0}P)";
                resultText.color = new Color(1f, 0.78f, 0.12f);
                CoinManager.Instance.AddCasinoCredit(payout);
                StartCoroutine(ShowWinCelebration(payout, multiplier, profit));
            }
            else
            {
                resultText.text = "꽝";
                resultText.color = Color.black;
            }

            spinButton.interactable = true;

            UpdateUI(CoinManager.Instance.CasinoCash);
            SpinResolved?.Invoke(allSame, payout);
        }

        private void ResetInterruptedSpin()
        {
            if (!isSpinning && !stakeCommitted)
                return;

            StopAllCoroutines();
            if (stakeCommitted && CoinManager.Instance != null)
                CoinManager.Instance.AddCasinoCredit(betAmount);

            stakeCommitted = false;
            isSpinning = false;
            if (spinButton != null)
                spinButton.interactable = true;
            SetBetControlsInteractable(true);
            if (winOverlay != null)
                winOverlay.SetActive(false);
        }

        private void UpdateUI(int cash)
        {
            creditText.text = $"사이트 포인트 {cash:N0}P";
            betText.text = $"베팅 {betAmount:N0}P";
            SetBetControlsInteractable(!isSpinning);
        }

        private int FindClosestBetOption(int amount)
        {
            int closest = 0;
            int difference = int.MaxValue;
            for (int i = 0; i < betOptions.Length; i++)
            {
                int candidateDifference = Mathf.Abs(betOptions[i] - amount);
                if (candidateDifference < difference)
                {
                    closest = i;
                    difference = candidateDifference;
                }
            }

            return closest;
        }

        private void ChangeBet(int direction)
        {
            if (isSpinning || CoinManager.Instance == null)
                return;

            int next = Mathf.Clamp(betOptionIndex + direction, 0, betOptions.Length - 1);
            if (betOptions[next] > CoinManager.Instance.CasinoCash)
                return;

            betOptionIndex = next;
            betAmount = betOptions[betOptionIndex];
            UpdateUI(CoinManager.Instance.CasinoCash);
        }

        private void SetBetControlsInteractable(bool enabled)
        {
            if (decreaseBetButton != null)
                decreaseBetButton.interactable = enabled && betOptionIndex > 0;
            if (increaseBetButton != null)
            {
                bool affordable = CoinManager.Instance != null && betOptionIndex + 1 < betOptions.Length &&
                                  betOptions[betOptionIndex + 1] <= CoinManager.Instance.CasinoCash;
                increaseBetButton.interactable = enabled && affordable;
            }
        }

        private void CreateBetControls()
        {
            RectTransform spinRect = spinButton.GetComponent<RectTransform>();
            decreaseBetButton = CreateControlButton("Decrease Bet", spinRect.parent, "-", spinRect, -210f);
            increaseBetButton = CreateControlButton("Increase Bet", spinRect.parent, "+", spinRect, 210f);
            decreaseBetButton.onClick.AddListener(() => ChangeBet(-1));
            increaseBetButton.onClick.AddListener(() => ChangeBet(1));
        }

        private Button CreateControlButton(string objectName, Transform parent, string label, RectTransform reference, float xOffset)
        {
            var go = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.layer = gameObject.layer;
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = reference.anchorMin;
            rect.anchorMax = reference.anchorMax;
            rect.pivot = reference.pivot;
            rect.anchoredPosition = reference.anchoredPosition + new Vector2(xOffset, 0f);
            rect.sizeDelta = new Vector2(120f, Mathf.Max(76f, reference.sizeDelta.y));

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.12f, 0.16f, 0.22f, 1f);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.layer = go.layer;
            textObject.transform.SetParent(go.transform, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
            text.font = betText.font;
            text.fontSize = 46f;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.text = label;
            return button;
        }

        private void CreateWinOverlay()
        {
            winOverlay = new GameObject("Win Celebration", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            winOverlay.layer = gameObject.layer;
            winOverlay.transform.SetParent(transform, false);
            RectTransform rect = winOverlay.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Image flash = winOverlay.GetComponent<Image>();
            flash.color = new Color(1f, 0.68f, 0.05f, 0.22f);
            flash.raycastTarget = false;

            winOverlayGroup = winOverlay.GetComponent<CanvasGroup>();
            winOverlayGroup.blocksRaycasts = false;
            winOverlayGroup.interactable = false;

            var textObject = new GameObject("Win Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.layer = gameObject.layer;
            textObject.transform.SetParent(winOverlay.transform, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.15f, 0.34f);
            textRect.anchorMax = new Vector2(0.85f, 0.68f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            winOverlayText = textObject.GetComponent<TextMeshProUGUI>();
            winOverlayText.font = betText.font;
            winOverlayText.fontSize = 76f;
            winOverlayText.fontStyle = FontStyles.Bold;
            winOverlayText.alignment = TextAlignmentOptions.Center;
            winOverlayText.color = new Color(1f, 0.86f, 0.18f);
            winOverlayText.raycastTarget = false;
            winOverlay.SetActive(false);
        }

        private IEnumerator ShowWinCelebration(int payout, float multiplier, int profit)
        {
            winOverlayText.text = $"{multiplier:0.#}배 당첨!\n{payout:N0}P 지급\n순이익 +{profit:N0}P";
            winOverlay.SetActive(true);
            winOverlay.transform.SetAsLastSibling();
            winOverlayGroup.alpha = 1f;

            float elapsed = 0f;
            while (elapsed < 0.35f)
            {
                elapsed += Time.unscaledDeltaTime;
                float scale = Mathf.Lerp(0.55f, 1.15f, elapsed / 0.35f);
                winOverlayText.rectTransform.localScale = Vector3.one * scale;
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < 0.18f)
            {
                elapsed += Time.unscaledDeltaTime;
                winOverlayText.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.15f, 1f, elapsed / 0.18f);
                yield return null;
            }

            yield return new WaitForSecondsRealtime(0.85f);
            elapsed = 0f;
            while (elapsed < 0.35f)
            {
                elapsed += Time.unscaledDeltaTime;
                winOverlayGroup.alpha = 1f - elapsed / 0.35f;
                yield return null;
            }

            winOverlay.SetActive(false);
        }
    }
}
