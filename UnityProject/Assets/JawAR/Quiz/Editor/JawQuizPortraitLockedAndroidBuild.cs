using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>
    /// Builds v34 with the v33 Material 3/Backboard pipeline and fixed upright portrait.
    /// </summary>
    public static class JawQuizPortraitLockedAndroidBuild
    {
        public const string OutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_BackboardProxy_v34_Material3UI_PortraitLocked.apk";
        public const string VersionName = "1.5.1-quiz-proxy-material3-ui-portrait-locked";
        public const int VersionCode = 34;

        [MenuItem("Tools/Jaw Anatomy Quiz/Material 3/Build Portrait-Locked v34 APK")]
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
