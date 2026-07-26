using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>Non-overwriting v36 build using the proven v34/v35 Android/proxy pipeline.</summary>
    public static class JawQuizV36InputUsabilityFixAndroidBuild
    {
        public const string OutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_v36_InputUsabilityFix.apk";
        public const string VersionName = "1.7.0-input-usability-fix";
        public const int VersionCode = 36;

        [MenuItem("Tools/Jaw Anatomy Quiz/Build v36 Input Usability Fix APK")]
        public static void Build()
        {
            if (File.Exists(OutputPath))
                throw new IOException("Safety stop: output APK already exists and will not be overwritten: " + OutputPath);
            var proxyUrl = Environment.GetEnvironmentVariable("QUIZ_PROXY_URL") ??
                           JawQuizAndroidTestBuild.PhoneProxyDefaultUrl;
            var token = Environment.GetEnvironmentVariable("QUIZ_PROXY_TOKEN") ?? string.Empty;
            JawQuizAndroidTestBuild.ValidatePhoneProxyBuildConfiguration(proxyUrl, token);
            var method = typeof(JawQuizAndroidTestBuild).GetMethod("BuildInternal",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException("JawQuizAndroidTestBuild.BuildInternal");
            method.Invoke(null, new object[]
            {
                OutputPath, VersionName, VersionCode, proxyUrl, token,
                JawQuizAndroidTestBuild.PhoneProxyProductName
            });
        }

        public static void BuildAndExit()
        {
            try { Build(); }
            catch (TargetInvocationException exception)
            {
                Debug.LogException(exception.InnerException ?? exception);
                EditorApplication.Exit(1);
                return;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }
    }
}
