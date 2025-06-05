using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using LumoraVR.Source.Logging;

namespace LumoraVR.Source.Management
{
    public partial class LoginManager
    {
        public class LoginResult
        {
            public bool Success { get; set; }
            public bool Requires2FA { get; set; }
            public string Error { get; set; }
        }

        public async Task<LoginResult> LoginAsync(string username, string password, string twoFactorCode = null)
        {
            try
            {
                Logger.Log($"Attempting login for user: {username}");
                if (!string.IsNullOrEmpty(twoFactorCode))
                    Logger.Log("2FA code provided");

                var loginData = new
                {
                    Username = username,
                    Password = password,
                    TwoFactorCode = twoFactorCode
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(loginData),
                    Encoding.UTF8,
                    "application/json");

                Logger.Log("Sending login request to server...");
                var response = await _httpClient.PostAsync("/api/user/login", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                Logger.Log($"Server response status: {response.StatusCode}");
                Logger.Log($"Server response content: {responseContent}");

                // Always try to parse the response content first
                JsonElement jsonResponse;
                try
                {
                    jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                    Logger.Log("Successfully parsed JSON response");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to parse JSON response: {ex.Message}");
                    return new LoginResult { Error = $"Invalid server response: {responseContent}" };
                }

                // Check for 2FA requirement
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    if (jsonResponse.TryGetProperty("title", out JsonElement titleElement) &&
                        titleElement.GetString() == "2FA Required")
                    {
                        Logger.Log("2FA required by server");
                        _requires2FA = true;
                        _pendingUsername = username;
                        _pendingPassword = password;
                        return new LoginResult { Requires2FA = true };
                    }
                }

                // If response is not successful, get the error message from the response
                if (!response.IsSuccessStatusCode)
                {
                    string errorMessage = "Invalid credentials";

                    // Try to extract error details
                    if (jsonResponse.TryGetProperty("title", out JsonElement errorTitle))
                    {
                        errorMessage = errorTitle.GetString();
                    }
                    else if (jsonResponse.TryGetProperty("error", out JsonElement errorElement))
                    {
                        errorMessage = errorElement.GetString();
                    }
                    else if (jsonResponse.TryGetProperty("message", out JsonElement messageElement))
                    {
                        errorMessage = messageElement.GetString();
                    }
                    Logger.Error($"Login failed: {errorMessage}");
                    return new LoginResult { Error = errorMessage };
                }

                // Success case - should have a token (check both camelCase and PascalCase)
                string token = null;
                if (jsonResponse.TryGetProperty("token", out JsonElement tokenElementLower))
                {
                    token = tokenElementLower.GetString();
                    Logger.Log("Found token (lowercase property)");
                }
                else if (jsonResponse.TryGetProperty("Token", out JsonElement tokenElementUpper))
                {
                    token = tokenElementUpper.GetString();
                    Logger.Log("Found token (uppercase property)");
                }

                if (!string.IsNullOrEmpty(token))
                {
                    Logger.Log("Login successful, token received");
                    _authData = new UserAuthData
                    {
                        AuthToken = token,
                        Username = username,
                        TokenExpiry = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()
                    };

                    LocalDatabase.Instance.SetValue(AUTH_KEY, _authData);
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authData.AuthToken);

                    await FetchUserProfile();
                    OnLoginStatusChanged?.Invoke(true);

                    return new LoginResult { Success = true };
                }

                Logger.Error("Invalid response structure from server");
                Logger.Log($"Response details: {responseContent}");
                return new LoginResult { Error = "Invalid response from server" };
            }
            catch (Exception ex)
            {
                Logger.Error($"Login exception: {ex.Message}");
                if (ex.InnerException != null)
                    Logger.Error($"Inner exception: {ex.InnerException.Message}");

                return new LoginResult { Error = $"Login error: {ex.Message}" };
            }
        }

        public async Task<RegisterResponse> RegisterAsync(string username, string email, string password)
        {
            try
            {
                Logger.Log($"Attempting to register user: {username}");

                var registerData = new
                {
                    Username = username,
                    Email = email,
                    Password = password
                };

                var json = JsonSerializer.Serialize(registerData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Logger.Log("Sending registration request to server...");
                var response = await _httpClient.PostAsync("/api/user/register", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                Logger.Log($"Server registration response status: {response.StatusCode}");
                Logger.Log($"Server registration response content: {responseContent}");

                string message = "Registration successful";
                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                        if (jsonResponse.TryGetProperty("message", out JsonElement messageElement))
                        {
                            message = messageElement.GetString();
                        }
                        else if (jsonResponse.TryGetProperty("error", out JsonElement errorElement))
                        {
                            message = errorElement.GetString();
                        }
                    }
                    catch
                    {
                        message = "Registration failed: " + responseContent;
                    }
                    Logger.Error($"Registration failed: {message}");
                }
                else
                {
                    Logger.Log("Registration successful");
                }

                return new RegisterResponse
                {
                    Success = response.IsSuccessStatusCode,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Registration exception: {ex.Message}");
                if (ex.InnerException != null)
                    Logger.Error($"Inner exception: {ex.InnerException.Message}");

                return new RegisterResponse
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        private async Task FetchUserProfile()
        {
            try
            {
                Logger.Log("Fetching user profile...");
                var profileResponse = await _httpClient.GetAsync("/api/user/me");

                var profileContent = await profileResponse.Content.ReadAsStringAsync();
                Logger.Log($"Profile response status: {profileResponse.StatusCode}");

                if (profileResponse.IsSuccessStatusCode)
                {
                    Logger.Log("Successfully retrieved user profile");
                    _authData.Profile = JsonSerializer.Deserialize<UserProfile>(profileContent,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    LocalDatabase.Instance.SetValue(AUTH_KEY, _authData);
                }
                else
                {
                    Logger.Error($"Failed to fetch user profile: {profileContent}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching user profile: {ex.Message}");
            }
        }

        public void Logout()
        {
            Logger.Log("Logging out user");
            _authData = null;
            _httpClient.DefaultRequestHeaders.Authorization = null;
            _requires2FA = false;
            _pendingUsername = null;
            _pendingPassword = null;

            if (LocalDatabase.Instance.HasKey(AUTH_KEY))
            {
                LocalDatabase.Instance.DeleteValue(AUTH_KEY);
            }

            OnLoginStatusChanged?.Invoke(false);
        }

        public bool IsTokenExpired()
        {
            if (_authData == null) return true;
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return currentTime > _authData.TokenExpiry;
        }
    }
}
