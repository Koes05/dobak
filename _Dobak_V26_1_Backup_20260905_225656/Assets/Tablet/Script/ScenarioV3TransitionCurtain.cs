using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class ScenarioV3TransitionCurtain : MonoBehaviour
{
    private static ScenarioV3TransitionCurtain instance;
    private CanvasGroup group;

    public static void CoverUntilFirstStory()
    {
        if (instance != null)
        {
            instance.ShowImmediate();
            instance.StopAllCoroutines();
            instance.StartCoroutine(instance.WaitForFirstStory());
            return;
        }

        GameObject root = new GameObject("Scenario V3 Launch Curtain", typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster), typeof(CanvasGroup), typeof(ScenarioV3TransitionCurtain));
        DontDestroyOnLoad(root);

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1200f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject black = new GameObject("Black", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        black.transform.SetParent(root.transform, false);
        RectTransform rect = black.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Image image = black.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = true;

        instance = root.GetComponent<ScenarioV3TransitionCurtain>();
        instance.group = root.GetComponent<CanvasGroup>();
        instance.ShowImmediate();
        instance.StartCoroutine(instance.WaitForFirstStory());
    }

    private void ShowImmediate()
    {
        if (group == null)
            group = GetComponent<CanvasGroup>();
        group.alpha = 1f;
        group.blocksRaycasts = true;
        group.interactable = true;
    }

    private IEnumerator WaitForFirstStory()
    {
        float timeout = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < timeout)
        {
            if (SceneManager.GetActiveScene().name == "TabletUI")
            {
                ScenarioV3Director director = FindAnyObjectByType<ScenarioV3Director>(FindObjectsInactive.Include);
                GameObject novel = GameObject.Find("Scenario V3 Novel");
                if (director != null && director.IsReady && !string.IsNullOrWhiteSpace(director.ActiveSceneId) &&
                    novel != null && novel.activeInHierarchy)
                    break;
            }
            yield return null;
        }

        // 첫 VN의 TMP/배경이 같은 프레임에 갱신되는 경우까지 가린다.
        yield return null;
        yield return new WaitForEndOfFrame();

        float elapsed = 0f;
        const float duration = 0.16f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            group.alpha = Mathf.Clamp01(1f - elapsed / duration);
            yield return null;
        }

        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;
        Destroy(gameObject);
        instance = null;
    }
}
