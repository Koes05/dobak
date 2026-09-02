using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public sealed class StudyActivityQuestion
{
    public int day;
    public string activityTitle;
    public string progressLabel;
    public string activityText;
    public string question;
    public string[] choices;
    public int answerIndex;
    public string correctText;
    public string wrongText;
}

public class QuizManager : MonoBehaviour
{
    public event Action<int, int> DailyQuizCompleted;

    [SerializeField] private TMP_Text questionText;
    [SerializeField] private Button[] answerButtons;
    [SerializeField] private TMP_Text resultText;
    [SerializeField] private Image resultBox;
    [SerializeField] private QuizData[] quizzes;
    [SerializeField] private float nextQuestionDelay = 1.0f;
    [SerializeField] private Button resetButton;
    [SerializeField] private TMP_Text progressText;

    private readonly Dictionary<int, List<StudyActivityQuestion>> questionsByDay = new();
    private List<StudyActivityQuestion> currentQuestions = new();
    private int currentIndex;
    private int correctAnswerCount;
    private int configuredDay = -1;
    private bool isWeekday = true;
    private bool listenersBound;
    private bool isAnswerLocked;
    private bool isDailyQuizFinished;
    private bool completionReported;
    private GameObject answerHeaderObject;
    private GameObject answerPanelObject;
    private AudioSource feedbackAudioSource;
    private AudioClip correctClip;
    private bool runtimeInitialized;
    private readonly List<Sprite> runtimeSprites = new();

    public bool HasActivityForCurrentDay => currentQuestions.Count > 0;
    public string CurrentActivityTitle => HasActivityForCurrentDay
        ? currentQuestions[0].activityTitle
        : "오늘의 일정";

    private void Awake()
    {
        InitializeRuntime();
    }

    private void Start()
    {
        InitializeRuntime();
        ConfigureForDay(configuredDay < 0 ? 1 : configuredDay, isWeekday);
        if (HasActivityForCurrentDay)
            BeginActivityView();
    }

    private void InitializeRuntime()
    {
        if (runtimeInitialized)
            return;
        runtimeInitialized = true;
        feedbackAudioSource = gameObject.AddComponent<AudioSource>();
        feedbackAudioSource.playOnAwake = false;
        feedbackAudioSource.volume = 0.45f;
        correctClip = Resources.Load<AudioClip>("Audio/SFX/quiz_correct");
        ResolveSceneLabels();
        BindButtonListeners();
        LoadActivities();
        if (resetButton != null)
            resetButton.onClick.AddListener(ResetDailyQuiz);
    }

    private void ResolveSceneLabels()
    {
        ApplyCustomStudyVisuals();

        if (questionText != null)
        {
            questionText.enableAutoSizing = true;
            questionText.fontSizeMin = 28f;
            questionText.fontSizeMax = 40f;
            questionText.fontStyle |= FontStyles.Bold;
            questionText.textWrappingMode = TextWrappingModes.Normal;
            questionText.overflowMode = TextOverflowModes.Overflow;
            questionText.margin = new Vector4(42f, 24f, 42f, 24f);

            RectTransform questionPanel = questionText.transform.parent.GetComponent<RectTransform>();
            if (questionPanel != null)
            {
                questionPanel.anchoredPosition = Vector2.zero;
                questionPanel.sizeDelta = new Vector2(1600f, 900f);
                Image panelImage = questionPanel.GetComponent<Image>();
                if (panelImage != null)
                    panelImage.color = new Color(1f, 1f, 1f, 0.94f);
            }
        }

        foreach (Button answerButton in answerButtons)
        {
            TMP_Text label = GetAnswerLabel(answerButton);
            if (label == null)
                continue;

            // 폰트 크기는 씬에 설정된 Answer_Text 값을 그대로 사용한다.
            // 런타임에서 크기를 바꾸지 않고 RectTransform만 버튼 내부에 맞춘다.
            label.enableAutoSizing = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.MidlineRight;
            ConfigureAnswerTextRect(label);
        }

        foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true))
        {
            if (label.gameObject.name == "Q_Text")
                label.gameObject.SetActive(false);
            else if (label.gameObject.name == "Question_Text" && label != questionText)
            {
                answerHeaderObject = label.gameObject;
                answerPanelObject = label.transform.parent.gameObject;
            }
        }

        RectTransform answerPanel = answerPanelObject != null
            ? answerPanelObject.GetComponent<RectTransform>()
            : null;
        if (answerPanel != null)
        {
            answerPanel.anchoredPosition = Vector2.zero;
            answerPanel.sizeDelta = new Vector2(1600f, 900f);
            Image answerPanelImage = answerPanel.GetComponent<Image>();
            if (answerPanelImage != null)
                answerPanelImage.enabled = false;
        }

        if (answerHeaderObject != null)
            answerHeaderObject.SetActive(false);

        // 씬에 남아 있는 구형 선택지 번호(1/2/3/4)와 런타임 Choice Number를 모두 숨긴다.
        foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true))
        {
            if (label == null || answerButtons.Any(button => button != null && label.transform.IsChildOf(button.transform)))
                continue;
            string value = (label.text ?? string.Empty).Trim();
            if (value == "1" || value == "2" || value == "3" || value == "4")
                label.gameObject.SetActive(false);
        }

        float[] choiceY = { -45f, -217f, -389f, -561f };
        for (int i = 0; i < answerButtons.Length; i++)
        {
            RectTransform choiceRect = answerButtons[i] != null
                ? answerButtons[i].GetComponent<RectTransform>()
                : null;
            if (choiceRect == null)
                continue;

            choiceRect.anchorMin = choiceRect.anchorMax = new Vector2(0.5f, 0.5f);
            choiceRect.pivot = new Vector2(0.5f, 0.5f);
            choiceRect.sizeDelta = new Vector2(1100f, 150f);
            choiceRect.anchoredPosition = new Vector2(440f, choiceY[Mathf.Min(i, choiceY.Length - 1)]);
            TMP_Text label = GetAnswerLabel(answerButtons[i]);
            if (label != null)
            {
                ConfigureAnswerTextRect(label);
                label.alignment = TextAlignmentOptions.MidlineRight;
                label.color = new Color(0.08f, 0.1f, 0.14f, 1f);
            }

            Image choiceImage = answerButtons[i].targetGraphic as Image;
            if (choiceImage != null)
            {
                Sprite choiceSprite = LoadRuntimeSprite(
                    "StudyUI/choice_panel",
                    Vector4.zero,
                    true);
                if (choiceSprite != null)
                {
                    choiceImage.sprite = choiceSprite;
                    choiceImage.type = Image.Type.Simple;
                    choiceImage.preserveAspect = true;
                }
                choiceImage.color = Color.white;
            }

            Transform choiceNumber = answerButtons[i].transform.Find("Choice Number");
            if (choiceNumber != null)
                choiceNumber.gameObject.SetActive(false);
        }

        if (progressText != null)
        {
            RectTransform progressRect = progressText.rectTransform;
            progressRect.anchorMin = progressRect.anchorMax = new Vector2(0f, 1f);
            progressRect.pivot = new Vector2(0f, 1f);
            progressRect.anchoredPosition = new Vector2(48f, -34f);
            progressRect.sizeDelta = new Vector2(620f, 64f);
            progressText.alignment = TextAlignmentOptions.MidlineLeft;
            progressText.fontStyle |= FontStyles.Bold;
        }

        if (resultBox != null)
        {
            RectTransform resultRect = resultBox.rectTransform;
            resultRect.anchoredPosition = new Vector2(0f, 10f);
            resultRect.sizeDelta = new Vector2(1500f, 92f);
        }
        if (resultText != null)
        {
            resultText.rectTransform.anchorMin = Vector2.zero;
            resultText.rectTransform.anchorMax = Vector2.one;
            resultText.rectTransform.anchoredPosition = Vector2.zero;
            resultText.rectTransform.sizeDelta = new Vector2(-60f, -20f);
            resultText.textWrappingMode = TextWrappingModes.Normal;
            resultText.alignment = TextAlignmentOptions.Center;
        }

        SetQuestionLayout(false);
    }

    private void ApplyCustomStudyVisuals()
    {
        Image background = GetComponent<Image>();
        Sprite backgroundSprite = LoadRuntimeSprite("StudyUI/study_background", Vector4.zero, false);
        if (background != null && backgroundSprite != null)
        {
            background.sprite = backgroundSprite;
            background.type = Image.Type.Simple;
            background.preserveAspect = false;
            background.color = Color.white;
        }

        Image questionPanel = questionText != null
            ? questionText.transform.parent.GetComponent<Image>()
            : null;
        Sprite questionSprite = LoadRuntimeSprite(
            "StudyUI/question_panel",
            new Vector4(84f, 84f, 84f, 84f),
            false);
        if (questionPanel != null && questionSprite != null)
        {
            questionPanel.sprite = questionSprite;
            questionPanel.type = Image.Type.Simple;
            questionPanel.preserveAspect = true;
            questionPanel.color = Color.white;
        }
    }

    private Sprite LoadRuntimeSprite(string resourcePath, Vector4 border, bool cropChoicePanel)
    {
        Texture2D texture = Resources.Load<Texture2D>(resourcePath);
        if (texture == null)
        {
            Debug.LogWarning($"Study UI image not found: {resourcePath}");
            return null;
        }

        Rect rect = new Rect(0f, 0f, texture.width, texture.height);
        if (cropChoicePanel)
        {
            float cropLeft = texture.width * 0.027f;
            float cropRight = texture.width * 0.027f;
            float cropBottom = texture.height * 0.25f;
            float cropTop = texture.height * 0.25f;
            rect = new Rect(cropLeft, cropBottom,
                texture.width - cropLeft - cropRight,
                texture.height - cropBottom - cropTop);
            float scale = rect.height / texture.height;
            border.y *= scale;
            border.w *= scale;
        }

        Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, border);
        sprite.name = resourcePath.Replace('/', '_');
        runtimeSprites.Add(sprite);
        return sprite;
    }

    private static TMP_Text GetAnswerLabel(Button button)
    {
        if (button == null)
            return null;

        TMP_Text answerText = button.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(label => label != null && label.gameObject.name == "Answer_Text");
        if (answerText != null)
            return answerText;

        return button.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(label => label != null && label.gameObject.name != "Choice Number");
    }

    private static void ConfigureAnswerTextRect(TMP_Text label)
    {
        if (label == null)
            return;

        RectTransform rect = label.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        // Inspector 기준: Left 150 / Right 150 / Top 0 / Bottom 0.
        rect.offsetMin = new Vector2(150f, 0f);
        rect.offsetMax = new Vector2(-150f, 0f);
        rect.anchoredPosition = Vector2.zero;
        label.margin = Vector4.zero;
    }

    private static void CreateChoiceNumber(Button button, int number)
    {
        if (button == null || button.transform.Find("Choice Number") != null)
            return;

        TMP_Text source = GetAnswerLabel(button);
        GameObject numberObject = new GameObject(
            "Choice Number",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        numberObject.layer = button.gameObject.layer;
        numberObject.transform.SetParent(button.transform, false);

        TextMeshProUGUI numberText = numberObject.GetComponent<TextMeshProUGUI>();
        numberText.font = source != null ? source.font : null;
        numberText.fontSize = 34f;
        numberText.fontStyle = FontStyles.Bold;
        numberText.alignment = TextAlignmentOptions.Center;
        numberText.color = Color.white;
        numberText.raycastTarget = false;
        numberText.text = number.ToString(CultureInfo.InvariantCulture);

        RectTransform rect = numberText.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(126f, 0f);
        rect.sizeDelta = new Vector2(74f, 64f);
    }

    private void OnDestroy()
    {
        foreach (Sprite sprite in runtimeSprites)
            if (sprite != null)
                Destroy(sprite);
        runtimeSprites.Clear();
    }

    private void BindButtonListeners()
    {
        if (listenersBound)
            return;
        listenersBound = true;
        for (int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;
            answerButtons[i].onClick.AddListener(() => CheckAnswer(index));
        }
    }

    public void ConfigureForDay(int day, bool weekday)
    {
        InitializeRuntime();
        CancelInvoke();
        configuredDay = day;
        isWeekday = weekday;
        currentQuestions = questionsByDay.TryGetValue(day, out List<StudyActivityQuestion> rows)
            ? new List<StudyActivityQuestion>(rows)
            : new List<StudyActivityQuestion>();
        ResetProgress();
        if (!isWeekday)
            ShowUnavailable("주말에는 조별과제 일정이 없습니다.\n오늘은 알바 일정을 확인해 보자.", "주말");
        else if (currentQuestions.Count == 0)
            ShowUnavailable("오늘 진행할 숙제나 조별과제 일정은 없습니다.", "일정 없음");
        else
            ShowActivityPrelude();
    }

    public void BeginActivityView()
    {
        InitializeRuntime();
        if (!HasActivityForCurrentDay || isDailyQuizFinished)
            return;
        CancelInvoke();
        ShowActivityPrelude();
        Invoke(nameof(LoadQuestion), 0.9f);
    }

    private void ResetProgress()
    {
        currentIndex = 0;
        correctAnswerCount = 0;
        isAnswerLocked = false;
        isDailyQuizFinished = false;
        completionReported = false;
    }

    private void ShowActivityPrelude()
    {
        SetQuestionLayout(false);
        StudyActivityQuestion item = currentQuestions[Mathf.Clamp(currentIndex, 0, currentQuestions.Count - 1)];
        questionText.text = item.activityText;
        if (progressText != null)
            progressText.text = item.activityTitle;
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        SetAnswerArea(false);
    }

    private void LoadQuestion()
    {
        if (currentIndex >= currentQuestions.Count)
        {
            FinishDailyQuiz();
            return;
        }

        StudyActivityQuestion item = currentQuestions[currentIndex];
        SetQuestionLayout(true);
        isAnswerLocked = false;
        questionText.text = item.question;
        if (progressText != null)
            progressText.text = $"{item.progressLabel}  {currentIndex + 1} / {currentQuestions.Count}";
        SetAnswerArea(true);
        for (int i = 0; i < answerButtons.Length; i++)
        {
            bool visible = i < item.choices.Length;
            answerButtons[i].gameObject.SetActive(visible);
            answerButtons[i].interactable = visible;
            if (visible)
            {
                TMP_Text label = GetAnswerLabel(answerButtons[i]);
                if (label != null)
                    label.text = WrapChoiceText(item.choices[i]);
            }
        }
        LayoutAnswerButtons();
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
    }

    private static string WrapChoiceText(string text)
    {
        // 실제 버튼 폭을 기준으로 TMP가 자연스럽게 줄바꿈한다.
        // 문자 수 기준 강제 줄바꿈은 "득실 / 을"처럼 조사가 따로 떨어지는 원인이 된다.
        return (text ?? string.Empty).Trim();
    }

    private void LayoutAnswerButtons()
    {
        const float firstY = -45f;
        const float gap = 22f;
        const float cardWidth = 1100f;
        const float cardHeight = 150f;
        float currentY = firstY;

        for (int i = 0; i < answerButtons.Length; i++)
        {
            Button button = answerButtons[i];
            if (button == null || !button.gameObject.activeSelf)
                continue;

            TMP_Text label = GetAnswerLabel(button);
            RectTransform rect = button.GetComponent<RectTransform>();
            if (label == null || rect == null)
                continue;

            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(cardWidth, cardHeight);
            // X는 기존 위치를 유지하고 Y만 일정 간격으로 정렬한다.
            rect.anchoredPosition = new Vector2(440f, currentY);

            ConfigureAnswerTextRect(label);
            label.alignment = TextAlignmentOptions.MidlineRight;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;

            Transform choiceNumber = button.transform.Find("Choice Number");
            if (choiceNumber != null)
                choiceNumber.gameObject.SetActive(false);

            currentY -= cardHeight + gap;
        }
    }

    private void CheckAnswer(int selectedIndex)
    {
        if (isAnswerLocked || isDailyQuizFinished || currentIndex >= currentQuestions.Count)
            return;
        isAnswerLocked = true;
        StudyActivityQuestion item = currentQuestions[currentIndex];
        bool correct = selectedIndex == item.answerIndex;
        if (resultText != null)
        {
            resultText.gameObject.SetActive(false);
        }
        if (resultBox != null)
        {
            resultBox.gameObject.SetActive(false);
        }
        if (correctClip != null)
            feedbackAudioSource.PlayOneShot(correctClip, 0.24f);
        foreach (Button button in answerButtons)
            button.interactable = false;

        if (correct)
        {
            correctAnswerCount++;
            if (GameFlowManager.Instance == null ||
                !GameFlowManager.Instance.V3ShowDialogue("나", item.correctText, AdvanceAfterCorrect))
                Invoke(nameof(AdvanceAfterCorrect), Mathf.Max(1.8f, nextQuestionDelay));
        }
        else
        {
            string reaction = string.IsNullOrWhiteSpace(item.wrongText)
                ? "아, 이 번호는 아닌 것 같은데... 다시 찾아보자."
                : item.wrongText;
            if (GameFlowManager.Instance == null ||
                !GameFlowManager.Instance.V3ShowDialogue("나", reaction, RetryCurrentQuestion))
                Invoke(nameof(RetryCurrentQuestion), 1.8f);
        }
    }

    private void RetryCurrentQuestion()
    {
        isAnswerLocked = false;
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        foreach (Button button in answerButtons)
            if (button.gameObject.activeSelf)
                button.interactable = true;
    }

    private void AdvanceAfterCorrect()
    {
        currentIndex++;
        if (currentIndex >= currentQuestions.Count)
        {
            FinishDailyQuiz();
            return;
        }
        ShowActivityPrelude();
        Invoke(nameof(LoadQuestion), 0.9f);
    }

    private void FinishDailyQuiz()
    {
        SetQuestionLayout(false);
        isDailyQuizFinished = true;
        isAnswerLocked = true;
        questionText.text = $"{CurrentActivityTitle}을 마쳤다.\n\n두 시간이 지났다.";
        if (progressText != null)
            progressText.text = "오늘 일정 완료";
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        SetAnswerArea(false);
        if (!completionReported)
        {
            completionReported = true;
            DailyQuizCompleted?.Invoke(correctAnswerCount, currentQuestions.Count);
        }
    }

    private void ShowUnavailable(string body, string label)
    {
        SetQuestionLayout(false);
        isDailyQuizFinished = true;
        questionText.text = body;
        if (progressText != null)
            progressText.text = label;
        if (resultText != null)
            resultText.gameObject.SetActive(false);
        if (resultBox != null)
            resultBox.gameObject.SetActive(false);
        SetAnswerArea(false);
    }

    private void SetAnswerArea(bool visible)
    {
        if (answerPanelObject != null)
            answerPanelObject.SetActive(visible);
        if (answerHeaderObject != null)
            answerHeaderObject.SetActive(false);
        foreach (Button button in answerButtons)
            button.gameObject.SetActive(visible);
    }

    private void SetQuestionLayout(bool answering)
    {
        if (questionText == null)
            return;

        RectTransform rect = questionText.rectTransform;
        rect.anchorMin = answering ? new Vector2(0f, 0.45f) : Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = answering ? new Vector2(-70f, -100f) : new Vector2(-100f, -110f);
    }

    public void ResetDailyQuiz()
    {
        ConfigureForDay(configuredDay, isWeekday);
    }

    private void LoadActivities()
    {
        questionsByDay.Clear();
        TextAsset asset = Resources.Load<TextAsset>("StudyActivities");
        if (asset == null)
        {
            Debug.LogError("Assets/Resources/StudyActivities.csv를 찾을 수 없습니다.");
            return;
        }
        List<List<string>> rows = ParseCsv(asset.text);
        if (rows.Count < 2)
            return;
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
            columns[rows[0][i].Trim().TrimStart('\uFEFF')] = i;
        for (int i = 1; i < rows.Count; i++)
        {
            if (!int.TryParse(Read(rows[i], columns, "day"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int day))
                continue;
            int.TryParse(Read(rows[i], columns, "answer_index"), out int answerIndex);
            var item = new StudyActivityQuestion
            {
                day = day,
                activityTitle = Read(rows[i], columns, "activity_title"),
                progressLabel = Read(rows[i], columns, "progress_label"),
                activityText = Read(rows[i], columns, "activity_text"),
                question = Read(rows[i], columns, "question"),
                choices = new[]
                {
                    Read(rows[i], columns, "choice_a"),
                    Read(rows[i], columns, "choice_b"),
                    Read(rows[i], columns, "choice_c")
                },
                answerIndex = Mathf.Clamp(answerIndex, 0, 2),
                correctText = Read(rows[i], columns, "correct_text"),
                wrongText = Read(rows[i], columns, "wrong_text")
            };
            if (!questionsByDay.TryGetValue(day, out List<StudyActivityQuestion> list))
            {
                list = new List<StudyActivityQuestion>();
                questionsByDay.Add(day, list);
            }
            list.Add(item);
        }
    }

    private static string Read(List<string> row, Dictionary<string, int> columns, string key)
    {
        return columns.TryGetValue(key, out int index) && index < row.Count ? row[index].Trim() : string.Empty;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((c == '\n' || c == '\r') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0])) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
