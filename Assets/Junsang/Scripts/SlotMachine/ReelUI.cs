using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Dobak.App.Casino.SlotMachine
{
    // 릴(세로줄) 하나를 담당.
    // Hierarchy 예시:
    // Reel (빈 오브젝트, 이 스크립트 부착)
    //  ├─ Top    (Image)
    //  ├─ Middle (Image)  <- 페이라인 판정에 사용되는 심볼
    //  └─ Bottom (Image)
    public class ReelUI : MonoBehaviour
    {
        [Header("표시용 이미지 3칸 (위/중간/아래)")]
        [SerializeField] private Image topSlot;
        [SerializeField] private Image middleSlot;
        [SerializeField] private Image bottomSlot;

        [Header("연출 설정")]
        [Tooltip("스핀 도중 심볼이 바뀌는 간격(초)")]
        [SerializeField] private float flickerInterval = 0.06f;

        public SlotSymbol CurrentMiddleSymbol { get; private set; }

        private SymbolDatabase database;
        private Coroutine spinRoutine;

        public void Init(SymbolDatabase db)
        {
            database = db;
        }

        // duration 동안 심볼을 빠르게 바꾸다가 finalSymbol로 정지
        public void Spin(float duration, SlotSymbol finalSymbol, System.Action onComplete = null)
        {
            if (spinRoutine != null) StopCoroutine(spinRoutine);
            spinRoutine = StartCoroutine(SpinRoutine(duration, finalSymbol, onComplete));
        }

        private IEnumerator SpinRoutine(float duration, SlotSymbol finalSymbol, System.Action onComplete)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                // 세 칸 모두 랜덤 심볼로 빠르게 교체 (시각 효과, 확률 무관)
                SetSlot(topSlot, database.GetVisualRandomSymbol());
                SetSlot(middleSlot, database.GetVisualRandomSymbol());
                SetSlot(bottomSlot, database.GetVisualRandomSymbol());

                yield return new WaitForSeconds(flickerInterval);
                elapsed += flickerInterval;
            }

            // 정지: 가운데 줄에 최종 심볼 확정, 위/아래는 시각적 변화용 랜덤
            SetSlot(topSlot, database.GetVisualRandomSymbol());
            SetSlot(middleSlot, finalSymbol);
            SetSlot(bottomSlot, database.GetVisualRandomSymbol());

            CurrentMiddleSymbol = finalSymbol;
            onComplete?.Invoke();
        }

        private void SetSlot(Image target, SlotSymbol symbol)
        {
            if (target == null || symbol == null) return;
            target.sprite = symbol.sprite;
            target.preserveAspect = true;
        }
    }
}
