using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class ScenarioV3Choice
{
    public string id;
    public string text;
    public string replyText;
    public string effects;
    public string nextSceneId;
}

[Serializable]
public sealed class ScenarioV3Line
{
    public string id;
    public int sequence;
    public string speaker;
    public string contact;
    public string delivery;
    public string portrait;
    public string text;
    public string enterEffects;
    public string autoNext;
    public ScenarioV3Choice choiceA;
    public ScenarioV3Choice choiceB;
    public ScenarioV3Choice choiceC;

    public IEnumerable<ScenarioV3Choice> Choices
    {
        get
        {
            if (choiceA != null && !string.IsNullOrWhiteSpace(choiceA.id)) yield return choiceA;
            if (choiceB != null && !string.IsNullOrWhiteSpace(choiceB.id)) yield return choiceB;
            if (choiceC != null && !string.IsNullOrWhiteSpace(choiceC.id)) yield return choiceC;
        }
    }
}

[Serializable]
public sealed class ScenarioV3Scene
{
    public string id;
    public string arc;
    public string day;
    public string timeWindow;
    public string trigger;
    public string condition;
    public int priority;
    public string onceScope;
    public string purpose;
    public readonly List<ScenarioV3Line> lines = new List<ScenarioV3Line>();
}

public sealed class ScenarioV3Database
{
    private readonly Dictionary<string, ScenarioV3Scene> scenes =
        new Dictionary<string, ScenarioV3Scene>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ScenarioV3Scene>> scenesByTrigger =
        new Dictionary<string, List<ScenarioV3Scene>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> returnToTabletScenes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> checkpointLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int SceneCount => scenes.Count;
    public IEnumerable<ScenarioV3Scene> Scenes => scenes.Values;

    public static ScenarioV3Database Load()
    {
        TextAsset asset = Resources.Load<TextAsset>("ScenarioV3");
        if (asset == null)
            throw new InvalidOperationException("Assets/Resources/ScenarioV3.csv를 찾을 수 없습니다.");

        var database = new ScenarioV3Database();
        List<List<string>> records = ParseCsv(asset.text);
        if (records.Count < 2)
            throw new InvalidOperationException("ScenarioV3.csv에 데이터가 없습니다.");

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < records[0].Count; i++)
            columns[records[0][i].Trim().TrimStart('\uFEFF')] = i;

        var choiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            List<string> row = records[rowIndex];
            string sceneId = Read(row, columns, "scene_id");
            string lineId = Read(row, columns, "line_id");
            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(lineId))
                continue;

            if (!database.scenes.TryGetValue(sceneId, out ScenarioV3Scene scene))
            {
                int.TryParse(Read(row, columns, "priority"), out int priority);
                scene = new ScenarioV3Scene
                {
                    id = sceneId,
                    arc = Read(row, columns, "arc"),
                    day = Read(row, columns, "day"),
                    timeWindow = Read(row, columns, "time_window"),
                    trigger = Read(row, columns, "trigger"),
                    condition = Read(row, columns, "condition"),
                    priority = priority,
                    onceScope = Read(row, columns, "once_scope"),
                    purpose = Read(row, columns, "purpose")
                };
                database.scenes.Add(sceneId, scene);
                if (!database.scenesByTrigger.TryGetValue(scene.trigger, out List<ScenarioV3Scene> triggerScenes))
                {
                    triggerScenes = new List<ScenarioV3Scene>();
                    database.scenesByTrigger.Add(scene.trigger, triggerScenes);
                }
                triggerScenes.Add(scene);
            }

            int sequence = scene.lines.Count + 1;
            int.TryParse(Read(row, columns, "sequence"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out sequence);
            var line = new ScenarioV3Line
            {
                id = lineId,
                sequence = sequence,
                speaker = Read(row, columns, "speaker"),
                contact = Read(row, columns, "contact"),
                delivery = Read(row, columns, "delivery"),
                portrait = Read(row, columns, "portrait"),
                text = Read(row, columns, "text"),
                enterEffects = Read(row, columns, "enter_effects"),
                autoNext = Read(row, columns, "auto_next"),
                choiceA = CreateChoice(row, columns, "a"),
                choiceB = CreateChoice(row, columns, "b"),
                choiceC = CreateChoice(row, columns, "c")
            };

            foreach (ScenarioV3Choice choice in line.Choices)
            {
                if (!choiceIds.Add(choice.id))
                    throw new InvalidOperationException($"중복된 choice_id: {choice.id}");
            }
            scene.lines.Add(line);
        }

        foreach (ScenarioV3Scene scene in database.scenes.Values)
        {
            scene.lines.Sort((left, right) => left.sequence.CompareTo(right.sequence));
            foreach (ScenarioV3Line line in scene.lines)
            {
                ValidateTarget(database, line.id, line.autoNext);
                foreach (ScenarioV3Choice choice in line.Choices)
                    ValidateTarget(database, $"{scene.id}/{choice.id}", choice.nextSceneId);
            }
        }

        ApplyReplyTexts(database, choiceIds);
        ApplyLineTextOverrides(database);
        ApplyFlowBindings(database);
        ApplyCheckpointDefinitions(database);

        foreach (List<ScenarioV3Scene> triggerScenes in database.scenesByTrigger.Values)
            triggerScenes.Sort((left, right) => right.priority.CompareTo(left.priority));

        Debug.Log($"[Scenario V3] {database.SceneCount}개 장면을 불러왔습니다.");
        return database;
    }

    private static void ApplyReplyTexts(ScenarioV3Database database, HashSet<string> choiceIds)
    {
        TextAsset replyAsset = Resources.Load<TextAsset>("ScenarioV3Replies");
        if (replyAsset == null)
            return;

        List<List<string>> rows = ParseCsv(replyAsset.text);
        if (rows.Count < 2)
            return;

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
            columns[rows[0][i].Trim().TrimStart('\uFEFF')] = i;

        var choicesById = database.scenes.Values
            .SelectMany(scene => scene.lines)
            .SelectMany(line => line.Choices)
            .ToDictionary(choice => choice.id, StringComparer.OrdinalIgnoreCase);

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            string choiceId = Read(rows[rowIndex], columns, "choice_id");
            string replyText = Read(rows[rowIndex], columns, "reply_text");
            if (string.IsNullOrWhiteSpace(choiceId) || string.IsNullOrWhiteSpace(replyText))
                continue;
            if (!choiceIds.Contains(choiceId) || !choicesById.TryGetValue(choiceId, out ScenarioV3Choice choice))
                throw new InvalidOperationException($"ScenarioV3Replies.csv가 없는 선택지 {choiceId}을 참조합니다.");
            choice.replyText = replyText;
        }
    }

    private static void ApplyLineTextOverrides(ScenarioV3Database database)
    {
        TextAsset textAsset = Resources.Load<TextAsset>("ScenarioV3Narration");
        if (textAsset == null)
            return;

        List<List<string>> rows = ParseCsv(textAsset.text);
        if (rows.Count < 2)
            return;

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
            columns[rows[0][i].Trim().TrimStart('\uFEFF')] = i;

        var linesById = database.scenes.Values
            .SelectMany(scene => scene.lines)
            .ToDictionary(line => line.id, StringComparer.OrdinalIgnoreCase);

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            string lineId = Read(rows[rowIndex], columns, "line_id");
            string text = Read(rows[rowIndex], columns, "text");
            if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(text))
                continue;
            if (!linesById.TryGetValue(lineId, out ScenarioV3Line line))
                throw new InvalidOperationException($"ScenarioV3Narration.csv가 없는 대사 {lineId}을 참조합니다.");
            if (string.IsNullOrWhiteSpace(line.text))
                line.text = text;
        }
    }

    public ScenarioV3Scene GetScene(string sceneId)
    {
        scenes.TryGetValue(sceneId ?? string.Empty, out ScenarioV3Scene scene);
        return scene;
    }

    public IReadOnlyList<ScenarioV3Scene> GetByTrigger(string trigger)
    {
        return scenesByTrigger.TryGetValue(trigger ?? string.Empty, out List<ScenarioV3Scene> result)
            ? result
            : Array.Empty<ScenarioV3Scene>();
    }

    public bool ShouldReturnToTablet(string sceneId)
    {
        return returnToTabletScenes.Contains(sceneId ?? string.Empty);
    }

    public bool TryGetCheckpointLabel(ScenarioV3Line line, out string label)
    {
        foreach (ScenarioV3Choice choice in line.Choices)
        {
            if (checkpointLabels.TryGetValue(choice.id, out label))
                return true;
        }
        label = string.Empty;
        return false;
    }

    private static void ApplyCheckpointDefinitions(ScenarioV3Database database)
    {
        TextAsset asset = Resources.Load<TextAsset>("ScenarioV3Checkpoints");
        if (asset == null)
            return;

        List<List<string>> rows = ParseCsv(asset.text);
        if (rows.Count < 2)
            return;

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
            columns[rows[0][i].Trim().TrimStart('\uFEFF')] = i;

        HashSet<string> knownChoices = database.scenes.Values
            .SelectMany(scene => scene.lines)
            .SelectMany(line => line.Choices)
            .Select(choice => choice.id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            string choiceId = Read(rows[rowIndex], columns, "choice_id");
            string label = Read(rows[rowIndex], columns, "label");
            if (!knownChoices.Contains(choiceId))
                throw new InvalidOperationException($"ScenarioV3Checkpoints.csv가 없는 선택지 {choiceId}을 참조합니다.");
            database.checkpointLabels[choiceId] = label;
        }
    }

    private static void ApplyFlowBindings(ScenarioV3Database database)
    {
        TextAsset flowAsset = Resources.Load<TextAsset>("ScenarioV3Flow");
        if (flowAsset == null)
            return;

        List<List<string>> rows = ParseCsv(flowAsset.text);
        if (rows.Count < 2)
            return;

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
            columns[rows[0][i].Trim().TrimStart('\uFEFF')] = i;

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            string sceneId = Read(rows[rowIndex], columns, "scene_id");
            string extraTrigger = Read(rows[rowIndex], columns, "extra_trigger");
            string returnToTablet = Read(rows[rowIndex], columns, "return_to_tablet");
            ScenarioV3Scene scene = database.GetScene(sceneId);
            if (scene == null)
                throw new InvalidOperationException($"ScenarioV3Flow.csv가 없는 장면 {sceneId}을 참조합니다.");

            if (string.Equals(returnToTablet, "true", StringComparison.OrdinalIgnoreCase))
                database.returnToTabletScenes.Add(sceneId);

            if (string.IsNullOrWhiteSpace(extraTrigger))
                continue;
            if (!database.scenesByTrigger.TryGetValue(extraTrigger, out List<ScenarioV3Scene> triggerScenes))
            {
                triggerScenes = new List<ScenarioV3Scene>();
                database.scenesByTrigger.Add(extraTrigger, triggerScenes);
            }
            if (!triggerScenes.Contains(scene))
                triggerScenes.Add(scene);
        }
    }

    private static ScenarioV3Choice CreateChoice(List<string> row,
        Dictionary<string, int> columns, string suffix)
    {
        string id = Read(row, columns, $"choice_{suffix}_id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return new ScenarioV3Choice
        {
            id = id,
            text = Read(row, columns, $"choice_{suffix}_text"),
            replyText = Read(row, columns, $"choice_{suffix}_reply"),
            effects = Read(row, columns, $"choice_{suffix}_effects"),
            nextSceneId = Read(row, columns, $"choice_{suffix}_next")
        };
    }

    private static void ValidateTarget(ScenarioV3Database database, string source, string target)
    {
        if (!string.IsNullOrWhiteSpace(target) && database.GetScene(target) == null)
            throw new InvalidOperationException($"{source}에서 없는 장면 {target}을 참조합니다.");
    }

    private static string Read(List<string> row, Dictionary<string, int> columns, string key)
    {
        return columns.TryGetValue(key, out int index) && index >= 0 && index < row.Count
            ? row[index].Trim()
            : string.Empty;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0]))
                    rows.Add(row);
                row = new List<string>();
            }
            else
            {
                field.Append(character);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
