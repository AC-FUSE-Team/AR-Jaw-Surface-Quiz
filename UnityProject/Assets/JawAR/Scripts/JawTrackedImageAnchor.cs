using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BMC.JawAR
{
    /// <summary>
    /// Places a marker-relative jaw model directly on an AR Foundation tracked image.
    /// The imported OBJ is already centered on the physical marker and expressed in metres.
    /// </summary>
    public sealed class JawTrackedImageAnchor : MonoBehaviour
    {
        [Header("AR references")]
        public ARTrackedImageManager trackedImageManager;
        public Transform jawAnchorRoot;
        public Text statusText;
        public string expectedReferenceImageName = "JawAruco5x5Id1";

        [Header("Tracking behaviour")]
        public bool smoothTrackedPose = true;
        [Range(1f, 40f)] public float positionSharpness = 18f;
        [Range(1f, 40f)] public float rotationSharpness = 18f;
        public bool keepLastPoseWhenTrackingLost = true;
        public bool hideUntilFirstDetection = true;

        public bool IsTracking { get; private set; }
        public bool HasEverDetectedMarker { get; private set; }
        public TrackingState LastTrackingState { get; private set; } = TrackingState.None;

        private Pose targetPose;
        private bool hasTargetPose;

        private void Awake()
        {
            ResolveReferences();
            if (jawAnchorRoot != null && hideUntilFirstDetection)
            {
                jawAnchorRoot.gameObject.SetActive(false);
            }
            SetStatus("POINT CAMERA AT JAW MARKER");
        }

        private void OnEnable()
        {
            ResolveReferences();
            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
            }
        }

        private void OnDisable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            }
        }

        private void Update()
        {
            if (!hasTargetPose || jawAnchorRoot == null || !IsTracking)
            {
                return;
            }

            if (!smoothTrackedPose)
            {
                jawAnchorRoot.SetPositionAndRotation(targetPose.position, targetPose.rotation);
                return;
            }

            var positionT = 1f - Mathf.Exp(-positionSharpness * Time.unscaledDeltaTime);
            var rotationT = 1f - Mathf.Exp(-rotationSharpness * Time.unscaledDeltaTime);
            jawAnchorRoot.position = Vector3.Lerp(jawAnchorRoot.position, targetPose.position, positionT);
            jawAnchorRoot.rotation = Quaternion.Slerp(jawAnchorRoot.rotation, targetPose.rotation, rotationT);
        }

        private void ResolveReferences()
        {
            if (trackedImageManager == null)
            {
                trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();
            }
        }

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            ProcessImages(args.added);
            ProcessImages(args.updated);

            foreach (var removed in args.removed)
            {
                var image = removed.Value;
                if (image != null && image.referenceImage.name == expectedReferenceImageName)
                {
                    HandleTrackingLost(TrackingState.None);
                }
            }
        }

        private void ProcessImages(IEnumerable<ARTrackedImage> images)
        {
            foreach (var image in images)
            {
                if (image == null || image.referenceImage.name != expectedReferenceImageName)
                {
                    continue;
                }

                LastTrackingState = image.trackingState;
                if (image.trackingState != TrackingState.Tracking)
                {
                    HandleTrackingLost(image.trackingState);
                    continue;
                }

                targetPose = new Pose(image.transform.position, image.transform.rotation);
                hasTargetPose = true;
                IsTracking = true;

                if (!HasEverDetectedMarker)
                {
                    HasEverDetectedMarker = true;
                    if (jawAnchorRoot != null)
                    {
                        jawAnchorRoot.gameObject.SetActive(true);
                        jawAnchorRoot.SetPositionAndRotation(targetPose.position, targetPose.rotation);
                    }
                }

                SetStatus("JAW MARKER TRACKING");
            }
        }

        private void HandleTrackingLost(TrackingState state)
        {
            LastTrackingState = state;
            IsTracking = false;
            if (!keepLastPoseWhenTrackingLost && jawAnchorRoot != null)
            {
                jawAnchorRoot.gameObject.SetActive(false);
            }
            SetStatus(HasEverDetectedMarker
                ? "MARKER LOST — HOLDING LAST JAW POSE"
                : "POINT CAMERA AT JAW MARKER");
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }
    }
}
