using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dobak.App.Casino.SlotMachine
{
    // 심볼 하나에 대한 데이터: 이미지, 등장 확률 가중치, 3개 일치 시 배당 배수
    [Serializable]
    public class SlotSymbol
    {
        public string symbolName;
        public Sprite sprite;

        [Tooltip("숫자가 클수록 자주 등장함 (절대 확률 아님, 상대 가중치)")]
        public int weight = 10;

        [Tooltip("이 심볼 3개가 가운데 줄에 나란히 나오면 배팅액에 곱해지는 배당 배수")]
        public float payoutMultiplier = 2f;
    }

    // 슬롯머신 전체 심볼 세트를 관리하는 ScriptableObject
    // Project 창에서 우클릭 > Create > SlotMachine > Symbol Database 로 생성해서
    // Inspector에서 심볼들을 등록하면 됩니다.
    [CreateAssetMenu(fileName = "SymbolDatabase", menuName = "SlotMachine/Symbol Database")]
    public class SymbolDatabase : ScriptableObject
    {
        public List<SlotSymbol> symbols = new List<SlotSymbol>();

        private int totalWeight = -1;

        private void OnEnable()
        {
            RecalculateTotalWeight();
        }

        public void RecalculateTotalWeight()
        {
            totalWeight = 0;
            foreach (var s in symbols)
                totalWeight += Mathf.Max(0, s.weight);
        }

        // 가중치 기반으로 심볼 1개를 랜덤 추첨
        public SlotSymbol GetRandomWeightedSymbol()
        {
            if (totalWeight <= 0) RecalculateTotalWeight();
            if (symbols.Count == 0)
            {
                Debug.LogError("SymbolDatabase에 등록된 심볼이 없습니다.");
                return null;
            }

            int roll = UnityEngine.Random.Range(0, totalWeight);
            int cumulative = 0;

            foreach (var s in symbols)
            {
                cumulative += Mathf.Max(0, s.weight);
                if (roll < cumulative)
                    return s;
            }

            return symbols[symbols.Count - 1]; // 안전장치
        }

        // 완전 랜덤(가중치 무시) - 스핀 도중 '플리커' 연출용
        public SlotSymbol GetVisualRandomSymbol()
        {
            if (symbols.Count == 0) return null;
            return symbols[UnityEngine.Random.Range(0, symbols.Count)];
        }
    }
}