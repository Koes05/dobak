using UnityEngine;

[System.Serializable]
public class NotificationData
{
    [Header("아이콘")]
    public Sprite icon;

    [Header("제목")]
    public string title;

    [Header("내용")]
    public string message;
}

