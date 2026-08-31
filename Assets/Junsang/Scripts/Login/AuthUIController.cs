using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Dobak.App.Casino.Auth
{
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
            // 기존 씬 직렬화 호환용 컴포넌트다. 로그인 UI는 예방 게임에서 사용하지 않는다.
            gameObject.SetActive(false);
        }

        public void OnClickLogin()
        {
            CloseAuth();
        }

        public void OnClickSignup()
        {
            CloseAuth();
        }

        public void ShowLoginPanel()
        {
            CloseAuth();
        }

        public void ShowSignupPanel()
        {
            CloseAuth();
        }

        public void CloseAuth()
        {
            loginPanel.SetActive(false);
            signupPanel.SetActive(false);
        }
    }
}
