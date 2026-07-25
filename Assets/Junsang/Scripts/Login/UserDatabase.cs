using System.Collections.Generic;

namespace Dobak.App.Casino.Auth
{
    [System.Serializable]
    public class UserAccount
    {
        public string id;
        public string password;
        public int coin;
    }

    [System.Serializable]
    public class UserDatabase
    {
        public List<UserAccount> accounts = new List<UserAccount>();
    }
}
