using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BMC.JawAR.Quiz.Learning
{
    [Serializable] internal sealed class ProxyAttemptResponse
    { public bool accepted; public bool duplicate; public string eventId; public bool queuedForBackboard; }
    [Serializable] internal sealed class ProxyHintResponse
    { public string text; public string source; public string responseReference; public string[] suggestedReviewRegions; }
    [Serializable] internal sealed class ProxyLearningEventRequest
    { public string eventId, studentId; }
    [Serializable] internal sealed class ProxyLearningEventResponse
    { public bool accepted, duplicate; public string memoryAction, reason, source, text, responseReference; }
    [Serializable] internal sealed class ProxyStatusResponse
    { public bool proxyConnected, backboardAvailable; public string mode; public int queuedLearningEvents; }
    [Serializable] internal sealed class ProxyHealthResponse { public string status; }
    [Serializable] internal sealed class ProxyHintRequest
    {
        public string studentId, sessionId, questionId, expectedStableRegionId, selectedStableRegionId;
        public bool correct; public float responseTimeSeconds; public int attemptNumber, hintLevel;
    }

    /// <summary>Timeout-bounded, non-blocking client. It has no Backboard credential.</summary>
    public sealed class JawQuizProxyClient
    {
        public enum HealthResult
        {
            Connected, TimedOut, InvalidPrivateAddress, Unauthorized, Unavailable, Cancelled
        }
        public string BaseUrl { get; set; } = "http://127.0.0.1:8765";
        public string PrototypeToken { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 4;

        public string BuildUrl(string path) => BaseUrl.TrimEnd('/') + path;


        public static string RemoteOrLocal(bool remoteSucceeded, string remoteText, string localText)
        {
            return remoteSucceeded && !string.IsNullOrWhiteSpace(remoteText)
                ? remoteText
                : (string.IsNullOrWhiteSpace(localText) ? "Local feedback remains available." : localText);
        }
        public IEnumerator PostAttempt(JawQuizAttemptRecord record, Action<bool, string> complete)
        {
            yield return Post("/api/v1/attempts", JsonUtility.ToJson(record), TimeoutSeconds, (ok, json) =>
            {
                if (!ok) { complete?.Invoke(false, string.Empty); return; }
                try
                {
                    var response = JsonUtility.FromJson<ProxyAttemptResponse>(json);
                    complete?.Invoke(response != null && (response.accepted || response.duplicate), string.Empty);
                }
                catch (Exception) { complete?.Invoke(false, string.Empty); }
            });
        }

        public IEnumerator RequestHint(JawQuizAttemptRecord attempt, int hintLevel,
            Action<bool, string, string> complete)
        {
            var request = new ProxyHintRequest
            {
                studentId = attempt.studentId, sessionId = attempt.sessionId,
                questionId = attempt.questionId, expectedStableRegionId = attempt.expectedStableRegionId,
                selectedStableRegionId = string.IsNullOrEmpty(attempt.selectedStableRegionId)
                    ? "Unknown" : attempt.selectedStableRegionId,
                correct = attempt.correct, responseTimeSeconds = attempt.responseTimeSeconds,
                attemptNumber = attempt.attemptNumber, hintLevel = hintLevel
            };
            yield return Post("/api/v1/hints", JsonUtility.ToJson(request), TimeoutSeconds, (ok, json) =>
            {
                if (!ok) { complete?.Invoke(false, string.Empty, string.Empty); return; }
                try
                {
                    var response = JsonUtility.FromJson<ProxyHintResponse>(json);
                    complete?.Invoke(response != null && !string.IsNullOrWhiteSpace(response.text),
                        response?.text ?? string.Empty, response?.responseReference ?? string.Empty);
                }
                catch (Exception) { complete?.Invoke(false, string.Empty, string.Empty); }
            });
        }

        public IEnumerator PostLearningEvent(JawQuizAttemptRecord attempt,
            Action<bool, string, string> complete)
        {
            var body = JsonUtility.ToJson(new ProxyLearningEventRequest
                { eventId = attempt.eventId, studentId = attempt.studentId });
            yield return Post("/api/v1/learning-events", body, 30, (ok, json) =>
            {
                if (!ok) { complete?.Invoke(false, string.Empty, string.Empty); return; }
                try
                {
                    var response = JsonUtility.FromJson<ProxyLearningEventResponse>(json);
                    complete?.Invoke(response != null && response.accepted,
                        response?.text ?? string.Empty, response?.responseReference ?? string.Empty);
                }
                catch (Exception) { complete?.Invoke(false, string.Empty, string.Empty); }
            });
        }

        public IEnumerator CheckStatus(Action<bool, bool, string, int> complete)
        {
            using var request = UnityWebRequest.Get(BuildUrl("/api/v1/status"));
            if (!string.IsNullOrWhiteSpace(PrototypeToken))
                request.SetRequestHeader("X-Quiz-Token", PrototypeToken);
            request.timeout = Mathf.Clamp(TimeoutSeconds, 1, 15);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            { complete?.Invoke(false, false, string.Empty, 0); yield break; }
            try
            {
                var response = JsonUtility.FromJson<ProxyStatusResponse>(request.downloadHandler.text);
                complete?.Invoke(response != null && response.proxyConnected,
                    response != null && response.backboardAvailable, response?.mode ?? string.Empty,
                    response?.queuedLearningEvents ?? 0);
            }
            catch (Exception) { complete?.Invoke(false, false, string.Empty, 0); }
        }

        public IEnumerator CheckHealth(Action<HealthResult> complete)
        {
            if (!JawQuizProxyConfiguration.TryValidatePrivateBaseUrl(BaseUrl, true, out _, out _))
            {
                complete?.Invoke(HealthResult.InvalidPrivateAddress);
                return CompletedOperation();
            }
            var request = UnityWebRequest.Get(BuildUrl("/health"));
            request.timeout = Mathf.Clamp(TimeoutSeconds, 1, 5);
            return CompleteHealthRequest(
                () => request.SendWebRequest(),
                () => ClassifyHealthResult(request.result, request.responseCode, request.error,
                    request.downloadHandler?.text),
                complete,
                request);
        }

        private static IEnumerator CompletedOperation() { yield break; }

        // Unity can throw synchronously from SendWebRequest (for example when the player blocks
        // cleartext HTTP), before there is an async operation to yield. The finally block also
        // guarantees a terminal callback if Unity disposes a test while it is in flight.
        private static IEnumerator CompleteHealthRequest(Func<object> beginRequest,
            Func<HealthResult> classifyCompletedRequest, Action<HealthResult> complete,
            IDisposable requestLifetime)
        {
            var completionSent = false;
            Action<HealthResult> finish = result =>
            {
                if (completionSent) return;
                completionSent = true;
                complete?.Invoke(result);
            };

            using (requestLifetime)
            {
                try
                {
                    object pendingOperation = null;
                    Exception startException = null;
                    try
                    {
                        pendingOperation = beginRequest();
                    }
                    catch (Exception exception)
                    {
                        startException = exception;
                    }

                    if (startException != null)
                    {
                        Debug.LogWarning("Quiz proxy health request could not start: " +
                                         startException.Message);
                        finish(HealthResult.Unavailable);
                    }
                    else
                    {
                        yield return pendingOperation;
                        HealthResult result;
                        try
                        {
                            result = classifyCompletedRequest();
                        }
                        catch (Exception exception)
                        {
                            Debug.LogWarning("Quiz proxy health response could not be classified: " +
                                             exception.Message);
                            result = HealthResult.Unavailable;
                        }
                        finish(result);
                    }
                }
                finally
                {
                    if (!completionSent) finish(HealthResult.Cancelled);
                }
            }
        }

        public static HealthResult ClassifyHealthResult(UnityWebRequest.Result result, long status,
            string error, string responseBody = "")
        {
            if (status == 401 || status == 403) return HealthResult.Unauthorized;
            if (result == UnityWebRequest.Result.Success && status >= 200 && status < 300)
            {
                try
                {
                    var health = JsonUtility.FromJson<ProxyHealthResponse>(responseBody);
                    return health != null && string.Equals(health.status, "ok", StringComparison.OrdinalIgnoreCase)
                        ? HealthResult.Connected : HealthResult.Unavailable;
                }
                catch (Exception) { return HealthResult.Unavailable; }
            }
            var safeError = error ?? string.Empty;
            return safeError.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   safeError.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                ? HealthResult.TimedOut : HealthResult.Unavailable;
        }

        private IEnumerator Post(string path, string json, int timeoutSeconds, Action<bool, string> complete)
        {
            using var request = new UnityWebRequest(BuildUrl(path), UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrWhiteSpace(PrototypeToken)) request.SetRequestHeader("X-Quiz-Token", PrototypeToken);
            request.timeout = Mathf.Clamp(timeoutSeconds, 1, 60);
            yield return request.SendWebRequest();
            complete?.Invoke(request.result == UnityWebRequest.Result.Success,
                request.downloadHandler?.text ?? string.Empty);
        }
    }
}
