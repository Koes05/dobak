using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Dobak.App.Casino.Auth
{
    public class AuthUIController : MonoBehaviour
    {

        [Header("Signup Panel")]
        [SerializeField] private GameObject signupPanel;
        [SerializeField] private TMP_InputField signupIdInput;
        [SerializeField] private TMP_InputField signupPwInput;
        [SerializeField] private TMP_Text signupErrorText;
        [SerializeField] private Button signupButton;

        private UserData userData;

        private void OnEnable()
        {
            signupButton.onClick.AddListener(OnClickSignup);
            userData = FindAnyObjectByType<UserData>();

            if (userData.IsCasinoLoggined)
            {
                this.signupPanel.SetActive(false);
            }
            else
            {
                this.signupPanel.SetActive(true);
            }
        }

        public void OnClickSignup()
        {
            string userId = signupIdInput.text.Trim();
            string userPw = signupPwInput.text.Trim();

            // 간단한 입력값 검증
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userPw))
            {
                signupErrorText.text = "아이디와 비밀번호를 입력해주세요.";
                return;
            }

            // TODO: 실제 서버와 통신하여 회원가입/로그인 처리
            // 여기서는 예시로 로컬에서 성공 처리
            bool signupSuccess = MockSignup(userId, userPw);

            if (signupSuccess)
            {
                // 로그인 성공 처리
                Debug.Log($"로그인 성공: {userId}");
                signupErrorText.text = "";
                CloseAuth();

                userData.SaveData(userId, userPw);
            }
            else
            {
                signupErrorText.text = "회원가입/로그인 실패. 다시 시도해주세요.";
            }
        }

        private bool MockSignup(string id, string pw)
        {
            // 실제로는 서버 API 호출 필요
            // 여기서는 단순히 비밀번호 길이로 성공 여부 판정
            return pw.Length >= 4;
        }

        public void CloseAuth()
        {
            signupPanel.SetActive(false);
        }
    }
}