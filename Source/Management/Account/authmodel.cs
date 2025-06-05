using System;

namespace LumoraVR.Source.Management
{
    public class UserAuthData
    {
        public string AuthToken { get; set; }
        public string Username { get; set; }
        public long TokenExpiry { get; set; }
        public UserProfile Profile { get; set; }
    }

    public class UserProfile
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string NormalizedUsername { get; set; }
        public DateTime RegistrationDate { get; set; }
        public bool IsVerified { get; set; }
        public bool IsLocked { get; set; }
        public string NameColor { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public PatreonMetadata PatreonData { get; set; }
    }

    public class LoginResponse
    {
        public string Token { get; set; }
    }

    public class RegisterResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class PatreonMetadata
    {
        public string UserId { get; set; }
        public bool IsActiveSupporter { get; set; }
        public int TotalSupportMonths { get; set; }
        public int TotalSupportCents { get; set; }
        public int LastTierCents { get; set; }
        public string TierName { get; set; }
        public string TierDescription { get; set; }
        public string TierColor { get; set; }
        public DateTime FirstSupportTimestamp { get; set; }
        public DateTime LastSupportTimestamp { get; set; }
    }
}