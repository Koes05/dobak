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
    Mom,         // 엄마    
    Unknown,     // 기타/알 수 없는 상대방
    Teacher,
    CafeManager,
    Bank,
    Site,
    Counselor,
    Joonho,
    Seoyeon

}

[System.Serializable]
public class Choice
{
    public string choiceText;      // 선택지 버튼 텍스트
    public int nextDialogueID;     // 이동할 다음 대화 ID (-1이면 종료)
    public int riskScoreChange;    // 위험도/예방 점수 변화량

    [Header("선택 시 앱 실행")]
    public bool openApp;

    public AppType targetApp;

    [Header("게임 흐름 선택")]
    public ChoiceAction action;
}

public class ChatChannel
{
    public SpeakerType speakerType;
    public string speakerName;
    public string lastMessage;     // 💡 [수정] 프로필창 표시용 마지막 대화 내용
    public int unreadCount;
    public List<string> receivedMessages = new List<string>();
    public int renderedReceivedCount;
    public List<Choice> eventChoices = new List<Choice>();
    public List<GameObject> spawnedBubbles = new List<GameObject>(); // 생성된 말풍선 오브젝트 저장 (화면 전환 시 복원용)
}

// ==========================================
// [대화 매니저 시스템]
// ==========================================
public class DialogueManager : MonoBehaviour
{
    [Header("UI Toggle Panel")]
    public GameObject dialoguePanel;

    [Header("대화 상대 표시")]
    [SerializeField] private TMP_Text speakerNameText;

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

    private Dictionary<SpeakerType, ChatChannel> channels = new Dictionary<SpeakerType, ChatChannel>();
    private SpeakerType currentSpeaker;
    private bool initialized;
    private readonly Dictionary<SpeakerType, ProfileSlot> profileSlotsBySpeaker = new Dictionary<SpeakerType, ProfileSlot>();
    private readonly Queue<ProfileSlot> availableProfileSlots = new Queue<ProfileSlot>();
    private readonly List<SpeakerType> contactOrder = new List<SpeakerType>();
    private ProfileSlot profileTemplate;
    private Vector2 contactStartPosition;
    private float contactSpacing = 120f;
    private RectTransform contactContent;
    private ScrollRect contactScroll;
    private Scrollbar contactScrollbar;

    private void Awake()
    {
        EnsureInitialized();
        if (dialoguePanel != null)
            dialoguePanel.SetActive(false);
    }

    private void Start()
    {
        EnsureInitialized();
        ConfigureChatWindowArt();
        ConfigureChatViewport();

        // 초기 프로필 UI 갱신
        UpdateAllProfileUI();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        InitializeChannels();
        PrepareProfileSlots();
        initialized = true;
    }

    private void InitializeChannels()
    {
        channels[SpeakerType.Friend] = CreateChannel(SpeakerType.Friend, "동창친구");
        channels[SpeakerType.Mom] = CreateChannel(SpeakerType.Mom, "엄마");
    }

    private static ChatChannel CreateChannel(SpeakerType speaker, string speakerName)
    {
        return new ChatChannel
        {
            speakerType = speaker,
            speakerName = speakerName,
            lastMessage = "",
            unreadCount = 0
        };
    }

    private void PrepareProfileSlots()
    {
        foreach (ProfileSlot discovered in FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (discovered.gameObject.scene.IsValid() && !profileSlots.Contains(discovered))
                profileSlots.Add(discovered);
        }

        profileSlots.RemoveAll(slot => slot == null);
        profileSlots.Sort((left, right) =>
            right.GetComponent<RectTransform>().anchoredPosition.y.CompareTo(left.GetComponent<RectTransform>().anchoredPosition.y));

        if (profileSlots.Count == 0)
            return;

        profileTemplate = profileSlots[0];
        if (profileSlots.Count > 1)
        {
            float measuredSpacing = Mathf.Abs(profileSlots[0].GetComponent<RectTransform>().anchoredPosition.y -
                                               profileSlots[1].GetComponent<RectTransform>().anchoredPosition.y);
            if (measuredSpacing > 1f)
                contactSpacing = measuredSpacing;
        }

        ConfigureContactScroll();

        contactOrder.Clear();
        contactOrder.Add(SpeakerType.Friend);
        contactOrder.Add(SpeakerType.Mom);
        for (int i = 0; i < profileSlots.Count; i++)
        {
            ProfileSlot slot = profileSlots[i];
            if (i == 0)
            {
                RegisterProfileSlot(slot, SpeakerType.Friend, "동창친구", true);
            }
            else if (i == 1)
            {
                RegisterProfileSlot(slot, SpeakerType.Mom, "엄마", true);
            }
            else
            {
                slot.gameObject.SetActive(false);
                availableProfileSlots.Enqueue(slot);
            }
        }

        ReflowProfileSlots();
    }

    private void RegisterProfileSlot(ProfileSlot slot, SpeakerType speaker, string speakerName, bool visible)
    {
        slot.Configure(speaker, speakerName, () => OpenDialogue(speaker));
        slot.SetLastMessage(channels.TryGetValue(speaker, out ChatChannel channel) ? channel.lastMessage : "");
        slot.UpdateUnreadBadge(channels.TryGetValue(speaker, out channel) ? channel.unreadCount : 0);
        slot.gameObject.SetActive(visible);
        profileSlotsBySpeaker[speaker] = slot;
    }

    private ProfileSlot EnsureProfileSlot(SpeakerType speaker, string speakerName)
    {
        if (profileSlotsBySpeaker.TryGetValue(speaker, out ProfileSlot existing))
            return existing;

        ProfileSlot slot;
        if (availableProfileSlots.Count > 0)
        {
            slot = availableProfileSlots.Dequeue();
        }
        else if (profileTemplate != null)
        {
            slot = Instantiate(profileTemplate, profileTemplate.transform.parent);
            RectTransform templateRect = profileTemplate.GetComponent<RectTransform>();
            RectTransform slotRect = slot.GetComponent<RectTransform>();
            slotRect.anchoredPosition = templateRect.anchoredPosition + Vector2.down *
                ((templateRect.rect.height + 20f) * profileSlots.Count);
            profileSlots.Add(slot);
        }
        else
        {
            return null;
        }

        RegisterProfileSlot(slot, speaker, speakerName, true);
        contactOrder.Add(speaker);
        slot.transform.SetAsLastSibling();
        ReflowProfileSlots();
        return slot;
    }

    private void MoveContactToTop(SpeakerType speaker)
    {
        if (!contactOrder.Remove(speaker))
            return;

        contactOrder.Insert(0, speaker);
        ReflowProfileSlots();
        if (contactScroll != null)
            contactScroll.verticalNormalizedPosition = 1f;
    }

    private void ReflowProfileSlots()
    {
        bool keepAtTop = contactScroll == null || contactContent == null ||
                         contactContent.rect.height <= contactScroll.viewport.rect.height + 1f ||
                         contactScroll.verticalNormalizedPosition >= 0.95f;

        for (int i = 0; i < contactOrder.Count; i++)
        {
            if (!profileSlotsBySpeaker.TryGetValue(contactOrder[i], out ProfileSlot slot))
                continue;

            RectTransform rect = slot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(contactStartPosition.x, contactStartPosition.y - contactSpacing * i);
            slot.transform.SetSiblingIndex(i);
        }

        if (contactContent != null)
        {
            float requiredHeight = Mathf.Max(contactScroll.viewport.rect.height + 1f, 30f + contactSpacing * contactOrder.Count);
            contactContent.sizeDelta = new Vector2(0f, requiredHeight);
            LayoutRebuilder.ForceRebuildLayoutImmediate(contactContent);
            if (keepAtTop)
            {
                contactContent.anchoredPosition = Vector2.zero;
                contactScroll.verticalNormalizedPosition = 1f;
            }
        }
    }

    private void ConfigureContactScroll()
    {
        if (profileTemplate == null || contactScroll != null)
            return;

        Transform messageRoot = profileTemplate.transform.parent;
        GameObject viewportObject = new GameObject("Contact Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
        viewportObject.layer = profileTemplate.gameObject.layer;
        viewportObject.transform.SetParent(messageRoot, false);

        RectTransform viewport = viewportObject.GetComponent<RectTransform>();
        viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 0.5f);
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.anchoredPosition = new Vector2(-630f, -65f);
        viewport.sizeDelta = new Vector2(560f, 650f);

        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(1f, 1f, 1f, 0.001f);

        GameObject contentObject = new GameObject("Contact Content", typeof(RectTransform));
        contentObject.layer = profileTemplate.gameObject.layer;
        contentObject.transform.SetParent(viewport, false);
        contactContent = contentObject.GetComponent<RectTransform>();
        contactContent.anchorMin = new Vector2(0f, 1f);
        contactContent.anchorMax = new Vector2(1f, 1f);
        contactContent.pivot = new Vector2(0.5f, 1f);
        contactContent.anchoredPosition = Vector2.zero;
        contactContent.sizeDelta = new Vector2(-24f, 650f);

        foreach (ProfileSlot slot in profileSlots)
            slot.transform.SetParent(contactContent, false);

        contactStartPosition = new Vector2(0f, -60f);
        contactScroll = viewportObject.GetComponent<ScrollRect>();
        contactScroll.content = contactContent;
        contactScroll.viewport = viewport;
        contactScroll.horizontal = false;
        contactScroll.vertical = true;
        contactScroll.movementType = ScrollRect.MovementType.Clamped;
        contactScroll.scrollSensitivity = 45f;
        contactScroll.inertia = true;
        contactScroll.decelerationRate = 0.12f;
        contactScrollbar = CreateContactScrollbar(viewport);
        contactScroll.verticalScrollbar = contactScrollbar;
        contactScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        contactScroll.verticalNormalizedPosition = 1f;
    }

    private Scrollbar CreateContactScrollbar(RectTransform viewport)
    {
        GameObject trackObject = new GameObject("Contact Scrollbar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Scrollbar));
        trackObject.layer = profileTemplate.gameObject.layer;
        trackObject.transform.SetParent(viewport, false);
        RectTransform track = trackObject.GetComponent<RectTransform>();
        track.anchorMin = new Vector2(1f, 0f);
        track.anchorMax = new Vector2(1f, 1f);
        track.pivot = new Vector2(1f, 0.5f);
        track.anchoredPosition = new Vector2(-4f, 0f);
        track.sizeDelta = new Vector2(5f, -20f);
        trackObject.GetComponent<Image>().color = new Color(0.72f, 0.74f, 0.79f, 0.38f);

        GameObject slidingObject = new GameObject("Sliding Area", typeof(RectTransform));
        slidingObject.layer = trackObject.layer;
        slidingObject.transform.SetParent(track, false);
        RectTransform sliding = slidingObject.GetComponent<RectTransform>();
        sliding.anchorMin = Vector2.zero;
        sliding.anchorMax = Vector2.one;
        sliding.offsetMin = new Vector2(0f, 5f);
        sliding.offsetMax = new Vector2(0f, -5f);

        GameObject handleObject = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        handleObject.layer = trackObject.layer;
        handleObject.transform.SetParent(sliding, false);
        RectTransform handle = handleObject.GetComponent<RectTransform>();
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.offsetMin = Vector2.zero;
        handle.offsetMax = Vector2.zero;
        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(0.23f, 0.46f, 0.82f, 0.8f);

        Scrollbar scrollbar = trackObject.GetComponent<Scrollbar>();
        scrollbar.targetGraphic = handleImage;
        scrollbar.handleRect = handle;
        scrollbar.direction = Scrollbar.Direction.TopToBottom;
        scrollbar.size = 0.25f;
        return scrollbar;
    }

    private void ConfigureChatViewport()
    {
        if (scrollRect == null || scrollRect.viewport == null)
            return;

        RectTransform viewport = scrollRect.viewport;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.anchoredPosition = Vector2.zero;

        float safeBottom = 245f;
        if (choiceButtonContainer is RectTransform choiceRect)
            safeBottom = Mathf.Max(safeBottom, choiceRect.anchoredPosition.y + choiceRect.rect.height + 36f);

        viewport.offsetMin = new Vector2(0f, safeBottom);
        viewport.offsetMax = new Vector2(-24f, -145f);

        if (scrollRect.verticalScrollbar != null)
        {
            RectTransform scrollbarRect = scrollRect.verticalScrollbar.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-10f, safeBottom);
            scrollbarRect.offsetMax = new Vector2(-4f, -145f);
        }

        Mask legacyMask = viewport.GetComponent<Mask>();
        if (legacyMask != null)
            legacyMask.enabled = false;
        if (viewport.GetComponent<RectMask2D>() == null)
            viewport.gameObject.AddComponent<RectMask2D>();

        if (chatContent.TryGetComponent(out VerticalLayoutGroup layout))
            layout.padding.bottom = Mathf.Max(layout.padding.bottom, 24);
    }

    private void ConfigureChatWindowArt()
    {
        if (scrollRect == null || scrollRect.transform.Find("Chat Window Middle") != null)
            return;

        Texture2D texture = Resources.Load<Texture2D>("Message/chat_window");
        if (texture == null)
        {
            Debug.LogWarning("메시지 창 이미지를 찾을 수 없습니다: Resources/Message/chat_window");
            return;
        }

        Transform root = scrollRect.transform;
        if (root.TryGetComponent(out Image oldBackground))
            oldBackground.color = new Color(1f, 1f, 1f, 0.001f);

        CreateChatWindowSlice("Chat Window Footer", root, texture,
            new Rect(0f, 0f, 1f, 0.16f), Vector2.zero, new Vector2(1f, 0f), 0f, 165f);
        CreateChatWindowSlice("Chat Window Middle", root, texture,
            new Rect(0f, 0.16f, 1f, 0.72f), new Vector2(0f, 0f), Vector2.one, 165f, -145f);
        CreateChatWindowSlice("Chat Window Header", root, texture,
            new Rect(0f, 0.875f, 1f, 0.125f), new Vector2(0f, 1f), Vector2.one, -145f, 0f);

        if (scrollRect.viewport != null && scrollRect.viewport.TryGetComponent(out Image viewportImage))
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
    }

    private static void CreateChatWindowSlice(string objectName, Transform parent, Texture texture,
        Rect uvRect, Vector2 anchorMin, Vector2 anchorMax, float bottomOffset, float topOffset)
    {
        GameObject slice = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        slice.layer = parent.gameObject.layer;
        slice.transform.SetParent(parent, false);
        slice.transform.SetAsFirstSibling();

        RectTransform rect = slice.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(0f, bottomOffset);
        rect.offsetMax = new Vector2(0f, topOffset);

        RawImage image = slice.GetComponent<RawImage>();
        image.texture = texture;
        image.uvRect = uvRect;
        image.color = Color.white;
        image.raycastTarget = false;
    }

    // -------------------------------------------------------------
    // [채팅창 열기/닫기]
    // -------------------------------------------------------------
    public void OpenDialogue(SpeakerType speaker)
    {
        EnsureInitialized();
        ConfigureChatViewport();
        currentSpeaker = speaker;
        if (dialoguePanel != null) dialoguePanel.SetActive(true);

        bool isNewChannel = !channels.ContainsKey(speaker);

        if (isNewChannel)
        {
            channels[speaker] = CreateChannel(speaker, speaker.ToString());
        }

        // 대화 상대 이름 표시
        if (channels.ContainsKey(speaker))
        {
            speakerNameText.text = channels[speaker].speakerName;
        }

        // 💡 [신규] 해당 채팅방을 열었으므로 안 읽은 메시지를 0으로 초기화
        channels[speaker].unreadCount = 0;

        RefreshChatWindow();

        ChatChannel currentChannel = channels[speaker];

        RenderReceivedMessages(currentChannel);

        if (currentChannel.spawnedBubbles.Count == 0)
        {
            Debug.Log("아직 받은 메시지가 없습니다.");
            return;
        }

        if (currentChannel.eventChoices.Count > 0)
        {
            CreateChoiceButtons(currentChannel.eventChoices);
        }

        StartCoroutine(ScrollToBottom());

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
                lastMessage = message,
                unreadCount = 1
            };
        }
        else
        {
            // 마지막 메시지 갱신
            channels[speaker].lastMessage = message;

            // 현재 열려있는 방이 아니라면 안 읽은 메시지 수 증가
            if (!dialoguePanel.activeSelf || currentSpeaker != speaker)
            {
                // 안 읽은 메시지 수 증가
                channels[speaker].unreadCount++;
            }
        }
        // 프로필 UI 갱신
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

    private void RenderReceivedMessages(ChatChannel channel)
    {
        while (channel.renderedReceivedCount < channel.receivedMessages.Count)
        {
            CreateBubble(otherBubblePrefab, channel.receivedMessages[channel.renderedReceivedCount]);
            channel.renderedReceivedCount++;
        }
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

        if (GameFlowManager.Instance != null)
        {
            switch (selectedChoice.action)
            {
                case ChoiceAction.AcceptGambling:
                    GameFlowManager.Instance.ResolveInvitation(true);
                    break;
                case ChoiceAction.DeclineGambling:
                    GameFlowManager.Instance.ResolveInvitation(false);
                    break;
                case ChoiceAction.RejectFirstInvitation:
                    GameFlowManager.Instance.StartInvitationRetempt();
                    return;
                case ChoiceAction.ContinueInvitation:
                    GameFlowManager.Instance.ContinueInvitation();
                    return;
                case ChoiceAction.RequestHelp:
                    GameFlowManager.Instance.RequestHelp();
                    break;
                case ChoiceAction.AcceptMomLoan:
                    GameFlowManager.Instance.ResolveMomLoan(true);
                    break;
                case ChoiceAction.DeclineMomLoan:
                    GameFlowManager.Instance.ResolveMomLoan(false);
                    break;
                case ChoiceAction.AcceptFriendLoan:
                    GameFlowManager.Instance.ResolveFriendLoan(true);
                    break;
                case ChoiceAction.DeclineFriendLoan:
                    GameFlowManager.Instance.ResolveFriendLoan(false);
                    break;
            }
        }

        // ==========================================
        // 선택지를 통해 특정 앱을 실행하는 경우
        // ==========================================

        if (selectedChoice.openApp)
        {
            Debug.Log($"선택지로 앱 실행 : {selectedChoice.targetApp}");

            // AppWindow를 찾아서 해당 앱 실행
            AppWindow appWindow = FindAnyObjectByType<AppWindow>();

            if (appWindow != null)
            {
                appWindow.OpenApp(selectedChoice.targetApp);          
            }
            else
            {
                Debug.LogError("AppWindow를 찾을 수 없습니다.");
            }
        }

        // ==========================================
        // 다음 대화가 있는 경우
        // ==========================================

        channels[currentSpeaker].eventChoices.Clear();
        EndDialogue();
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

        ProfileSlot slot = EnsureProfileSlot(speaker, channels[speaker].speakerName);
        if (slot == null)
            return;

        string contactLabel = channels[speaker].speakerName;
        slot.Configure(speaker, contactLabel, () => OpenDialogue(speaker));
        slot.SetLastMessage(lastMsg);
        slot.UpdateUnreadBadge(unread);
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
        if (tmp != null)
        {
            tmp.text = text;
            LayoutRebuilder.ForceRebuildLayoutImmediate(bubble.GetComponent<RectTransform>());
        } 

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

    private IEnumerator ScrollToBottom()
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
    }

    private void EndDialogue()
    {
        ClearChoices();
    }

    public void ReceiveNotificationMessage(SpeakerType speaker, string speakerName, string message)
    {
        EnsureInitialized();
        bool contactAlreadyExisted = profileSlotsBySpeaker.ContainsKey(speaker);
        // 해당 화자의 채널이 없으면 생성
        if (!channels.ContainsKey(speaker))
        {
            channels[speaker] = new ChatChannel
            {
                speakerType = speaker,
                speakerName = speakerName,
                unreadCount = 0
            };
        }

        channels[speaker].speakerName = GetContactName(speaker, speakerName);

        // 마지막 메시지 변경
        channels[speaker].lastMessage = message;
        channels[speaker].receivedMessages.Add(message);

        if (isActiveAndEnabled && dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == speaker)
        {
            RenderReceivedMessages(channels[speaker]);
            channels[speaker].unreadCount = 0;
        }
        else
        {
            channels[speaker].unreadCount++;
        }

        // 프로필 UI 즉시 갱신
        UpdateProfileUI(speaker);
        if (contactAlreadyExisted)
            MoveContactToTop(speaker);
    }

    public void SetEventChoices(SpeakerType speaker, List<Choice> choices)
    {
        EnsureInitialized();
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
            return;

        channel.eventChoices = choices ?? new List<Choice>();
        if (dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == speaker)
            CreateChoiceButtons(channel.eventChoices);
    }

    private static string GetContactName(SpeakerType speaker, string providedName)
    {
        return speaker switch
        {
            SpeakerType.Friend => string.IsNullOrWhiteSpace(providedName) ? "동창친구" : providedName,
            SpeakerType.Mom => "엄마",
            SpeakerType.Teacher => "학교",
            SpeakerType.CafeManager => "카페 매니저",
            SpeakerType.Bank => "은행 알림",
            SpeakerType.Site => "사이트 알림",
            SpeakerType.Counselor => "상담 선생님",
            _ => string.IsNullOrWhiteSpace(providedName) ? speaker.ToString() : providedName
        };
    }
}

public enum ChoiceAction
{
    None,
    AcceptGambling,
    DeclineGambling,
    RejectFirstInvitation,
    ContinueInvitation,
    RequestHelp,
    AcceptMomLoan,
    DeclineMomLoan,
    AcceptFriendLoan,
    DeclineFriendLoan
}
