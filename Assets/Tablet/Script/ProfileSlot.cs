using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System;

public class ProfileSlot : MonoBehaviour
{
    [Header("Profile Info")]
    public SpeakerType speakerType;            // 해당 프로필의 타입
    public TextMeshProUGUI lastMessageText;    // 마지막 대화를 출력할 UI 텍스트

    [Header("Unread Badge Settings")]
    public GameObject unreadBadgeObject;       // 빨간색 원 알림 배지 (오브젝트 전체)
    public TextMeshProUGUI unreadCountText;    // 알림 숫자 텍스트 (선택 사항)

    [Header("Text Limitation Settings")]
    public int maxCharacterLimit = 20;        // 최대 글자 수 제한 (기본값 20자)

    private TMP_Text contactNameText;
    private Button openButton;

    public void Configure(SpeakerType type, string contactName, Action onOpen)
    {
        speakerType = type;
        ResolveReferences();

        if (contactNameText != null)
            contactNameText.text = contactName;

        if (openButton != null)
        {
            openButton.onClick.RemoveAllListeners();
            openButton.onClick.AddListener(() => onOpen?.Invoke());
        }
    }

    private void ResolveReferences()
    {
        openButton ??= GetComponent<Button>();
        if (contactNameText != null)
            return;

        foreach (TMP_Text candidate in GetComponentsInChildren<TMP_Text>(true))
        {
            if (candidate != lastMessageText && candidate != unreadCountText)
            {
                contactNameText = candidate;
                break;
            }
        }
    }

    // 프로필의 마지막 메시지를 업데이트하는 함수
    public void SetLastMessage(string rawText)
    {
        if (lastMessageText == null) return;

        if (string.IsNullOrEmpty(rawText))
        {
            lastMessageText.text = "";
            return;
        }

        // 1. 줄바꿈 제거 (\n -> 공백 처리)
        string cleanText = rawText.Replace("\n", " ");

        // 2. 글자 수 제한 처리 (최대 글자 수 초과 시 말줄임표 '...' 추가)
        if (cleanText.Length > maxCharacterLimit)
        {
            cleanText = cleanText.Substring(0, maxCharacterLimit) + "...";
        }

        lastMessageText.text = cleanText;
    }

    // 읽지않음 표시기
    public void UpdateUnreadBadge(int unreadCount)
    {
        if (unreadBadgeObject == null) return;

        if (unreadCount > 0)
        {
            unreadBadgeObject.SetActive(true);

            // 숫자 텍스트가 지정되어 있다면 개수 표시 (99개 이상은 99+ 처리)
            if (unreadCountText != null)
            {
                unreadCountText.text = unreadCount > 99 ? "99+" : unreadCount.ToString();
            }
        }
        else
        {
            // 안 읽은 메시지가 0개면 빨간 배지 숨김
            unreadBadgeObject.SetActive(false);
        }
    }
}
