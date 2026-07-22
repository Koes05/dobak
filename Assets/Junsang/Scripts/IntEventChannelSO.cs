using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[CreateAssetMenu(fileName = "IntEventChannelSO", menuName = "SO/Game/Events/Int Event Channel")]
public class IntEventChannelSO : ScriptableObject
{
    private readonly List<UnityAction<int>> listeners = new List<UnityAction<int>>();

    public void Raise(int value)
    {
        for (int i = listeners.Count - 1; i >= 0; i--)
            listeners[i]?.Invoke(value);
    }

    public void RegisterListener(UnityAction<int> listener) => listeners.Add(listener);
    public void UnregisterListener(UnityAction<int> listener) => listeners.Remove(listener);
}
