using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public sealed class ScenarioMessage
{
    public string trigger;
    public SpeakerType speaker;
    public string title;
    public string message;
}

public sealed class ScenarioMessageTable
{
    private readonly Dictionary<string, List<ScenarioMessage>> messages = new Dictionary<string, List<ScenarioMessage>>();

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
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            List<string> columns = ParseCsvLine(lines[i]);
            if (columns.Count < 4 || !Enum.TryParse(columns[1], true, out SpeakerType speaker))
            {
                Debug.LogWarning($"ScenarioMessages.csv {i + 1}행을 읽지 못했습니다.");
                continue;
            }

            var entry = new ScenarioMessage
            {
                trigger = columns[0].Trim(),
                speaker = speaker,
                title = columns[2].Trim(),
                message = columns[3].Trim()
            };

            if (!table.messages.TryGetValue(entry.trigger, out List<ScenarioMessage> list))
            {
                list = new List<ScenarioMessage>();
                table.messages.Add(entry.trigger, list);
            }

            list.Add(entry);
        }

        return table;
    }

    public bool TryGet(string trigger, int index, out ScenarioMessage message)
    {
        message = null;
        if (!messages.TryGetValue(trigger, out List<ScenarioMessage> list) || list.Count == 0)
            return false;

        message = list[Mathf.Abs(index) % list.Count];
        return true;
    }

    public int Count(string trigger)
    {
        return messages.TryGetValue(trigger, out List<ScenarioMessage> list) ? list.Count : 0;
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
