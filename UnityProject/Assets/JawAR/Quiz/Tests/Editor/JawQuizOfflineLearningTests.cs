using System;
using System.IO;
using System.Linq;
using BMC.JawAR.Quiz.Learning;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizOfflineLearningTests
    {
        private string root;

        [SetUp] public void SetUp() => root = Path.Combine(Path.GetTempPath(), "jaw-quiz-tests-" + Guid.NewGuid());
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test]
        public void AppendImmediatelyPersistsAndRecoversAfterRestart()
        {
            var store = new JawQuizAttemptStore(root);
            var record = Attempt("student_001", "q1", "LeftRamus", "RightRamus", false);
            Assert.That(store.Append(record), Is.True);
            Assert.That(new FileInfo(store.AttemptsPath).Length, Is.GreaterThan(0));
            var recovered = new JawQuizAttemptStore(root);
            Assert.That(recovered.Attempts.Select(x => x.eventId), Contains.Item(record.eventId));
        }

        [Test]
        public void TruncatedFinalJsonlRecordDoesNotLoseEarlierAttempt()
        {
            var store = new JawQuizAttemptStore(root);
            var record = Attempt("student_001", "q1", "LeftRamus", "RightRamus", false);
            store.Append(record);
            File.AppendAllText(store.AttemptsPath, "{\"eventId\":\"truncated");
            var recovered = new JawQuizAttemptStore(root);
            Assert.That(recovered.Attempts.Count, Is.EqualTo(1));
            Assert.That(recovered.Attempts[0].eventId, Is.EqualTo(record.eventId));
        }

        [Test]
        public void DuplicateUuidIsRejected()
        {
            var store = new JawQuizAttemptStore(root);
            var record = Attempt("student_001", "q1", "LeftRamus", "RightRamus", false);
            Assert.That(store.Append(record), Is.True);
            Assert.That(store.Append(record), Is.False);
            Assert.That(store.Attempts.Count, Is.EqualTo(1));
        }

        [Test]
        public void OfflineQueueAndSynchronizationJournalRecover()
        {
            var store = new JawQuizAttemptStore(root);
            var first = Attempt("student_001", "q1", "LeftRamus", "RightRamus", false);
            var second = Attempt("student_001", "q2", "RightRamus", "RightRamus", true);
            store.Append(first); store.Append(second);
            store.MarkSynchronization(first.eventId, JawQuizSyncState.Synced, "mock-ref");
            var recovered = new JawQuizAttemptStore(root);
            Assert.That(recovered.Pending().Select(x => x.eventId), Is.EquivalentTo(new[] { second.eventId }));
            Assert.That(recovered.Attempts.Single(x => x.eventId == first.eventId).backboardResponseReference,
                Is.EqualTo("mock-ref"));
        }

        [Test]
        public void StudentAndSessionDataRemainIsolated()
        {
            var store = new JawQuizAttemptStore(root);
            store.Append(Attempt("student_001", "q1", "LeftRamus", "RightRamus", false));
            store.Append(Attempt("student_002", "q1", "LeftRamus", "LeftRamus", true));
            Assert.That(store.Pending("student_001").Count, Is.EqualTo(1));
            Assert.That(store.Pending("student_001")[0].studentId, Is.EqualTo("student_001"));
            Assert.That(store.Pending("student_002")[0].studentId, Is.EqualTo("student_002"));
        }

        [Test]
        public void ExportsContainStableIdsWithoutStudentNames()
        {
            var store = new JawQuizAttemptStore(root);
            store.Append(Attempt("student_001", "q1", "LeftMentalForamen", "MentalProtuberance", false));
            var json = File.ReadAllText(store.ExportJson());
            var csv = File.ReadAllText(store.ExportCsv());
            StringAssert.Contains("LeftMentalForamen", json);
            StringAssert.Contains("student_001", csv);
            StringAssert.DoesNotContain("realName", json + csv);
        }

        [Test]
        public void SchedulerIsDeterministicAndAvoidsImmediateRepeat()
        {
            var q1 = Question("q1", "LeftRamus");
            var q2 = Question("q2", "RightRamus");
            var history = new[] { Attempt("student_001", "q1", "LeftRamus", "RightRamus", false) };
            var scheduler = new JawQuizDeterministicScheduler();
            var first = scheduler.Order(new[] { q1, q2 }, history).Select(q => q.QuestionId).ToArray();
            var second = scheduler.Order(new[] { q1, q2 }, history).Select(q => q.QuestionId).ToArray();
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first[0], Is.EqualTo("q2"));
        }

        [Test]
        public void SpeechFallbackHonorsMute()
        {
            var speech = new QuizLoggingSpeechService();
            speech.Speak("first");
            speech.Muted = true;
            speech.Speak("should not replace");
            Assert.That(speech.LastText, Is.EqualTo("first"));
        }

        [Test]
        public void BackboardTimeoutUsesImmediateLocalExplanation()
        {
            Assert.That(JawQuizProxyClient.RemoteOrLocal(false, string.Empty, "Local anatomy hint"),
                Is.EqualTo("Local anatomy hint"));
        }

        [TestCase("http://192.168.2.244:8765", "http://192.168.2.244:8765")]
        [TestCase("http://10.4.5.6:9000", "http://10.4.5.6:9000")]
        [TestCase("http://172.16.0.1:1", "http://172.16.0.1:1")]
        [TestCase("http://172.31.255.254:65535/", "http://172.31.255.254:65535")]
        public void ProxyUrlAcceptsPrivateIpv4(string candidate, string expected)
        {
            Assert.That(JawQuizProxyConfiguration.TryValidatePrivateBaseUrl(candidate, true,
                out var normalized, out _), Is.True);
            Assert.That(normalized, Is.EqualTo(expected));
        }

        [TestCase("http://8.8.8.8:8765")]
        [TestCase("http://127.0.0.1:8765")]
        [TestCase("http://user:password@192.168.2.244:8765")]
        [TestCase("http://192.168.2.244:8765/api")]
        [TestCase("http://192.168.2.244:8765?debug=1")]
        [TestCase("http://192.168.2.244:8765/#fragment")]
        [TestCase("https://192.168.2.244:8765")]
        public void ProxyUrlRejectsUnsafeOrNonBaseAddress(string candidate)
        {
            Assert.That(JawQuizProxyConfiguration.TryValidatePrivateBaseUrl(candidate, true,
                out _, out _), Is.False);
        }

        [Test]
        public void ProxyUrlPersistsAndResetsToBuildDefault()
        {
            var key = "JawQuiz.ProxyTest." + Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(JawQuizProxyConfiguration.Save("http://10.1.2.3:8123",
                    out _, out _, key), Is.True);
                Assert.That(JawQuizProxyConfiguration.Load("http://192.168.2.244:8765", key),
                    Is.EqualTo("http://10.1.2.3:8123"));
                Assert.That(JawQuizProxyConfiguration.Reset("http://192.168.2.244:8765", key),
                    Is.EqualTo("http://192.168.2.244:8765"));
                Assert.That(PlayerPrefs.HasKey(key), Is.False);
            }
            finally { PlayerPrefs.DeleteKey(key); }
        }

        [Test]
        public void HealthTimeoutIsSanitized()
        {
            Assert.That(JawQuizProxyClient.ClassifyHealthResult(UnityWebRequest.Result.ConnectionError,
                0, "Request timed out"), Is.EqualTo(JawQuizProxyClient.HealthResult.TimedOut));
            Assert.That(JawQuizProxyClient.ClassifyHealthResult(UnityWebRequest.Result.ProtocolError,
                401, "raw error"), Is.EqualTo(JawQuizProxyClient.HealthResult.Unauthorized));
        }

        [Test]
        public void AllProxyClientRoutesUseSelectedBaseUrl()
        {
            var key = "JawQuiz.ProxyRouteTest." + Guid.NewGuid().ToString("N");
            try
            {
                JawQuizProxyConfiguration.Save("http://172.20.3.4:9876", out _, out _, key);
                var client = new JawQuizProxyClient
                    { BaseUrl = JawQuizProxyConfiguration.Load("http://192.168.2.244:8765", key) };
                Assert.That(client.BuildUrl("/api/v1/attempts"), Is.EqualTo("http://172.20.3.4:9876/api/v1/attempts"));
                Assert.That(client.BuildUrl("/api/v1/hints"), Is.EqualTo("http://172.20.3.4:9876/api/v1/hints"));
                Assert.That(client.BuildUrl("/api/v1/learning-events"), Is.EqualTo("http://172.20.3.4:9876/api/v1/learning-events"));
                Assert.That(client.BuildUrl("/api/v1/status"), Is.EqualTo("http://172.20.3.4:9876/api/v1/status"));
                Assert.That(client.BuildUrl("/health"), Is.EqualTo("http://172.20.3.4:9876/health"));
            }
            finally { PlayerPrefs.DeleteKey(key); }
        }

        [Test]
        public void MemoryPolicyDoesNotWriteForCorrectOrFirstIncorrectAttempt()
        {
            var correct = Attempt("student_001", "q1", "RightMentalForamen",
                "RightMentalForamen", true);
            var firstWrong = Attempt("student_001", "q1", "RightMentalForamen",
                "MentalProtuberance", false);
            Assert.That(JawQuizMemoryPolicy.Evaluate(correct, new[] { correct }).ShouldWrite, Is.False);
            Assert.That(JawQuizMemoryPolicy.Evaluate(firstWrong, new[] { firstWrong }).ShouldWrite, Is.False);
        }

        [Test]
        public void MemoryPolicyWritesOnceThresholdHasRecurringConfusionPair()
        {
            var first = Attempt("student_001", "q1", "RightMentalForamen",
                "MentalProtuberance", false);
            var second = Attempt("student_001", "q2", "RightMentalForamen",
                "MentalProtuberance", false);
            var decision = JawQuizMemoryPolicy.Evaluate(second, new[] { first, second });
            Assert.That(decision.ShouldWrite, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("recurring_confusion_pair"));
            Assert.That(decision.PolicyKey,
                Is.EqualTo("confusion:RightMentalForamen:MentalProtuberance"));
        }

        [Test]
        public void MemoryPolicyIgnoresOtherStudentsWhenCountingThresholds()
        {
            var other = Attempt("student_002", "q1", "RightMentalForamen",
                "MentalProtuberance", false);
            var current = Attempt("student_001", "q1", "RightMentalForamen",
                "MentalProtuberance", false);
            Assert.That(JawQuizMemoryPolicy.Evaluate(current, new[] { other, current }).ShouldWrite,
                Is.False);
        }

        [Test]
        public void SimulatedGradingRemainsDeterministicWhenAttemptIsQueuedOffline()
        {
            var engine = new JawQuizEngine(new[] { Question("q1", "LeftRamus") });
            Assert.That(engine.StartQuiz(), Is.True); engine.ConfirmQuestionPresented();
            var evaluation = engine.EvaluateSelection("LeftRamus", 2f);
            var store = new JawQuizAttemptStore(root);
            store.Append(Attempt("student_001", "q1", evaluation.ExpectedRegionId,
                evaluation.SelectedRegionId, evaluation.Kind == JawQuizSelectionKind.Correct));
            Assert.That(evaluation.Kind, Is.EqualTo(JawQuizSelectionKind.Correct));
            Assert.That(store.Pending().Count, Is.EqualTo(1));
        }

        private static JawQuizAttemptRecord Attempt(string student, string question, string expected,
            string selected, bool correct) => JawQuizAttemptRecord.Create(student, "session_test", question,
                "data-v1:test", expected, selected, correct, 8.4f, 2, 1);

        private static JawQuizQuestionDefinition Question(string id, string region) =>
            new(id, region, "Find " + region, "Find " + region, "Correct", "Try again",
                "Hint one", "Hint two", "Explanation", JawQuizDifficulty.Beginner);
    }
}
