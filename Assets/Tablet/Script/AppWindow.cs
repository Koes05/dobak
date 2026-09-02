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
    Setting,
    SNS,
    Sleep
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
    private AppType? pendingAppType;
    private Coroutine openingRoutine;
    private float openingStartedAt;
    private AudioSource uiAudioSource;
    private AudioClip appOpenClip;

    public AppType? CurrentAppType { get; private set; }

    //=========================
    // 시작
    //=========================

    private void Start()
    {
        uiAudioSource = gameObject.AddComponent<AudioSource>();
        uiAudioSource.playOnAwake = false;
        uiAudioSource.volume = 0.35f;
        appOpenClip = Resources.Load<AudioClip>("Audio/SFX/app_open");

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

        if (CurrentAppType == type && currentApp != null && currentApp.activeInHierarchy)
            return;

        // 앱 본체는 즉시 활성화하고 스플래시는 그 위에서 재생한다.
        // 그래야 연출 도중 메시지나 선택지가 도착해도 비활성 UI에서 코루틴이 끊기지 않는다.
        if (isOpening)
            CancelOpening();

        ActivateApp(type);
        StartOpenRoutine(type);
    }

    private void ActivateApp(AppType type)
    {
        if (currentApp != null)
            currentApp.SetActive(false);

        currentApp = null;
        CurrentAppType = null;
        AppChanged?.Invoke(null);

        if (!appDictionary.TryGetValue(type, out GameObject app))
            return;

        currentApp = app;
        currentApp.SetActive(true);
        CurrentAppType = type;
        if (type == AppType.Message)
        {
            DialogueManager dialogue = currentApp.GetComponentInChildren<DialogueManager>(true);
            if (dialogue == null)
                dialogue = FindAnyObjectByType<DialogueManager>(FindObjectsInactive.Include);
            dialogue?.OpenMostRecentConversation();
        }
        AppChanged?.Invoke(type);
    }

    private void StartOpenRoutine(AppType type)
    {
        openingStartedAt = Time.realtimeSinceStartup;
        openingRoutine = StartCoroutine(OpenRoutine(type));
    }

    private void CancelOpening()
    {
        if (openingRoutine != null)
            StopCoroutine(openingRoutine);
        openingRoutine = null;
        isOpening = false;
        pendingAppType = null;
        if (splash != null)
            splash.SetActive(false);
    }

    //=========================
    // 실제 실행
    //=========================

    IEnumerator OpenRoutine(AppType type)
    {
        isOpening = true;
        if (appOpenClip != null)
            uiAudioSource.PlayOneShot(appOpenClip, 0.22f);

        //-------------------
        // Splash 시작
        //-------------------

        splash.SetActive(true);

        // 아래에서 위로 커지도록
        splashRect.localScale = new Vector3(1, 0, 1);

        float timer = 0;

        while (timer < animationTime)
        {
            timer += Time.unscaledDeltaTime;

            float t = Mathf.SmoothStep(0, 1, timer / animationTime);

            splashRect.localScale = new Vector3(1, t, 1);

            yield return null;
        }

        // 앱은 이미 활성화되어 있고 스플래시만 잠깐 유지한다.

        yield return new WaitForSecondsRealtime(splashTime);

        //-------------------
        // Splash 종료
        //-------------------

        splash.SetActive(false);

        isOpening = false;
        openingRoutine = null;

        pendingAppType = null;
    }

    //=========================
    // 앱 닫기
    //=========================

    public void CloseCurrentApp()
    {
        CancelOpening();
        if (currentApp != null)
        {
            currentApp.SetActive(false);
            currentApp = null;
            CurrentAppType = null;
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

    public void RegisterRuntimeApp(AppType type, GameObject app)
    {
        if (app == null)
            return;

        appDictionary[type] = app;
        app.SetActive(false);
    }

    public void OpenSetting()
    {
        OpenApp(AppType.Setting);
    }
    
    public void OpenMap()
    {
        OpenApp(AppType.Map);
    }

    public void OpenSNS()
    {
        OpenApp(AppType.SNS);
    }

    public void OpenSleep()
    {
        OpenApp(AppType.Sleep);
    }
}
