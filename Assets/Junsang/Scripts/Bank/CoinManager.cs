using System;
using UnityEngine;
using System.Collections.Generic;

namespace Dobak.Manager
{
    // 거래 기록이 어느 잔액에 영향을 준 것인지 구분
    public enum TransactionScope
    {
        BankToCasinoCharge, // 뱅크 -> 카지노 충전 (뱅크 캐시 감소)
        CasinoToBankCashOut, // 카지노 -> 뱅크 환전
        DebtRepayment,      // 빌린 돈 상환
        CasinoBet,          // 카지노 내 베팅 (카지노 캐시 감소)
        CasinoWin,          // 카지노 내 당첨 (카지노 캐시 증가)
        ExternalIncome      // 알바비, 가상 대출 등 뱅크 캐시 증가
    }

    public enum ChargeToCasinoFailureReason
    {
        None,
        InvalidAmount,
        InsufficientBankCash
    }

    [Serializable]
    public struct TransactionRecord
    {
        public string description;      // 예: "Use 10$ in Casino", "Win 20$ in Casino"
        public int amount;              // 음수=지출, 양수=수입
        public TransactionScope scope;
        public int bankBalanceAfter;
        public int casinoBalanceAfter;
        public DateTime timestamp;
    }

    // 잔액을 전역에서 관리하는 싱글톤.
    // SlotMachinePanel, ChargePanel, MyPagePanel, TopBar 등 어디서든
    // CoinManager.Instance로 접근해서 잔액을 읽거나 변경한다.
    public class CoinManager : MonoBehaviour
    {
        public const int WonPerPoint = 10;
        public static CoinManager Instance { get; private set; }

        [SerializeField] private int startingBankCash = 100000;

        public int BankCash { get; private set; }
        public int CasinoCash { get; private set; } // 시작은 항상 0

        public event Action<int> OnBankCashChanged;
        public event Action<int> OnCasinoCashChanged;
        public event Action<TransactionRecord> OnTransactionAdded;
        public event Action<int, int> OnCasinoChargeCompleted;

        private readonly List<TransactionRecord> history = new List<TransactionRecord>();
        public IReadOnlyList<TransactionRecord> History => history;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            BankCash = startingBankCash;
            CasinoCash = 0;
        }

        // 뱅크 -> 카지노 충전. 뱅크 캐시가 충분해야 성공.
        public bool ChargeToCasino(int amount)
        {
            return TryChargeToCasino(amount, out _);
        }

        public bool TryChargeToCasino(int amount, out ChargeToCasinoFailureReason failureReason)
        {
            if (amount <= 0)
            {
                failureReason = ChargeToCasinoFailureReason.InvalidAmount;
                return false;
            }

            if (BankCash < amount)
            {
                failureReason = ChargeToCasinoFailureReason.InsufficientBankCash;
                return false;
            }

            int points = ConvertWonToPoints(amount);
            if (points <= 0)
            {
                failureReason = ChargeToCasinoFailureReason.InvalidAmount;
                return false;
            }

            BankCash -= amount;
            CasinoCash += points;
            failureReason = ChargeToCasinoFailureReason.None;

            OnBankCashChanged?.Invoke(BankCash);
            OnCasinoCashChanged?.Invoke(CasinoCash);
            AddRecord("사이트 포인트 충전", -amount, TransactionScope.BankToCasinoCharge);
            OnCasinoChargeCompleted?.Invoke(amount, points);

            return true;
        }

        public static int ConvertWonToPoints(int won)
        {
            return won > 0 && won % WonPerPoint == 0 ? won / WonPerPoint : 0;
        }

        // 카지노 내 베팅: 오직 카지노 캐시만 사용, 뱅크 캐시는 건드리지 않음
        public bool TryBetCasino(int amount)
        {
            if (amount <= 0 || CasinoCash < amount) return false;

            CasinoCash -= amount;
            OnCasinoCashChanged?.Invoke(CasinoCash);

            return true;
        }

        // 카지노 당첨금 등, 카지노 캐시만 증가 (뱅크로 자동 환급되지 않음)
        public void AddCasinoCredit(int amount)
        {
            if (amount <= 0) return;

            CasinoCash += amount;
            OnCasinoCashChanged?.Invoke(CasinoCash);
        }

        public bool TryCashOutCasino(out int points, out int won)
        {
            points = CasinoCash;
            won = points * WonPerPoint;
            if (points <= 0 || won <= 0)
                return false;

            CasinoCash = 0;
            BankCash += won;
            OnCasinoCashChanged?.Invoke(CasinoCash);
            OnBankCashChanged?.Invoke(BankCash);
            AddRecord("사이트 포인트 환전", won, TransactionScope.CasinoToBankCashOut);
            return true;
        }

        public bool TrySpendBankCash(int amount, string description, TransactionScope scope)
        {
            if (amount <= 0 || BankCash < amount)
                return false;

            BankCash -= amount;
            OnBankCashChanged?.Invoke(BankCash);
            AddRecord(description, -amount, scope);
            return true;
        }

        public void AddBankCash(int amount, string description = "입금")
        {
            if (amount <= 0) return;

            BankCash += amount;
            OnBankCashChanged?.Invoke(BankCash);
            AddRecord(description, amount, TransactionScope.ExternalIncome);
        }

        public void SetBankCash(int amount, string description = "시나리오 잔액 설정")
        {
            int nextValue = Mathf.Max(0, amount);
            int delta = nextValue - BankCash;
            if (delta == 0)
                return;

            BankCash = nextValue;
            OnBankCashChanged?.Invoke(BankCash);
            AddRecord(description, delta, TransactionScope.ExternalIncome);
        }

        public void AdjustBankCash(int amount, string description = "시나리오 거래")
        {
            SetBankCash(BankCash + amount, description);
        }

        public void ResetScenarioBalances(int bankCash)
        {
            history.Clear();
            BankCash = Mathf.Max(0, bankCash);
            CasinoCash = 0;
            OnBankCashChanged?.Invoke(BankCash);
            OnCasinoCashChanged?.Invoke(CasinoCash);
        }

        private void AddRecord(string description, int amount, TransactionScope scope)
        {
            var record = new TransactionRecord
            {
                description = description,
                amount = amount,
                scope = scope,
                bankBalanceAfter = BankCash,
                casinoBalanceAfter = CasinoCash,
                timestamp = DateTime.Now
            };

            history.Add(record);
            OnTransactionAdded?.Invoke(record);
        }
    }
}
