using UnityEngine;

namespace Dobak.App
{
    [CreateAssetMenu(fileName = "AppData", menuName = "Data/AppData")]
    public class AppData : ScriptableObject
    {
        public AppID appID;
        public GameObject appPrefab;
    }
}