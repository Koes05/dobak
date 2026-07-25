using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class AuthUIController : MonoBehaviour
{
    [Header("Login Panel")]
    [SerializeField] private GameObject loginPanel;
    [SerializeField] private TMP_InputField loginIdInput;
    [SerializeField] private TMP_InputField loginPwInput;
    [SerializeField] private TMP_Text loginErrorText;
    [SerializeField] private Button loginButton;
    [SerializeField] private Button gotoSignupButton;

    [Header("Signup Panel")]
    [SerializeField] private GameObject signupPanel;
    [SerializeField] private TMP_InputField signupIdInput;
    [SerializeField] private TMP_InputField signupPwInput;
    [SerializeField] private TMP_Text signupErrorText;
    [SerializeField] private Button signupButton;
    [SerializeField] private Button backtoLoginButton;

    private void Awake()
    {
        loginButton.onClick.AddListener(OnClickLogin);
        gotoSignupButton.onClick.AddListener(ShowSignupPanel);
        signupButton.onClick.AddListener(OnClickSignup);
        backtoLoginButton.onClick.AddListener(ShowLoginPanel);
    }

    public void OnClickLogin()
    {
        bool success = LocalAccountManager.Instance.Login(
            loginIdInput.text, loginPwInput.text, out string error);

        if (success)
        {
            loginErrorText.gameObject.SetActive(false);
            Debug.Log($"로그인 성공: {LocalAccountManager.Instance.CurrentUser.id}");
            CloseAuth();
            // 이후
        }
        else
        {
            loginErrorText.text = error;
            loginErrorText.gameObject.SetActive(true);
        }
    }

    public void OnClickSignup()
    {
        bool success = LocalAccountManager.Instance.SignUp(
            signupIdInput.text, signupPwInput.text, out string error);

        if (success)
        {
            signupErrorText.gameObject.SetActive(false);
            Debug.Log("회원가입 성공, 로그인 화면으로 전환");
            ShowLoginPanel();
        }
        else
        {
            signupErrorText.text = error;
            signupErrorText.gameObject.SetActive(true);
        }
    }

    public void ShowLoginPanel()
    {
        loginPanel.SetActive(true);
        signupPanel.SetActive(false);
    }

    public void ShowSignupPanel()
    {
        loginPanel.SetActive(false);
        signupPanel.SetActive(true);
    }

    public void CloseAuth()
    {
        loginPanel.SetActive(false);
        signupPanel.SetActive(false);
    }
}
