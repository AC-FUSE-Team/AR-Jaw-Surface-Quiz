using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>
    /// Non-overwriting v37 build using the proven v34/v35/v36 Android/proxy pipeline. Carries
    /// the windowed-consensus post-lock drift-correction fix in JawOpenCvArucoTracker.cs (see
    /// Artifacts/JawFullPlaqueCalibrationDiagnostic_v3_SmartCorrection/) into the actual quiz app,
    /// after Omar physically confirmed on his Note 9 -- via the isolated diagnostic build, not
    /// this app -- that it visibly reduces the "jaw drifts away from the print" symptom. No scene
    /// or scene-builder change is needed: the new tracker fields (correctDriftAfterLock,
    /// postLockWindowSize, etc.) have defaults matching what was already tested, and Unity fills
    /// them in automatically for the existing serialized tracker component.
    /// </summary>
    public static class JawQuizV37PostLockDriftCorrectionAndroidBuild
    {
        public const string OutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_v37_PostLockDriftCorrection.apk";
        public const string VersionName = "1.8.0-post-lock-drift-correction";
        public const int VersionCode = 37;

        [MenuItem("Tools/Jaw Anatomy Quiz/Build v37 Post-Lock Drift Correction APK")]
        public static void Build()
        {
            if (File.Exists(OutputPath))
                throw new IOException("Safety stop: output APK already exists and will not be overwritten: " + OutputPath);
            var method = typeof(JawQuizAndroidTestBuild).GetMethod("BuildInternal",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException("JawQuizAndroidTestBuild.BuildInternal");
            method.Invoke(null, new object[]
            {
                OutputPath, VersionName, VersionCode, string.Empty, string.Empty,
                JawQuizAndroidTestBuild.ProductName
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
