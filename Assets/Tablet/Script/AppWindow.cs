using System.Collections;
using UnityEngine;

public enum AppType
{
    Browser,
    Bank,
    Message,
    Phone,
    Setting
}

[System.Serializable]
public class AppData
{
    public AppType appType;

    public GameObject appUI;
}

public class AppWindow : MonoBehaviour
{
    [Header("앱 전체")]
    [SerializeField] private RectTransform app;

    [Header("흰색 버퍼링 화면")]
    [SerializeField] private GameObject splash;
    

    [Header("실제 UI")]
    [SerializeField] private GameObject ui;

    [Header("설정")]
    [SerializeField] private float animationTime = 0.25f;

    [SerializeField] private float splashTime = 0.35f;


    public void Start()
    {
        splash.SetActive(false);
        ui.SetActive(false);

    }
    public void Open()
    {
        StopAllCoroutines();
        StartCoroutine(OpenRoutine());
        app.gameObject.SetActive(true);
        splash.SetActive(true);
    }

    IEnumerator OpenRoutine()
    {
        app.gameObject.SetActive(true);

        splash.SetActive(true);
        ui.SetActive(true);

        app.localScale = Vector3.one * 0.85f;

        float t = 0;

        while (t < animationTime)
        {
            t += Time.deltaTime;

            float value = Mathf.SmoothStep(0,1,t/animationTime);

            app.localScale = Vector3.Lerp(
                Vector3.one * 0.85f,
                Vector3.one,
                value);

            yield return null;
        }

        app.localScale = Vector3.one;

        yield return new WaitForSeconds(splashTime);

        splash.SetActive(false);
    }

    public void Close()
    {
        app.gameObject.SetActive(false);

        splash.SetActive(false);
    }
}