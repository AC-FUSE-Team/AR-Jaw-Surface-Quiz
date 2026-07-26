using System;
using System.Collections;
using System.Reflection;
using BMC.JawAR.Quiz.Learning;
using NUnit.Framework;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizProxyClientHealthTests
    {
        private static IEnumerator CreateOperation(Func<object> begin,
            Func<JawQuizProxyClient.HealthResult> classify,
            Action<JawQuizProxyClient.HealthResult> complete)
        {
            var method = typeof(JawQuizProxyClient).GetMethod("CompleteHealthRequest",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (IEnumerator)method.Invoke(null, new object[]
                { begin, classify, complete, null });
        }

        [Test]
        public void HealthRequest_SynchronousStartException_CompletesUnavailable()
        {
            JawQuizProxyClient.HealthResult? result = null;
            var operation = CreateOperation(
                () => throw new InvalidOperationException("Insecure connection not allowed"),
                () => JawQuizProxyClient.HealthResult.Connected,
                value => result = value);

            Assert.False(operation.MoveNext());
            Assert.AreEqual(JawQuizProxyClient.HealthResult.Unavailable, result);
        }

        [Test]
        public void HealthRequest_Cancellation_CompletesCancelled()
        {
            JawQuizProxyClient.HealthResult? result = null;
            var operation = CreateOperation(() => new object(),
                () => JawQuizProxyClient.HealthResult.Connected,
                value => result = value);

            Assert.True(operation.MoveNext());
            ((IDisposable)operation).Dispose();
            Assert.AreEqual(JawQuizProxyClient.HealthResult.Cancelled, result);
        }

        [TestCase(JawQuizProxyClient.HealthResult.Connected)]
        [TestCase(JawQuizProxyClient.HealthResult.TimedOut)]
        [TestCase(JawQuizProxyClient.HealthResult.Unauthorized)]
        [TestCase(JawQuizProxyClient.HealthResult.Unavailable)]
        public void HealthRequest_CompletedPath_ReportsExactlyOneTerminalResult(
            JawQuizProxyClient.HealthResult expected)
        {
            var callbackCount = 0;
            JawQuizProxyClient.HealthResult? result = null;
            var operation = CreateOperation(() => new object(), () => expected, value =>
            {
                callbackCount++;
                result = value;
            });

            Assert.True(operation.MoveNext());
            Assert.False(operation.MoveNext());
            ((IDisposable)operation).Dispose();
            Assert.AreEqual(expected, result);
            Assert.AreEqual(1, callbackCount);
        }

        [Test]
        public void HealthRequest_ResponseClassificationException_CompletesUnavailable()
        {
            JawQuizProxyClient.HealthResult? result = null;
            var operation = CreateOperation(() => new object(),
                () => throw new FormatException("bad response"), value => result = value);

            Assert.True(operation.MoveNext());
            Assert.False(operation.MoveNext());
            Assert.AreEqual(JawQuizProxyClient.HealthResult.Unavailable, result);
        }
    }
}
