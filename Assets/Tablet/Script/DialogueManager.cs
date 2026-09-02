using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public string replyText;       // 메시지 앱에 실제로 표시할 내 답장
    public int nextDialogueID;     // 이동할 다음 대화 ID (-1이면 종료)
    public int riskScoreChange;    // 위험도/예방 점수 변화량

    [Header("선택 시 앱 실행")]
    public bool openApp;

    public AppType targetApp;

    [Header("게임 흐름 선택")]
    public ChoiceAction action;

    public string scenarioAction;
}

public class ChatMessageEntry
{
    public bool isPlayer;
    public string text;
}

public class ChatChannel
{
    public SpeakerType speakerType;
    public string speakerName;
    public string lastMessage;     // 💡 [수정] 프로필창 표시용 마지막 대화 내용
    public int unreadCount;
    public List<string> receivedMessages = new List<string>();
    public int renderedReceivedCount;
    // 앱을 닫거나 하루가 바뀌어도 메시지 내역은 유지한다.
    // spawnedBubbles가 어떤 이유로 사라져도 이 기록으로 다시 그릴 수 있다.
    public List<ChatMessageEntry> messageHistory = new List<ChatMessageEntry>();
    public List<Choice> eventChoices = new List<Choice>();
    public Queue<List<Choice>> pendingChoiceSets = new Queue<List<Choice>>();
    public List<GameObject> spawnedBubbles = new List<GameObject>(); // 생성된 말풍선 오브젝트 저장 (화면 전환 시 복원용)
    public GameObject typingBubble;
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
    private SpeakerType mostRecentSpeaker = SpeakerType.Friend;
    private SpeakerType preferredSpeaker = SpeakerType.Unknown;
    private bool initialized;
    private readonly Dictionary<SpeakerType, ProfileSlot> profileSlotsBySpeaker = new Dictionary<SpeakerType, ProfileSlot>();
    private readonly Queue<ProfileSlot> availableProfileSlots = new Queue<ProfileSlot>();
    private readonly List<SpeakerType> contactOrder = new List<SpeakerType>();
    private readonly HashSet<string> submittedScenarioActions = new HashSet<string>();
    private ProfileSlot profileTemplate;
    private Vector2 contactStartPosition;
    private float contactSpacing = 120f;
    private RectTransform contactContent;
    private ScrollRect contactScroll;
    private Scrollbar contactScrollbar;

    public int TotalUnreadCount => channels.Values.Sum(channel => channel.unreadCount);
    public bool IsDialogueOpen => dialoguePanel != null && dialoguePanel.activeInHierarchy;
    public SpeakerType CurrentSpeaker => currentSpeaker;

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
        HideLegacyMessageLabels();

        // 초기 프로필 UI 갱신
        UpdateAllProfileUI();
    }

    private void HideLegacyMessageLabels()
    {
        // 구형 씬에 고정 배치된 슬롯은 V3에서 사용하지 않는다. 텍스트만 숨기지 말고 행 전체를 비활성화한다.
        foreach (string legacyName in new[] { "Stranger_Profile", "Scammer_Profile" })
        {
            GameObject legacy = GameObject.Find(legacyName);
            if (legacy == null)
            {
                Transform found = Resources.FindObjectsOfTypeAll<Transform>()
                    .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid() && candidate.name == legacyName);
                legacy = found != null ? found.gameObject : null;
            }
            if (legacy != null)
                legacy.SetActive(false);
        }

        Transform root = dialoguePanel != null && dialoguePanel.transform.parent != null
            ? dialoguePanel.transform.parent
            : transform;
        foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (label == null || label == speakerNameText)
                continue;
            string value = (label.text ?? string.Empty).Trim();
            if (value == "채팅")
            {
                // 좌측에는 이미 '채팅+' 로고 이미지가 있으므로 씬에 남은 예전 텍스트 제목은 숨긴다.
                label.gameObject.SetActive(false);
                continue;
            }
            if (value == "Stranger" || value == "Scammer")
            {
                ProfileSlot slot = label.GetComponentInParent<ProfileSlot>(true);
                if (slot != null)
                    slot.gameObject.SetActive(false);
                else
                    label.gameObject.SetActive(false);
            }
        }
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
        channels[SpeakerType.Friend] = CreateChannel(SpeakerType.Friend, "민재");
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
        foreach (ProfileSlot discovered in FindObjectsByType<ProfileSlot>(FindObjectsInactive.Include))
        {
            if (discovered.gameObject.scene.IsValid() && !profileSlots.Contains(discovered))
                profileSlots.Add(discovered);
        }

        profileSlots.RemoveAll(slot => slot == null);
        profileSlots.Sort((left, right) =>
            right.GetComponent<RectTransform>().anchoredPosition.y.CompareTo(left.GetComponent<RectTransform>().anchoredPosition.y));

        if (profileSlots.Count == 0)
            return;

        ProfileSlot friendSlot = profileSlots.FirstOrDefault(slot =>
            slot.speakerType == SpeakerType.Friend || slot.gameObject.name.Contains("Friend_Profile"));
        ProfileSlot momSlot = profileSlots.FirstOrDefault(slot =>
            slot.speakerType == SpeakerType.Mom || slot.gameObject.name.Contains("Mom_Profile"));
        profileTemplate = friendSlot ?? profileSlots.FirstOrDefault(slot => !IsSuppressedLegacySlot(slot)) ?? profileSlots[0];

        if (profileSlots.Count > 1)
        {
            float measuredSpacing = Mathf.Abs(profileSlots[0].GetComponent<RectTransform>().anchoredPosition.y -
                                               profileSlots[1].GetComponent<RectTransform>().anchoredPosition.y);
            if (measuredSpacing > 1f)
                contactSpacing = measuredSpacing;
        }

        ConfigureContactScroll();
        profileSlotsBySpeaker.Clear();
        availableProfileSlots.Clear();
        contactOrder.Clear();

        foreach (ProfileSlot slot in profileSlots)
        {
            if (slot == null)
                continue;

            // Stranger/Scammer는 씬에 존재해도 삭제하거나 다른 연락처로 재활용하지 않는다.
            // 생성되는 즉시 해당 행만 숨긴다.
            if (IsSuppressedLegacySlot(slot))
            {
                slot.gameObject.SetActive(false);
                continue;
            }

            if (slot == friendSlot)
            {
                RegisterProfileSlot(slot, SpeakerType.Friend, "민재", true);
                contactOrder.Add(SpeakerType.Friend);
            }
            else if (slot == momSlot)
            {
                RegisterProfileSlot(slot, SpeakerType.Mom, "엄마", true);
                contactOrder.Add(SpeakerType.Mom);
            }
            else
            {
                slot.gameObject.SetActive(false);
                availableProfileSlots.Enqueue(slot);
            }
        }

        // 씬에 Friend/Mom 슬롯이 없던 경우에만 안전하게 동적 슬롯을 만든다.
        if (!profileSlotsBySpeaker.ContainsKey(SpeakerType.Friend))
        {
            channels[SpeakerType.Friend].speakerName = "민재";
            EnsureProfileSlot(SpeakerType.Friend, "민재");
        }
        if (!profileSlotsBySpeaker.ContainsKey(SpeakerType.Mom))
            EnsureProfileSlot(SpeakerType.Mom, "엄마");

        ReflowProfileSlots();
        HideLegacyMessageLabels();
    }

    private static bool IsSuppressedLegacySlot(ProfileSlot slot)
    {
        if (slot == null)
            return false;
        string name = slot.gameObject.name ?? string.Empty;
        return slot.speakerType == SpeakerType.Stranger ||
               slot.speakerType == SpeakerType.Scammer ||
               name.IndexOf("Stranger", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Scammer", StringComparison.OrdinalIgnoreCase) >= 0;
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
        if (speaker == SpeakerType.Stranger || speaker == SpeakerType.Scammer)
        {
            if (profileSlotsBySpeaker.TryGetValue(speaker, out ProfileSlot suppressed))
                suppressed.gameObject.SetActive(false);
            return null;
        }
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
        if (speaker == SpeakerType.Stranger || speaker == SpeakerType.Scammer)
            return;
        contactOrder.Remove(speaker);
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

        float safeBottom = 250f;
        if (choiceButtonContainer is RectTransform choiceRect)
            safeBottom = Mathf.Max(safeBottom, choiceRect.anchoredPosition.y + choiceRect.rect.height + 36f);

        viewport.offsetMin = new Vector2(0f, safeBottom);
        viewport.offsetMax = new Vector2(-24f, -175f);

        if (scrollRect.verticalScrollbar != null)
        {
            RectTransform scrollbarRect = scrollRect.verticalScrollbar.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-10f, safeBottom);
            scrollbarRect.offsetMax = new Vector2(-4f, -175f);
        }

        Mask legacyMask = viewport.GetComponent<Mask>();
        if (legacyMask != null)
            legacyMask.enabled = false;
        RectMask2D chatMask = viewport.GetComponent<RectMask2D>();
        if (chatMask == null)
            chatMask = viewport.gameObject.AddComponent<RectMask2D>();
        chatMask.enabled = true;
        chatMask.padding = Vector4.zero;

        if (chatContent.TryGetComponent(out VerticalLayoutGroup layout))
        {
            layout.padding.top = Mathf.Max(layout.padding.top, 32);
            layout.padding.bottom = Mathf.Max(layout.padding.bottom, 32);
        }
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
        if (dialoguePanel != null && !dialoguePanel.activeSelf)
            dialoguePanel.SetActive(true);

        if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            return;

        EnsureInitialized();
        ConfigureChatViewport();
        currentSpeaker = speaker;

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
        FindAnyObjectByType<NotificationManager>(FindObjectsInactive.Include)
            ?.ClearMessageNotifications(speaker);

        ChatChannel currentChannel = channels[speaker];
        RebuildBubblesFromHistory(currentChannel);
        RefreshChatWindow();
        RenderReceivedMessages(currentChannel);

        if (currentChannel.eventChoices.Count > 0)
        {
            CreateChoiceButtons(currentChannel.eventChoices);
        }

        StartCoroutine(ScrollToBottom());

        // 채팅방 진입 시 해당 프로필의 배지 갱신 (0이 되었으므로 숨겨짐)
        UpdateProfileUI(speaker);
        GameFlowManager.Instance?.V3NotifyConversationOpened(speaker);
    }

    public void EnsureConversation(SpeakerType speaker, string speakerName)
    {
        EnsureInitialized();
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
        {
            channel = CreateChannel(speaker, speakerName);
            channels[speaker] = channel;
        }
        channel.speakerName = GetContactName(speaker, speakerName);
        EnsureProfileSlot(speaker, channel.speakerName);
        UpdateProfileUI(speaker);
    }

    public bool IsConversationOpen(SpeakerType speaker)
    {
        return dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == speaker;
    }

    public void PreferConversation(SpeakerType speaker)
    {
        preferredSpeaker = speaker;
        if (channels.TryGetValue(speaker, out ChatChannel channel))
        {
            EnsureProfileSlot(speaker, channel.speakerName);
            MoveContactToTop(speaker);
        }
    }

    public void OpenMostRecentConversation()
    {
        EnsureInitialized();
        SpeakerType target = preferredSpeaker != SpeakerType.Unknown
            ? preferredSpeaker
            : mostRecentSpeaker;
        preferredSpeaker = SpeakerType.Unknown;
        OpenDialogue(target);
    }

    public void OpenDialogueByInt(int speakerIndex)
    {
        OpenDialogue((SpeakerType)speakerIndex);
    }

    public void CloseDialogue()
    {
        if (channels.TryGetValue(currentSpeaker, out ChatChannel channel) && channel.typingBubble != null)
        {
            Destroy(channel.typingBubble);
            channel.typingBubble = null;
        }
        if (dialoguePanel != null)
            dialoguePanel.SetActive(false);
        GameFlowManager.Instance?.V3NotifyConversationClosed(currentSpeaker);
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
        // 레거시 경로로 들어온 메시지도 채팅 내역의 원본 데이터에 남긴다.
        RecordMessageHistory(speaker, false, message);
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
            ch.spawnedBubbles.RemoveAll(bubble => bubble == null);
            foreach (var bubble in ch.spawnedBubbles)
                bubble.SetActive(false);
        }

        if (channels.ContainsKey(currentSpeaker))
        {
            foreach (var bubble in channels[currentSpeaker].spawnedBubbles)
                if (bubble != null)
                    bubble.SetActive(true);
        }

        ClearChoices();
    }

    private void RebuildBubblesFromHistory(ChatChannel channel)
    {
        if (channel == null)
            return;

        // 메시지 오브젝트는 앱 UI가 재구성될 때 일부만 사라질 수 있다.
        // 화면에 남아 있는 오브젝트를 기준으로 복구 여부를 판단하지 않고,
        // 채널의 messageHistory를 항상 원본으로 삼아 현재 채팅방을 다시 만든다.
        if (channel.typingBubble != null)
        {
            Destroy(channel.typingBubble);
            channel.typingBubble = null;
        }
        foreach (GameObject bubble in channel.spawnedBubbles)
        {
            if (bubble != null)
                Destroy(bubble);
        }
        channel.spawnedBubbles.Clear();

        bool visible = channel.speakerType == currentSpeaker;
        foreach (ChatMessageEntry entry in channel.messageHistory)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.text))
                continue;
            CreateBubbleForChannel(entry.isPlayer ? myBubblePrefab : otherBubblePrefab,
                entry.text, channel.speakerType, visible);
        }

        // messageHistory에 수신 메시지까지 포함되어 있으므로 별도 수신 목록을 재출력하지 않는다.
        channel.renderedReceivedCount = channel.receivedMessages.Count;
    }

    private void RecordMessageHistory(SpeakerType speaker, bool isPlayer, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !channels.TryGetValue(speaker, out ChatChannel channel))
            return;
        channel.messageHistory.Add(new ChatMessageEntry
        {
            isPlayer = isPlayer,
            text = text
        });
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

            StartCoroutine(ScrollToBottom());
        }
    }

    private void OnSelectChoice(Choice selectedChoice)
    {
        bool repeatableScenarioAction = !string.IsNullOrWhiteSpace(selectedChoice.scenarioAction) &&
            selectedChoice.scenarioAction.StartsWith("v3-borrow-", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectedChoice.scenarioAction) && !repeatableScenarioAction &&
            !submittedScenarioActions.Add(selectedChoice.scenarioAction))
            return;

        bool silentChoice = !string.IsNullOrWhiteSpace(selectedChoice.scenarioAction) &&
            selectedChoice.scenarioAction.StartsWith("v3-borrow-cancel:", StringComparison.OrdinalIgnoreCase);
        string sentText = string.IsNullOrWhiteSpace(selectedChoice.replyText)
            ? selectedChoice.choiceText
            : selectedChoice.replyText;
        if (!silentChoice)
        {
            RecordMessageHistory(currentSpeaker, true, sentText);
            CreateBubble(myBubblePrefab, sentText);
            SetLastMessage(sentText);
        }

        if (selectedChoice.riskScoreChange != 0)
        {
            Debug.Log($"[{currentSpeaker}] 위험도 변화: {selectedChoice.riskScoreChange}");
        }

        ClearChoices();
        if (channels.TryGetValue(currentSpeaker, out ChatChannel selectedChannel))
            selectedChannel.eventChoices.Clear();

        StartCoroutine(CompleteChoiceAfterReply(selectedChoice, currentSpeaker));
    }

    private IEnumerator CompleteChoiceAfterReply(Choice selectedChoice, SpeakerType selectedSpeaker)
    {
        yield return StartCoroutine(ScrollToBottom());
        yield return new WaitForSecondsRealtime(0.3f);

        currentSpeaker = selectedSpeaker;

        if (GameFlowManager.Instance != null)
        {
            if (!string.IsNullOrWhiteSpace(selectedChoice.scenarioAction))
                GameFlowManager.Instance.ExecuteScenarioAction(selectedChoice.scenarioAction);

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
                    yield break;
                case ChoiceAction.ContinueInvitation:
                    GameFlowManager.Instance.ContinueInvitation();
                    yield break;
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

        PromoteNextChoiceSet(currentSpeaker);
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
        CreateBubbleForChannel(prefab, text, currentSpeaker, true);
    }

    private void CreateBubbleForChannel(GameObject prefab, string text, SpeakerType speaker, bool visible)
    {
        GameObject bubble = Instantiate(prefab, chatContent);
        TextMeshProUGUI tmp = bubble.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
        {
            tmp.text = text;
            LayoutRebuilder.ForceRebuildLayoutImmediate(bubble.GetComponent<RectTransform>());
        } 

        if (channels.ContainsKey(speaker))
        {
            channels[speaker].spawnedBubbles.Add(bubble);
        }

        bubble.SetActive(visible);

        if (visible && isActiveAndEnabled && gameObject.activeInHierarchy)
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

        if (scrollRect == null)
            yield break;

        Canvas.ForceUpdateCanvases();
        if (chatContent is RectTransform contentRect)
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
        if (scrollRect.viewport != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.viewport);
        Canvas.ForceUpdateCanvases();

        scrollRect.StopMovement();
        scrollRect.velocity = Vector2.zero;
        scrollRect.verticalNormalizedPosition = 0f;

        // ContentSizeFitter can settle one frame after a new bubble and choices appear.
        yield return null;
        Canvas.ForceUpdateCanvases();
        if (chatContent is RectTransform finalContentRect)
            LayoutRebuilder.ForceRebuildLayoutImmediate(finalContentRect);
        scrollRect.StopMovement();
        scrollRect.velocity = Vector2.zero;
        scrollRect.verticalNormalizedPosition = 0f;
    }

    private void EndDialogue()
    {
        ClearChoices();
    }

    public void ShowTypingIndicator(SpeakerType speaker, string speakerName)
    {
        EnsureConversation(speaker, speakerName);
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
            return;

        HideTypingIndicator(speaker);
        if (!IsConversationOpen(speaker))
            return;

        GameObject bubble = Instantiate(otherBubblePrefab, chatContent);
        TMP_Text tmp = bubble.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
            tmp.text = "입력중...";
        channel.typingBubble = bubble;
        StartCoroutine(ScrollToBottom());
    }

    public void HideTypingIndicator(SpeakerType speaker)
    {
        if (!channels.TryGetValue(speaker, out ChatChannel channel) || channel.typingBubble == null)
            return;
        Destroy(channel.typingBubble);
        channel.typingBubble = null;
    }

    public void ReceiveNotificationMessage(SpeakerType speaker, string speakerName, string message)
    {
        EnsureInitialized();
        if (!channels.ContainsKey(speaker))
        {
            channels[speaker] = new ChatChannel
            {
                speakerType = speaker,
                speakerName = speakerName,
                unreadCount = 0
            };
        }

        ChatChannel channel = channels[speaker];
        channel.speakerName = GetContactName(speaker, speakerName);
        channel.lastMessage = message;
        channel.receivedMessages.Add(message);
        RecordMessageHistory(speaker, false, message);
        mostRecentSpeaker = speaker;

        bool conversationOpen = IsConversationOpen(speaker);
        bool convertedTypingBubble = conversationOpen && channel.typingBubble != null;
        if (convertedTypingBubble)
        {
            TMP_Text typingText = channel.typingBubble.GetComponentInChildren<TMP_Text>(true);
            if (typingText != null)
                typingText.text = message;
            channel.spawnedBubbles.Add(channel.typingBubble);
            channel.typingBubble = null;
            channel.renderedReceivedCount = channel.receivedMessages.Count;
            channel.unreadCount = 0;
            StartCoroutine(ScrollToBottom());
        }
        else
        {
            if (channel.typingBubble != null)
            {
                Destroy(channel.typingBubble);
                channel.typingBubble = null;
            }
            if (conversationOpen)
            {
                RenderReceivedMessages(channel);
                channel.unreadCount = 0;
            }
            else
            {
                channel.unreadCount++;
            }
        }

        UpdateProfileUI(speaker);
        MoveContactToTop(speaker);
    }

    public void ReceivePlayerMessage(SpeakerType recipient, string recipientName, string message)
    {
        EnsureInitialized();
        if (!channels.ContainsKey(recipient))
            channels[recipient] = CreateChannel(recipient, recipientName);

        ChatChannel channel = channels[recipient];
        channel.speakerName = GetContactName(recipient, recipientName);
        channel.lastMessage = message;
        RecordMessageHistory(recipient, true, message);
        mostRecentSpeaker = recipient;
        bool visible = dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == recipient;
        CreateBubbleForChannel(myBubblePrefab, message, recipient, visible);
        UpdateProfileUI(recipient);
        MoveContactToTop(recipient);
    }

    public void ResetScenarioConversations()
    {
        EnsureInitialized();
        ClearChoices();
        foreach (ChatChannel channel in channels.Values)
        {
            foreach (GameObject bubble in channel.spawnedBubbles)
            {
                if (bubble != null)
                    Destroy(bubble);
            }
            if (channel.typingBubble != null)
                Destroy(channel.typingBubble);
            channel.typingBubble = null;
            channel.spawnedBubbles.Clear();
            channel.receivedMessages.Clear();
            channel.messageHistory.Clear();
            channel.renderedReceivedCount = 0;
            channel.eventChoices.Clear();
            channel.pendingChoiceSets.Clear();
            channel.unreadCount = 0;
            channel.lastMessage = string.Empty;
        }
        submittedScenarioActions.Clear();
        if (dialoguePanel != null)
            dialoguePanel.SetActive(false);
        UpdateAllProfileUI();
    }

    public void EnsureContact(SpeakerType speaker, string speakerName)
    {
        EnsureInitialized();
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
        {
            channels[speaker] = CreateChannel(speaker, GetContactName(speaker, speakerName));
            channel = channels[speaker];
        }
        else if (!string.IsNullOrWhiteSpace(speakerName))
        {
            channel.speakerName = GetContactName(speaker, speakerName);
        }

        EnsureProfileSlot(speaker, channel.speakerName);
        UpdateProfileUI(speaker);
    }

    public void SetEventChoices(SpeakerType speaker, List<Choice> choices)
    {
        EnsureInitialized();
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
        {
            EnsureContact(speaker, speaker.ToString());
            if (!channels.TryGetValue(speaker, out channel))
                return;
        }

        List<Choice> incoming = choices ?? new List<Choice>();
        if (incoming.Count == 0)
            return;

        if (SameChoiceSet(channel.eventChoices, incoming) ||
            channel.pendingChoiceSets.Any(queued => SameChoiceSet(queued, incoming)))
            return;

        if (channel.eventChoices.Count == 0)
            channel.eventChoices = incoming;
        else
            channel.pendingChoiceSets.Enqueue(incoming);

        PreferConversation(speaker);

        if (dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == speaker &&
            channel.pendingChoiceSets.Count == 0)
            CreateChoiceButtons(channel.eventChoices);
    }

    private static bool SameChoiceSet(List<Choice> left, List<Choice> right)
    {
        if (left == null || right == null || left.Count != right.Count || left.Count == 0)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index]?.scenarioAction != right[index]?.scenarioAction ||
                left[index]?.choiceText != right[index]?.choiceText)
                return false;
        }
        return true;
    }

    public void DismissEventChoices(SpeakerType speaker)
    {
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
            return;
        channel.eventChoices.Clear();
        channel.pendingChoiceSets.Clear();
        if (currentSpeaker == speaker)
            ClearChoices();
    }

    private void PromoteNextChoiceSet(SpeakerType speaker)
    {
        if (!channels.TryGetValue(speaker, out ChatChannel channel))
            return;

        if (channel.eventChoices.Count == 0 && channel.pendingChoiceSets.Count > 0)
            channel.eventChoices = channel.pendingChoiceSets.Dequeue();

        if (dialoguePanel != null && dialoguePanel.activeInHierarchy && currentSpeaker == speaker &&
            channel.eventChoices.Count > 0)
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
