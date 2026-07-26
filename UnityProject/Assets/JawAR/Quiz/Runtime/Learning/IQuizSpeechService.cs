using System;
using UnityEngine;

namespace BMC.JawAR.Quiz.Learning
{
    public interface IQuizSpeechService : IDisposable
    {
        bool Muted { get; set; }
        void Speak(string text);
        void Stop();
    }

    public sealed class QuizLoggingSpeechService : IQuizSpeechService
    {
        public bool Muted { get; set; }
        public string LastText { get; private set; } = string.Empty;
        public void Speak(string text)
        {
            if (Muted || string.IsNullOrWhiteSpace(text)) return;
            LastText = text;
            Debug.Log("JAW_QUIZ_TTS " + text);
        }
        public void Stop() { }
        public void Dispose() { }
    }

    public sealed class AndroidQuizTextToSpeechService : IQuizSpeechService
    {
        private const string Bridge = "com.omar.jawaruco.JawQuizTtsBridge";
        private AndroidJavaClass bridge;
        private bool muted;
        public bool Muted
        {
            get => muted;
            set { muted = value; if (muted) Stop(); }
        }

        public AndroidQuizTextToSpeechService()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bridge = new AndroidJavaClass(Bridge);
            bridge.CallStatic("initialize");
#endif
        }
        public void Speak(string text)
        {
            if (muted || string.IsNullOrWhiteSpace(text)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridge?.CallStatic("speak", text.Length > 600 ? text.Substring(0, 600) : text);
#endif
        }
        public void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bridge?.CallStatic("stop");
#endif
        }
        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bridge?.CallStatic("shutdown");
            bridge?.Dispose();
            bridge = null;
#endif
        }
    }

    public static class QuizSpeechFactory
    {
        public static IQuizSpeechService Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidQuizTextToSpeechService();
#else
            return new QuizLoggingSpeechService();
#endif
        }
    }
}
