package com.omar.jawaruco;

import android.graphics.Bitmap;
import android.os.SystemClock;

import com.google.mediapipe.framework.image.BitmapImageBuilder;
import com.google.mediapipe.framework.image.MPImage;
import com.google.mediapipe.tasks.components.containers.NormalizedLandmark;
import com.google.mediapipe.tasks.core.BaseOptions;
import com.google.mediapipe.tasks.vision.core.RunningMode;
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarker;
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarkerResult;
import com.unity3d.player.UnityPlayer;

import org.opencv.android.Utils;
import org.opencv.core.CvType;
import org.opencv.core.Mat;

import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/** Asynchronous MediaPipe bridge that returns the latest portrait-image index fingertip. */
public final class JawHandLandmarkerBridge {
    private static final String MODEL_ASSET = "hand_landmarker.task";
    private static final ExecutorService EXECUTOR = Executors.newSingleThreadExecutor();
    private static final AtomicBoolean BUSY = new AtomicBoolean(false);

    private static HandLandmarker landmarker;
    private static volatile boolean initialized;
    private static volatile float[] latest = new float[0];
    private static volatile String lastError = "";
    private static long resultSequence;

    private JawHandLandmarkerBridge() { }

    public static synchronized boolean initialize() {
        if (initialized && landmarker != null) return true;
        try {
            BaseOptions baseOptions = BaseOptions.builder()
                    .setModelAssetPath(MODEL_ASSET)
                    .build();
            HandLandmarker.HandLandmarkerOptions options =
                    HandLandmarker.HandLandmarkerOptions.builder()
                            .setBaseOptions(baseOptions)
                            .setRunningMode(RunningMode.VIDEO)
                            .setNumHands(1)
                            .setMinHandDetectionConfidence(0.45f)
                            .setMinHandPresenceConfidence(0.45f)
                            .setMinTrackingConfidence(0.45f)
                            .build();
            landmarker = HandLandmarker.createFromOptions(
                    UnityPlayer.currentActivity.getApplicationContext(), options);
            initialized = true;
            lastError = "";
            return true;
        } catch (Throwable error) {
            initialized = false;
            lastError = error.getClass().getSimpleName() + ": " + error.getMessage();
            android.util.Log.e("JawHandLandmarker", "Initialization failed", error);
            return false;
        }
    }

    /**
     * Queues an RGBA portrait frame. A frame is dropped when inference is already busy.
     * Unity owns its input array; JNI supplies Java with a safe copy for the worker.
     */
    public static boolean submitRgbaFrame(byte[] rgba, int width, int height) {
        if (!initialize() || rgba == null || rgba.length < width * height * 4) return false;
        if (!BUSY.compareAndSet(false, true)) return false;

        EXECUTOR.execute(() -> {
            Mat rgbaMat = new Mat(height, width, CvType.CV_8UC4);
            Bitmap bitmap = null;
            MPImage mpImage = null;
            try {
                rgbaMat.put(0, 0, rgba);
                bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
                Utils.matToBitmap(rgbaMat, bitmap);
                mpImage = new BitmapImageBuilder(bitmap).build();

                HandLandmarkerResult result =
                        landmarker.detectForVideo(mpImage, SystemClock.uptimeMillis());
                List<List<NormalizedLandmark>> hands = result.landmarks();
                if (hands == null || hands.isEmpty() || hands.get(0).size() <= 8) {
                    latest = new float[0];
                    return;
                }

                List<NormalizedLandmark> points = hands.get(0);
                NormalizedLandmark tip = points.get(8);
                NormalizedLandmark pip = points.get(6);
                NormalizedLandmark wrist = points.get(0);
                latest = new float[] {
                        ++resultSequence,
                        tip.x(), tip.y(), tip.z(),
                        pip.x(), pip.y(),
                        wrist.x(), wrist.y()
                };
                lastError = "";
            } catch (Throwable error) {
                latest = new float[0];
                lastError = error.getClass().getSimpleName() + ": " + error.getMessage();
                android.util.Log.e("JawHandLandmarker", "Frame inference failed", error);
            } finally {
                if (mpImage != null) mpImage.close();
                if (bitmap != null) bitmap.recycle();
                rgbaMat.release();
                BUSY.set(false);
            }
        });
        return true;
    }

    /** [sequenceMs, tipX,tipY,tipZ,pipX,pipY,wristX,wristY], normalized in portrait image space. */
    public static float[] getLatestFingertip() {
        return latest;
    }

    public static String getLastError() {
        return lastError;
    }

    public static synchronized void shutdown() {
        latest = new float[0];
        if (landmarker != null) {
            try {
                landmarker.close();
            } catch (Throwable ignored) { }
            landmarker = null;
        }
        initialized = false;
    }
}
