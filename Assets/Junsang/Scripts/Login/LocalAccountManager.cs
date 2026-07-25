using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Dobak.App.Casino.Auth
{
    public class LocalAccountManager : MonoBehaviour
    {
        public static LocalAccountManager Instance { get; private set; }

        private string filePath;
        private UserDatabase db;

        public UserAccount CurrentUser { get; private set; }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            filePath = Path.Combine(Application.persistentDataPath, "account.json");
            LoadDataBase();
        }

        private void LoadDataBase()
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                db = JsonUtility.FromJson<UserDatabase>(json);
                Debug.Log($"로드됨: {filePath}");
            }
            else
            {
                db = new UserDatabase();
            }
        }

        private void SaveDatabase()
        {
            string json = JsonUtility.ToJson(db, true);
            File.WriteAllText(filePath, json);
        }

        public bool SignUp(string id, string password, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(password))
            {
                error = "아이디/비밀번호를 입력하세요.";
                return false;
            }
            if (password.Length < 6)
            {
                error = "비밀번호는 6자 이상이어야 합니다.";
                return false;
            }
            if (db.accounts.Any(a => a.id == id))
            {
                error = "이미 존재하는 아이디입니다.";
                return false;
            }

            var newAccount = new UserAccount
            {
                id = id,
                password = HashPassword(password),
                coin = 1000
            };

            db.accounts.Add(newAccount);
            SaveDatabase();
            return true;
        }

        public bool Login(string id, string password, out string error)
        {
            error = "";
            var account = db.accounts.FirstOrDefault(a => a.id == id);

            if (account == null)
            {
                error = "존재하지 않는 아이디입니다.";
                return false;
            }
            if (account.password != HashPassword(password))
            {
                error = "비밀번호가 일치하지 않습니다.";
                return false;
            }

            CurrentUser = account;
            return true;
        }

        public void Logout()
        {
            CurrentUser = null;
        }

        public void SaveCurrentUser()
        {
            int idx = db.accounts.FindIndex(a => a.id == CurrentUser.id);
            if (idx >= 0)
            {
                db.accounts[idx] = CurrentUser;
                SaveDatabase();
            }
        }

        private string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            StringBuilder sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
