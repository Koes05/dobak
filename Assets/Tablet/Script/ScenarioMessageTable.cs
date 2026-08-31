using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

public sealed class ScenarioMessage
{
    public int sequence;
    public SpeakerType speaker;
    public string title;
    public string message;
    public string delivery;
    public string choiceA;
    public string actionA;
    public string choiceB;
    public string actionB;
}

public sealed class ScenarioEventDefinition
{
    public string id;
    public string trigger;
    public string condition;
    public float chance = 1f;
    public string selection;
    public string once;
    public string stateKey;
    public string stateValue;
    public readonly List<ScenarioMessage> steps = new List<ScenarioMessage>();
}

public sealed class ScenarioMessageTable
{
    private readonly Dictionary<string, ScenarioEventDefinition> eventsById =
        new Dictionary<string, ScenarioEventDefinition>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ScenarioEventDefinition>> eventsByTrigger =
        new Dictionary<string, List<ScenarioEventDefinition>>(StringComparer.OrdinalIgnoreCase);

    public static ScenarioMessageTable Load()
    {
        var table = new ScenarioMessageTable();
        TextAsset asset = Resources.Load<TextAsset>("ScenarioMessages");
        if (asset == null)
        {
            Debug.LogWarning("ScenarioMessages.csv를 찾을 수 없습니다.");
            return table;
        }

        string[] lines = asset.text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
            return table;

        List<string> headers = ParseCsvLine(lines[0]);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            columns[headers[i].Trim().TrimStart('\uFEFF')] = i;

        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
                continue;

            List<string> row = ParseCsvLine(lines[lineIndex]);
            string eventId = Read(row, columns, "event_id");
            string trigger = Read(row, columns, "trigger");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(trigger) ||
                !Enum.TryParse(Read(row, columns, "speaker"), true, out SpeakerType speaker))
            {
                Debug.LogWarning($"ScenarioMessages.csv {lineIndex + 1}행을 읽지 못했습니다.");
                continue;
            }

            if (!table.eventsById.TryGetValue(eventId, out ScenarioEventDefinition definition))
            {
                float chance = 1f;
                float.TryParse(Read(row, columns, "chance"), NumberStyles.Float, CultureInfo.InvariantCulture, out chance);
                definition = new ScenarioEventDefinition
                {
                    id = eventId,
                    trigger = trigger,
                    condition = Read(row, columns, "condition"),
                    chance = Mathf.Clamp01(chance),
                    selection = Read(row, columns, "selection"),
                    once = Read(row, columns, "once"),
                    stateKey = Read(row, columns, "state_key"),
                    stateValue = Read(row, columns, "state_value")
                };
                table.eventsById.Add(eventId, definition);
                if (!table.eventsByTrigger.TryGetValue(trigger, out List<ScenarioEventDefinition> triggerEvents))
                {
                    triggerEvents = new List<ScenarioEventDefinition>();
                    table.eventsByTrigger.Add(trigger, triggerEvents);
                }
                triggerEvents.Add(definition);
            }

            int sequence = definition.steps.Count;
            int.TryParse(Read(row, columns, "sequence"), out sequence);
            definition.steps.Add(new ScenarioMessage
            {
                sequence = sequence,
                speaker = speaker,
                title = Read(row, columns, "title"),
                message = Read(row, columns, "message"),
                delivery = Read(row, columns, "delivery"),
                choiceA = Read(row, columns, "choice_a"),
                actionA = Read(row, columns, "action_a"),
                choiceB = Read(row, columns, "choice_b"),
                actionB = Read(row, columns, "action_b")
            });
        }

        foreach (ScenarioEventDefinition definition in table.eventsById.Values)
            definition.steps.Sort((left, right) => left.sequence.CompareTo(right.sequence));

        return table;
    }

    public IReadOnlyList<ScenarioEventDefinition> GetCandidates(string trigger)
    {
        return eventsByTrigger.TryGetValue(trigger, out List<ScenarioEventDefinition> result)
            ? result
            : Array.Empty<ScenarioEventDefinition>();
    }

    public int Count(string eventId)
    {
        return eventsById.TryGetValue(eventId, out ScenarioEventDefinition definition) ? definition.steps.Count : 0;
    }

    public int EventCount => eventsById.Count;
    public IEnumerable<string> Triggers => eventsByTrigger.Keys;
    public IEnumerable<ScenarioEventDefinition> Events => eventsById.Values;

    public bool HasTrigger(string trigger)
    {
        return !string.IsNullOrWhiteSpace(trigger) && eventsByTrigger.ContainsKey(trigger);
    }

    private static string Read(List<string> row, Dictionary<string, int> columns, string key)
    {
        return columns.TryGetValue(key, out int index) && index >= 0 && index < row.Count
            ? row[index].Trim()
            : string.Empty;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        result.Add(current.ToString());
        return result;
    }
}
