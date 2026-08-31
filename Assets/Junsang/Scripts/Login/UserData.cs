using UnityEngine;

public class UserData : MonoBehaviour
{
    public string Id { get; private set; }
    public string Pw { get; private set; }

    public bool IsCasinoLoggined { get; private set; } = false;

    public void SaveData(string id, string pw)
    {
        Id = id;
        Pw = pw;

        IsCasinoLoggined = true;
    }

    public void LoadData(ref string id, ref string pw)
    {
        id = Id;
        pw = Pw;
    }
}
