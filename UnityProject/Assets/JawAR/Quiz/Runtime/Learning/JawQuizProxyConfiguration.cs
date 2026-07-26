using System;
using System.Net;
using UnityEngine;

namespace BMC.JawAR.Quiz.Learning
{
    /// <summary>Stores only a non-secret, private-LAN proxy URL. Credentials are never persisted here.</summary>
    public static class JawQuizProxyConfiguration
    {
        public const string PlayerPrefsKey = "JawQuiz.ProxyBaseUrl.v1";

        public static bool TryValidatePrivateBaseUrl(string candidate, bool rejectLoopback,
            out string normalized, out string error)
        {
            normalized = string.Empty;
            error = "Invalid private address";
            if (string.IsNullOrWhiteSpace(candidate) ||
                !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                (uri.AbsolutePath != string.Empty && uri.AbsolutePath != "/") ||
                uri.Port < 1 || uri.Port > 65535 ||
                !IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            var bytes = address.GetAddressBytes();
            var privateAddress = bytes[0] == 10 ||
                                 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                                 (bytes[0] == 192 && bytes[1] == 168);
            if (!privateAddress || (rejectLoopback && bytes[0] == 127)) return false;

            normalized = $"http://{address}:{uri.Port}";
            error = string.Empty;
            return true;
        }

        public static string Load(string buildDefault, string key = PlayerPrefsKey)
        {
            var stored = PlayerPrefs.GetString(key, string.Empty);
            if (TryValidatePrivateBaseUrl(stored, true, out var normalized, out _)) return normalized;
            return TryValidatePrivateBaseUrl(buildDefault, true, out normalized, out _)
                ? normalized : buildDefault;
        }

        public static bool Save(string candidate, out string normalized, out string error,
            string key = PlayerPrefsKey)
        {
            if (!TryValidatePrivateBaseUrl(candidate, true, out normalized, out error)) return false;
            PlayerPrefs.SetString(key, normalized);
            PlayerPrefs.Save();
            return true;
        }

        public static string Reset(string buildDefault, string key = PlayerPrefsKey)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            return Load(buildDefault, key);
        }
    }
}
