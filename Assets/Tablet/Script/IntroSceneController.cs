using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class IntroSceneController : MonoBehaviour
{
    [SerializeField] private CanvasGroup content;
    [SerializeField] private Button startButton;
    private AsyncOperation preloadedScene;

    private void Awake()
    {
        if (content != null)
            content.alpha = 0f;
        if (startButton != null)
        {
            startButton.onClick.RemoveListener(StartGame);
            startButton.onClick.AddListener(StartGame);
        }
    }

    private IEnumerator Start()
    {
        preloadedScene = SceneManager.LoadSceneAsync("TabletUI", LoadSceneMode.Single);
        if (preloadedScene != null)
            preloadedScene.allowSceneActivation = false;
        yield return null;
    }

    private void Update()
    {
        if (content != null && content.alpha < 1f)
            content.alpha = Mathf.MoveTowards(content.alpha, 1f, Time.unscaledDeltaTime * 1.8f);
    }

    private void OnDestroy()
    {
        if (startButton != null)
            startButton.onClick.RemoveListener(StartGame);
    }

    public void StartGame()
    {
        if (startButton != null)
            startButton.interactable = false;

        if (preloadedScene != null)
            preloadedScene.allowSceneActivation = true;
        else
            SceneManager.LoadScene("TabletUI");
    }
}
