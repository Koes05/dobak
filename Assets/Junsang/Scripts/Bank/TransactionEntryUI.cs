using UnityEngine;
using TMPro;
using Dobak.Manager;
using UnityEngine.UI;

namespace Dobak.App.Bank
{
    // Transaction History의 한 줄 (예: "Use 1$ in Casino"). 프리팹으로 만들어서 BankUI에 연결.
    public class TransactionEntryUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text descriptionText;
        private TMP_Text amountText;

        public void Set(TransactionRecord record)
        {
            string sign = record.amount >= 0 ? "+" : "";
            string gameTime = record.gameDay > 0
                ? $"{record.gameDay:00}일차  {record.gameHour:00}:00"
                : $"{record.timestamp:MM.dd HH:mm}";
            descriptionText.text = $"<size=20><color=#758199>{gameTime}</color></size>\n" +
                $"<b>{record.description}</b>";
            descriptionText.fontSize = 28f;
            descriptionText.fontStyle |= FontStyles.Bold;
            descriptionText.alignment = TextAlignmentOptions.MidlineLeft;
            descriptionText.color = new Color(0.06f, 0.1f, 0.18f);
            descriptionText.textWrappingMode = TextWrappingModes.Normal;
            RectTransform leftRect = descriptionText.rectTransform;
            leftRect.anchorMin = new Vector2(0.035f, 0.12f);
            leftRect.anchorMax = new Vector2(0.60f, 0.88f);
            leftRect.offsetMin = leftRect.offsetMax = Vector2.zero;

            amountText = EnsureAmountText();
            string amountColor = record.amount >= 0 ? "#00A889" : "#111827";
            amountText.text = $"<color={amountColor}><b>{sign}{record.amount:N0}원</b></color>\n" +
                $"<size=21><color=#758199>잔액 {record.bankBalanceAfter:N0}원</color></size>";

            Image background = GetComponent<Image>();
            if (background == null)
                background = gameObject.AddComponent<Image>();
            background.sprite = Resources.Load<Sprite>("BankUI/transaction_row");
            background.color = Color.white;
            Outline outline = GetComponent<Outline>();
            if (outline == null)
                outline = gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.68f, 0.78f, 0.92f, 0.9f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);
            LayoutElement layout = GetComponent<LayoutElement>();
            if (layout == null)
                layout = gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = 118f;
            layout.minHeight = 118f;
        }

        private TMP_Text EnsureAmountText()
        {
            if (amountText != null)
                return amountText;

            Transform existing = transform.Find("Amount And Balance");
            if (existing != null)
                amountText = existing.GetComponent<TMP_Text>();
            if (amountText == null)
            {
                GameObject go = new GameObject("Amount And Balance", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                amountText = go.GetComponent<TMP_Text>();
            }

            amountText.font = descriptionText.font;
            amountText.fontSize = 28f;
            amountText.fontStyle |= FontStyles.Bold;
            amountText.alignment = TextAlignmentOptions.MidlineRight;
            amountText.textWrappingMode = TextWrappingModes.NoWrap;
            amountText.raycastTarget = false;
            RectTransform rect = amountText.rectTransform;
            rect.anchorMin = new Vector2(0.62f, 0.12f);
            rect.anchorMax = new Vector2(0.88f, 0.88f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return amountText;
        }
    }
}
