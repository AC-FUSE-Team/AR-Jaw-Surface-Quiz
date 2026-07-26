using System.IO;
using System.Linq;
using BMC.JawAR.Quiz.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace BMC.JawAR.Quiz.Tests
{
    /// <summary>
    /// Regression coverage for the jaw quiz's fixed upright portrait contract.
    /// </summary>
    public sealed class JawQuizAndroidTestBuildOrientationTests
    {
        [Test]
        public void ConfigureTemporaryBuildSettings_UsesOnlyUprightPortrait()
        {
            var originalOrientation = PlayerSettings.defaultInterfaceOrientation;
            var originalPortrait = PlayerSettings.allowedAutorotateToPortrait;
            var originalPortraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
            var originalLandscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
            var originalLandscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;
            var originalProductName = PlayerSettings.productName;
            var originalVersionName = PlayerSettings.bundleVersion;
            var originalVersionCode = PlayerSettings.Android.bundleVersionCode;
            var originalPackageId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            var originalScenes = EditorBuildSettings.scenes;

            try
            {
                JawQuizAndroidTestBuild.ConfigureTemporaryBuildSettings();

                Assert.AreEqual(UIOrientation.Portrait, PlayerSettings.defaultInterfaceOrientation);
                Assert.IsTrue(PlayerSettings.allowedAutorotateToPortrait);
                Assert.IsFalse(PlayerSettings.allowedAutorotateToPortraitUpsideDown);
                Assert.IsFalse(PlayerSettings.allowedAutorotateToLandscapeLeft);
                Assert.IsFalse(PlayerSettings.allowedAutorotateToLandscapeRight);

                Assert.DoesNotThrow(() => JawQuizAndroidTestBuild.ValidateOrientationIsPortraitLocked());
            }
            finally
            {
                PlayerSettings.defaultInterfaceOrientation = originalOrientation;
                PlayerSettings.allowedAutorotateToPortrait = originalPortrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = originalPortraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = originalLandscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = originalLandscapeRight;
                PlayerSettings.productName = originalProductName;
                PlayerSettings.bundleVersion = originalVersionName;
                PlayerSettings.Android.bundleVersionCode = originalVersionCode;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, originalPackageId);
                EditorBuildSettings.scenes = originalScenes;
            }
        }

        [Test]
        public void ValidateOrientationIsPortraitLocked_RejectsAnyLandscapeDirection()
        {
            var originalOrientation = PlayerSettings.defaultInterfaceOrientation;
            var originalPortrait = PlayerSettings.allowedAutorotateToPortrait;
            var originalPortraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
            var originalLandscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
            var originalLandscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;

            try
            {
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                PlayerSettings.allowedAutorotateToPortrait = true;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                PlayerSettings.allowedAutorotateToLandscapeRight = false;

                Assert.Throws<System.InvalidOperationException>(
                    () => JawQuizAndroidTestBuild.ValidateOrientationIsPortraitLocked());
            }
            finally
            {
                PlayerSettings.defaultInterfaceOrientation = originalOrientation;
                PlayerSettings.allowedAutorotateToPortrait = originalPortrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = originalPortraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = originalLandscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = originalLandscapeRight;
            }
        }

        [Test]
        public void ProjectPlayerSettings_ArePersistedAsOnlyUprightPortrait()
        {
            Assert.AreEqual(UIOrientation.Portrait, PlayerSettings.defaultInterfaceOrientation);
            Assert.IsTrue(PlayerSettings.allowedAutorotateToPortrait);
            Assert.IsFalse(PlayerSettings.allowedAutorotateToPortraitUpsideDown);
            Assert.IsFalse(PlayerSettings.allowedAutorotateToLandscapeLeft);
            Assert.IsFalse(PlayerSettings.allowedAutorotateToLandscapeRight);
        }

        [Test]
        public void AndroidManifest_PinsUnityActivityToPortrait()
        {
            var manifest = File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml");
            StringAssert.Contains("android:screenOrientation=\"portrait\"", manifest);
            StringAssert.DoesNotContain("fullUser", manifest);
            StringAssert.DoesNotContain("sensor", manifest);
        }

        [Test]
        public void RuntimeAndAndroidBridges_DoNotRequestLandscape()
        {
            var sourceFiles = Directory.GetFiles("Assets/JawAR/Quiz/Runtime", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Assets/Plugins/Android", "*.java", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("Assets/Plugins/Android", "*.kt", SearchOption.AllDirectories));
            foreach (var path in sourceFiles)
            {
                var source = File.ReadAllText(path);
                Assert.IsFalse(source.Contains("Screen.orientation = ScreenOrientation.Landscape"),
                    path + " requests a landscape Screen.orientation.");
                Assert.IsFalse(source.Contains("Screen.autorotateToLandscapeLeft = true"),
                    path + " enables landscape-left autorotation.");
                Assert.IsFalse(source.Contains("Screen.autorotateToLandscapeRight = true"),
                    path + " enables landscape-right autorotation.");
                Assert.IsFalse(source.Contains("Screen.autorotateToPortraitUpsideDown = true"),
                    path + " enables reverse-portrait autorotation.");
                Assert.IsFalse(source.Contains("setRequestedOrientation("),
                    path + " contains an Android requested-orientation override.");
            }
        }

        [Test]
        public void ValidatePhoneProxyBuildConfiguration_AcceptsPrivateLanEndpoint()
        {
            Assert.DoesNotThrow(() => JawQuizAndroidTestBuild.ValidatePhoneProxyBuildConfiguration(
                "http://192.168.2.244:8765", new string('x', 48)));
        }

        [TestCase("http://127.0.0.1:8765")]
        [TestCase("http://0.0.0.0:8765")]
        [TestCase("https://10.70.221.178:8765")]
        [TestCase("http://10.70.221.178:8080")]
        public void ValidatePhoneProxyBuildConfiguration_RejectsUnsafeEndpoint(string endpoint)
        {
            Assert.Throws<System.InvalidOperationException>(() =>
                JawQuizAndroidTestBuild.ValidatePhoneProxyBuildConfiguration(endpoint, new string('x', 48)));
        }

        [Test]
        public void ValidatePhoneProxyBuildConfiguration_RejectsShortToken()
        {
            Assert.Throws<System.InvalidOperationException>(() =>
                JawQuizAndroidTestBuild.ValidatePhoneProxyBuildConfiguration(
                    "http://10.70.221.178:8765", "replace-me"));
        }

        [Test]
        public void ConfigureTemporaryBuildSettings_PhoneProxyAllowsUnityCleartextHttp()
        {
            var original = PlayerSettings.insecureHttpOption;
            try
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
                JawQuizAndroidTestBuild.ConfigurePhoneProxyHttp();
                Assert.AreEqual(InsecureHttpOption.AlwaysAllowed,
                    PlayerSettings.insecureHttpOption);
            }
            finally
            {
                PlayerSettings.insecureHttpOption = original;
            }
        }
    }
}
