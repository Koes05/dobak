using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ==========================================
// [대화 데이터 구조]
// ==========================================
public enum SpeakerType
{
    Friend,      // 동창 친구
    Stranger,    // 모르는 사람 / SNS 접근자
    Scammer,     // 전문 사기꾼 / 리딩방 업자
    Family       // 가족
}

[System.Serializable]
public class Choice
{
    public string choiceText;      // 선택지 버튼 텍스트
    public int nextDialogueID;     // 이동할 다음 대화 ID (-1이면 종료)
    public int riskScoreChange;    // 위험도/예방 점수 변화량
}

[System.Serializable]
public class DialogueNode
{
    public int id;                 // 노드 고유 ID
    public SpeakerType speakerType; // 상대방 타입
    public string speakerName;     // 상대방 이름
    public string message;         // 상대방 메시지
    public List<Choice> choices;   // 선택지 목록 (1개~N개 동적 처리)
}

public class ChatChannel
{
    public SpeakerType speakerType;
    public string speakerName;
    public int currentDialogueID;  // 현재 진행 중인 대화 노드 ID
    public string lastMessage;     // 💡 [수정] 프로필창 표시용 마지막 대화 내용
    public int unreadCount;
    public List<GameObject> spawnedBubbles = new List<GameObject>(); // 생성된 말풍선 오브젝트 저장 (화면 전환 시 복원용)
}

// ==========================================
// [대화 매니저 시스템]
// ==========================================
public class DialogueManager : MonoBehaviour
{
    [Header("UI Toggle Panel")]
    public GameObject dialoguePanel;

    [Header("Scroll & Container Settings")]
    public ScrollRect scrollRect;
    public Transform chatContent;
    public Transform choiceButtonContainer;

    [Header("Profile Slots List")]
    public List<ProfileSlot> profileSlots = new List<ProfileSlot>();

    [Header("Prefabs")]
    public GameObject otherBubblePrefab;
    public GameObject myBubblePrefab;
    public GameObject choiceButtonPrefab;

    private Dictionary<SpeakerType, Dictionary<int, DialogueNode>> allDialogues 
        = new Dictionary<SpeakerType, Dictionary<int, DialogueNode>>();

    private Dictionary<SpeakerType, ChatChannel> channels = new Dictionary<SpeakerType, ChatChannel>();
    private SpeakerType currentSpeaker;

    private void Start()
    {
        if (dialoguePanel != null) dialoguePanel.SetActive(false);
        LoadPrototypeData();
        
        // 초기 프로필 상태 세팅
        UpdateAllProfileUI();
    }

    // -------------------------------------------------------------
    // [채팅창 열기/닫기]
    // -------------------------------------------------------------
    public void OpenDialogue(SpeakerType speaker)
    {
        currentSpeaker = speaker;
        if (dialoguePanel != null) dialoguePanel.SetActive(true);

        bool isNewChannel = !channels.ContainsKey(speaker);

        if (isNewChannel)
        {
            channels[speaker] = new ChatChannel
            {
                speakerType = speaker,
                currentDialogueID = 1,
                lastMessage = "",
                unreadCount = 0
            };
        }

        // 💡 [신규] 해당 채팅방을 열었으므로 안 읽은 메시지를 0으로 초기화
        channels[speaker].unreadCount = 0;

        RefreshChatWindow();

        ChatChannel currentChannel = channels[speaker];

        if (isNewChannel || currentChannel.spawnedBubbles.Count == 0)
        {
            DisplayNode(currentChannel.currentDialogueID);
        }
        else
        {
            if (currentChannel.currentDialogueID >= 0 && 
                allDialogues.ContainsKey(speaker) && 
                allDialogues[speaker].ContainsKey(currentChannel.currentDialogueID))
            {
                DialogueNode currentNode = allDialogues[speaker][currentChannel.currentDialogueID];
                CreateChoiceButtons(currentNode.choices);
            }

            StartCoroutine(ScrollToBottom());
        }

        // 채팅방 진입 시 해당 프로필의 배지 갱신 (0이 되었으므로 숨겨짐)
        UpdateProfileUI(speaker);
    }

    public void OpenDialogueByInt(int speakerIndex)
    {
        OpenDialogue((SpeakerType)speakerIndex);
    }

    public void CloseDialogue()
    {
        if (dialoguePanel != null) dialoguePanel.SetActive(false);
    }

    // -------------------------------------------------------------
    // [외부 메시지 수신 연동] 
    // 채팅창이 닫혀있을 때 누군가에게 선톡/신규 메시지가 오는 경우 사용
    // -------------------------------------------------------------
    public void ReceiveExternalMessage(SpeakerType speaker, string message)
    {
        if (!channels.ContainsKey(speaker))
        {
            channels[speaker] = new ChatChannel
            {
                speakerType = speaker,
                currentDialogueID = 1,
                lastMessage = message,
                unreadCount = 1
            };
        }
        else
        {
            channels[speaker].lastMessage = message;

            // 현재 열려있는 방이 아니라면 안 읽은 메시지 수 증가
            if (!dialoguePanel.activeSelf || currentSpeaker != speaker)
            {
                channels[speaker].unreadCount++;
            }
        }

        UpdateProfileUI(speaker);
    }

    // -------------------------------------------------------------
    // [대화 출력 & 선택 로직]
    // -------------------------------------------------------------
    private void RefreshChatWindow()
    {
        foreach (var ch in channels.Values)
        {
            foreach (var bubble in ch.spawnedBubbles)
            {
                bubble.SetActive(false);
            }
        }

        if (channels.ContainsKey(currentSpeaker))
        {
            foreach (var bubble in channels[currentSpeaker].spawnedBubbles)
            {
                bubble.SetActive(true);
            }
        }

        ClearChoices();
    }

    private void DisplayNode(int nodeID)
    {
        if (!allDialogues.ContainsKey(currentSpeaker) || !allDialogues[currentSpeaker].ContainsKey(nodeID))
        {
            EndDialogue();
            return;
        }

        DialogueNode currentNode = allDialogues[currentSpeaker][nodeID];
        channels[currentSpeaker].currentDialogueID = nodeID;

        string formattedMsg = $"{currentNode.speakerName}\n{currentNode.message}";
        CreateBubble(otherBubblePrefab, formattedMsg);
        
        SetLastMessage(currentNode.message);
        CreateChoiceButtons(currentNode.choices);

        StartCoroutine(ScrollToBottom());
    }

    private void CreateChoiceButtons(List<Choice> choices)
    {
        ClearChoices();

        if (choices != null && choices.Count > 0)
        {
            foreach (var choice in choices)
            {
                GameObject btnObj = Instantiate(choiceButtonPrefab, choiceButtonContainer);
                btnObj.GetComponentInChildren<TextMeshProUGUI>().text = choice.choiceText;

                Choice targetChoice = choice;
                btnObj.GetComponent<Button>().onClick.AddListener(() => OnSelectChoice(targetChoice));
            }
        }
    }

    private void OnSelectChoice(Choice selectedChoice)
    {
        CreateBubble(myBubblePrefab, selectedChoice.choiceText);
        SetLastMessage(selectedChoice.choiceText);

        if (selectedChoice.riskScoreChange != 0)
        {
            Debug.Log($"[{currentSpeaker}] 위험도 변화: {selectedChoice.riskScoreChange}");
        }

        ClearChoices();

        if (selectedChoice.nextDialogueID >= 0)
        {
            StartCoroutine(DelayedNextDialogue(selectedChoice.nextDialogueID, 0.5f));
        }
        else
        {
            channels[currentSpeaker].currentDialogueID = -1;
            EndDialogue();
        }
    }

    // -------------------------------------------------------------
    // [유틸리티 및 프로필 연동]
    // -------------------------------------------------------------
    private void SetLastMessage(string msg)
    {
        if (channels.ContainsKey(currentSpeaker))
        {
            channels[currentSpeaker].lastMessage = msg;
            UpdateProfileUI(currentSpeaker);
        }
    }

    // 💡 특정 스피커의 프로필 UI만 갱신
    public void UpdateProfileUI(SpeakerType speaker)
    {
        if (!channels.ContainsKey(speaker)) return;

        string lastMsg = channels[speaker].lastMessage;
        int unread = channels[speaker].unreadCount;

        foreach (var slot in profileSlots)
        {
            if (slot != null && slot.speakerType == speaker)
            {
                slot.SetLastMessage(lastMsg);
                slot.UpdateUnreadBadge(unread); // 💡 안 읽은 메시지 배지 반영
            }
        }
    }

    // 모든 프로필 UI 일괄 갱신
    public void UpdateAllProfileUI()
    {
        foreach (var speaker in channels.Keys)
        {
            UpdateProfileUI(speaker);
        }
    }

    private void CreateBubble(GameObject prefab, string text)
    {
        GameObject bubble = Instantiate(prefab, chatContent);
        TextMeshProUGUI tmp = bubble.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null) tmp.text = text;

        if (channels.ContainsKey(currentSpeaker))
        {
            channels[currentSpeaker].spawnedBubbles.Add(bubble);
        }

        StartCoroutine(ScrollToBottom());
    }

    private void ClearChoices()
    {
        foreach (Transform child in choiceButtonContainer)
        {
            Destroy(child.gameObject);
        }
    }

    private IEnumerator DelayedNextDialogue(int nextID, float delay)
    {
        yield return new WaitForSeconds(delay);
        DisplayNode(nextID);
    }

    private IEnumerator ScrollToBottom()
    {
        yield return new WaitForEndOfFrame();
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
    }

    private void EndDialogue()
    {
        ClearChoices();
    }

    private void LoadPrototypeData()
    {
        allDialogues.Clear();

        var friendNodes = new Dictionary<int, DialogueNode>
        {
            { 1, new DialogueNode { id = 1, speakerType = SpeakerType.Friend, speakerName = "동창 친구", message = "야 오랜만이다! 대박 사이트 찾음 10분만에 3배 가자!", choices = new List<Choice> { new Choice { choiceText = "링크 줘봐", nextDialogueID = 2, riskScoreChange = 10 }, new Choice { choiceText = "안 해", nextDialogueID = -1, riskScoreChange = -10 } } }},
            { 2, new DialogueNode { id = 2, speakerType = SpeakerType.Friend, speakerName = "동창 친구", message = "여기 링크 보낸다! [링크]", choices = new List<Choice> { new Choice { choiceText = "확인했어", nextDialogueID = -1, riskScoreChange = 0 } } }}
        };

        var strangerNodes = new Dictionary<int, DialogueNode>
        {
            { 1, new DialogueNode { id = 1, speakerType = SpeakerType.Stranger, speakerName = "익명 소모임", message = "안녕하세요! 재테크 관심 있으신가요? 100% 수익 보장합니다.", choices = new List<Choice> { new Choice { choiceText = "관심 있어요", nextDialogueID = -1, riskScoreChange = 15 }, new Choice { choiceText = "차단합니다", nextDialogueID = -1, riskScoreChange = -15 } } }}
        };

        var scammerNodes = new Dictionary<int, DialogueNode>
        {
            { 1, new DialogueNode { id = 1, speakerType = SpeakerType.Scammer, speakerName = "김실장 (리딩방)", message = "회원님, 오늘 무료 추천주 보유중인데 입장하시겠습니까?", choices = new List<Choice> { new Choice { choiceText = "입장할게요", nextDialogueID = -1, riskScoreChange = 20 }, new Choice { choiceText = "신고할게요", nextDialogueID = -1, riskScoreChange = -20 } } }}
        };

        allDialogues[SpeakerType.Friend] = friendNodes;
        allDialogues[SpeakerType.Stranger] = strangerNodes;
        allDialogues[SpeakerType.Scammer] = scammerNodes;
    }
}