using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[CreateAssetMenu(fileName = "VoidEventChannelSO", menuName = "SO/Game/Events/Void Event Channel")]
public class VoidEventChannelSO : ScriptableObject
{
    private readonly List<UnityAction> listeners = new List<UnityAction>();
    public void Raise() { for (int i = listeners.Count - 1; i >= 0; i--) listeners[i]?.Invoke(); }
    public void RegisterListener(UnityAction listener) => listeners.Add(listener);
    public void UnregisterListener(UnityAction listener) => listeners.Remove(listener);
}
