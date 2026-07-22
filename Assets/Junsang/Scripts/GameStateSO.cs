using UnityEngine;

[CreateAssetMenu(menuName = "SO/Game/State/Game State", fileName = "New GameStateSO")]
public class GameStateSO : ScriptableObject
{
    [SerializeField] private int startBalance = 1000;
    [SerializeField] private int startDebt = 5000;
    [SerializeField] private float interestRate = 0.05f;

    public int Balance { get; private set; }
    public int Debt { get; private set; }
    public int CurrentDay { get; private set; }

    // 이벤트 채널 참조 (Inspector에서 연결)
    public IntEventChannelSO onBalanceChanged;
    public IntEventChannelSO onDebtChanged;

    // 씬 시작할 때마다 초기화 (Play 모드에서만, 애셋 자체는 영구 데이터 아님)
    public void Initialize()
    {
        Balance = startBalance;
        Debt = startDebt;
        CurrentDay = 1;
    }

    public void PayDebt(int amount)
    {
        if (amount > Balance) return;
        Balance -= amount;
        Debt -= amount;
        onBalanceChanged?.Raise(Balance);
        onDebtChanged?.Raise(Debt);
    }

    public void AddBalance(int amount)
    {
        Balance += amount;
        onBalanceChanged?.Raise(Balance);
    }

    public void NextDay()
    {
        Debt = Mathf.RoundToInt(Debt * (1 + interestRate));
        CurrentDay++;
        onDebtChanged?.Raise(Debt);
    }
}
