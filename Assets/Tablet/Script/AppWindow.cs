using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 앱 종류
/// Inspector에서도 선택할 수 있다.
/// </summary>
public enum AppType
{
    Browser,
    Study,
    Bank,
    Message,
    Map,
    Setting
}

/// <summary>
/// Inspector에서
/// 앱 종류와 UI를 연결하기 위한 클래스
/// </summary>
[System.Serializable]
public class AppData
{
    [Header("앱 종류")]
    public AppType appType;

    [Header("실제 UI")]
    public GameObject appUI;
}

public class AppWindow : MonoBehaviour
{
    public event Action<AppType?> AppChanged;

    //=========================
    // Inspector
    //=========================

    [Header("등록할 앱")]
    [SerializeField]
    private AppData[] apps;

    [Header("공용 Splash")]
    [SerializeField]
    private GameObject splash;

    [SerializeField]
    private RectTransform splashRect;

    [Header("애니메이션 시간")]
    [SerializeField]
    private float animationTime = 0.25f;

    [SerializeField]
    private float splashTime = 0.2f;

    //=========================
    // 내부 변수
    //=========================

    // Dictionary
    // Key : AppType
    // Value : GameObject(UI)
    private Dictionary<AppType, GameObject> appDictionary = new Dictionary<AppType, GameObject>();

    // 현재 열려있는 앱
    private GameObject currentApp;

    // 애니메이션 중복 실행 방지
    private bool isOpening;

    //=========================
    // 시작
    //=========================

    private void Start()
    {
        // Splash 숨김
        splash.SetActive(false);

        // Dictionary 등록
        foreach (AppData app in apps)
        {
            // 같은 AppType이 두 번 등록되는 것을 방지
            if (!appDictionary.ContainsKey(app.appType))
            {
                appDictionary.Add(app.appType, app.appUI);
            }

            // 시작 시 모든 앱 끄기
            app.appUI.SetActive(false);
        }
    }

    //=========================
    // 앱 열기
    //=========================

    public void OpenApp(AppType type)
    {
        if (type == AppType.Browser && GameFlowManager.Instance != null && !GameFlowManager.Instance.IsGamblingUnlocked)
            return;

        if (type == AppType.Study && GameFlowManager.Instance != null && !GameFlowManager.Instance.CanOpenStudy())
            return;

        // 애니메이션 중이면 무시
        if (isOpening)
            return;

        StartCoroutine(OpenRoutine(type));
    }

    //=========================
    // 실제 실행
    //=========================

    IEnumerator OpenRoutine(AppType type)
    {
        isOpening = true;

        // 이전 앱이 켜져있으면 종료
        if (currentApp != null)
        {
            currentApp.SetActive(false);
        }

        //-------------------
        // Splash 시작
        //-------------------

        splash.SetActive(true);

        // 아래에서 위로 커지도록
        splashRect.localScale = new Vector3(1, 0, 1);

        float timer = 0;

        while (timer < animationTime)
        {
            timer += Time.deltaTime;

            float t = Mathf.SmoothStep(0, 1, timer / animationTime);

            splashRect.localScale = new Vector3(1, t, 1);

            yield return null;
        }

        //-------------------
        // 앱 켜기
        //-------------------

        // Dictionary에서 바로 찾기
        if (appDictionary.TryGetValue(type, out GameObject app))
        {
            currentApp = app;
            currentApp.SetActive(true);
            AppChanged?.Invoke(type);
        }

        //-------------------
        // Splash 잠깐 유지
        //-------------------

        yield return new WaitForSeconds(splashTime);

        //-------------------
        // Splash 종료
        //-------------------

        splash.SetActive(false);

        isOpening = false;
    }

    //=========================
    // 앱 닫기
    //=========================

    public void CloseCurrentApp()
    {
        if (currentApp != null)
        {
            currentApp.SetActive(false);
            currentApp = null;
            AppChanged?.Invoke(null);
        }
    }

    //=========================
    // 버튼 연결용 함수
    //=========================

    public void OpenBrowser()
    {
        OpenApp(AppType.Browser);
    }

    public void OpenBank()
    {
        OpenApp(AppType.Bank);
    }

    public void OpenMessage()
    {
        OpenApp(AppType.Message);
    }

    public void OpenStudy()
    {
        OpenApp(AppType.Study);
    }

    public void OpenSetting()
    {
        OpenApp(AppType.Setting);
    }
    
    public void OpenMap()
    {
        OpenApp(AppType.Map);
    }
}
