using UnityEngine;

namespace Dobak.App.Casino.Auth
{
    /// <summary>
    /// 이전 씬의 직렬화 참조를 깨뜨리지 않기 위한 호환용 컴포넌트다.
    /// 계정 생성, 로그인, 파일 저장 기능은 의도적으로 제거되어 있다.
    /// </summary>
    public sealed class LocalAccountManager : MonoBehaviour
    {
        public static LocalAccountManager Instance => null;

        private void Awake()
        {
            Destroy(this);
        }
    }
}
