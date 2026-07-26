using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizCompactUiAndroidBuild
    {
        public const string OutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_BackboardProxy_v32_CompactPortraitUI.apk";
        public const string VersionName = "1.4.4-quiz-proxy-compact-portrait-ui";
        public const int VersionCode = 32;

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Backboard Compact Portrait UI APK")]
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
