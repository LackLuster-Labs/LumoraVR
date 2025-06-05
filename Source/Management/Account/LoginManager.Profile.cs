using System;
using System.Threading.Tasks;

namespace LumoraVR.Source.Management
{
    public partial class LoginManager
    {
        private void LoadSavedSession()
        {
            try
            {
                _authData = LocalDatabase.Instance.GetValue<UserAuthData>(AUTH_KEY);

                if (_authData != null)
                {
                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (currentTime > _authData.TokenExpiry)
                    {
                        Logout();
                        return;
                    }

                    _httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authData.AuthToken);

                    OnLoginStatusChanged?.Invoke(true);
                }
            }
            catch
            {
                Logout();
            }
        }

        public string GetCurrentUsername()
        {
            return _authData?.Username;
        }

        public UserProfile GetUserProfile()
        {
            return _authData?.Profile;
        }

        public async Task RefreshUserProfile()
        {
            if (!IsLoggedIn) return;
            await FetchUserProfile();
        }
    }
}