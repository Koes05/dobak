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

    [Header("앱 타입")]
    public AppType appType; 

    [Header("발신자 타입")]
    public SpeakerType speakerType;

}

