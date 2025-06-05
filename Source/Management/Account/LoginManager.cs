using System;
using System.Net.Http;
using Godot;

namespace LumoraVR.Source.Management
{
    public partial class LoginManager : Node
    {
        public static LoginManager Instance;
        private const string ApiBaseUrl = "https://api.lumoravr.com";
        private readonly System.Net.Http.HttpClient _httpClient;
        private const string AUTH_KEY = "auth_data";

        public event Action<bool> OnLoginStatusChanged;
        public bool IsLoggedIn => _authData?.AuthToken != null;
        private UserAuthData _authData;
        private bool _requires2FA;
        private string _pendingUsername;
        private string _pendingPassword;

        public LoginManager()
        {
            _httpClient = new System.Net.Http.HttpClient
            {
                BaseAddress = new Uri(ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public override void _Ready()
        {
            Instance = this;
            LoadSavedSession();
        }

        public override void _Process(double delta)
        {
            base._Process(delta);

            if (IsLoggedIn && IsTokenExpired())
            {
                Logout();
            }
        }

        public override void _ExitTree()
        {
            _httpClient.Dispose();
            base._ExitTree();
        }
    }
}
