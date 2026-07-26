package com.omar.jawaruco;

import java.util.ArrayList;
import java.util.List;

import org.opencv.android.OpenCVLoader;
import org.opencv.calib3d.Calib3d;
import org.opencv.core.CvType;
import org.opencv.core.Mat;
import org.opencv.core.MatOfDouble;
import org.opencv.core.MatOfPoint2f;
import org.opencv.core.MatOfPoint3f;
import org.opencv.core.Point;
import org.opencv.core.Point3;
import org.opencv.objdetect.ArucoDetector;
import org.opencv.objdetect.DetectorParameters;
import org.opencv.objdetect.Dictionary;
import org.opencv.objdetect.Objdetect;

/** Small Unity-facing bridge for the physical DICT_5X5_50 marker, ID 1. */
public final class JawArucoBridge {
    private static boolean initialized;
    private static ArucoDetector detector;

    private JawArucoBridge() { }

    public static synchronized boolean initialize() {
        if (initialized) return true;
        try {
            initialized = OpenCVLoader.initLocal();
            if (!initialized) return false;
            Dictionary dictionary = Objdetect.getPredefinedDictionary(Objdetect.DICT_5X5_50);
            DetectorParameters parameters = new DetectorParameters();
            // Default corner detection only locates corners to the nearest whole pixel, which is
            // the dominant source of solvePnP jitter/bias for a marker this size. Sub-pixel
            // refinement meaningfully tightens the pose the jaw is anchored to.
            parameters.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
            detector = new ArucoDetector(dictionary, parameters);
            return true;
        } catch (Throwable error) {
            android.util.Log.e("JawArucoBridge", "OpenCV initialization failed", error);
            initialized = false;
            return false;
        }
    }

    /**
     * Returns [tx,ty,tz,r00..r22,c0x,c0y..c3x,c3y], or an empty array.
     * Translation and rotation express marker coordinates in the OpenCV camera frame.
     */
    public static synchronized float[] detectPose(
            byte[] gray, int width, int height,
            double fx, double fy, double cx, double cy,
            double markerSizeMeters, int wantedId) {
        if (!initialize() || gray == null || gray.length < width * height) return new float[0];

        Mat image = new Mat(height, width, CvType.CV_8UC1);
        Mat ids = new Mat();
        List<Mat> corners = new ArrayList<>();
        Mat cameraMatrix = Mat.eye(3, 3, CvType.CV_64F);
        MatOfDouble distortion = new MatOfDouble();
        Mat rvec = new Mat();
        Mat tvec = new Mat();
        Mat rotation = new Mat();
        MatOfPoint3f objectPoints = null;
        MatOfPoint2f imagePoints = null;

        try {
            image.put(0, 0, gray);
            detector.detectMarkers(image, corners, ids);
            if (ids.empty()) return new float[0];

            int match = -1;
            for (int row = 0; row < ids.rows(); row++) {
                if ((int) ids.get(row, 0)[0] == wantedId) {
                    match = row;
                    break;
                }
            }
            if (match < 0 || match >= corners.size()) return new float[0];

            Point[] detected = new MatOfPoint2f(corners.get(match)).toArray();
            if (detected.length != 4) return new float[0];

            double half = markerSizeMeters * 0.5;
            // OpenCV reports TL, TR, BR, BL. Marker +Y points toward its printed top/jaw.
            objectPoints = new MatOfPoint3f(
                    new Point3(-half, half, 0.0),
                    new Point3(half, half, 0.0),
                    new Point3(half, -half, 0.0),
                    new Point3(-half, -half, 0.0));
            imagePoints = new MatOfPoint2f(detected);

            cameraMatrix.put(0, 0, fx);
            cameraMatrix.put(0, 1, 0.0);
            cameraMatrix.put(0, 2, cx);
            cameraMatrix.put(1, 0, 0.0);
            cameraMatrix.put(1, 1, fy);
            cameraMatrix.put(1, 2, cy);
            cameraMatrix.put(2, 0, 0.0);
            cameraMatrix.put(2, 1, 0.0);
            cameraMatrix.put(2, 2, 1.0);

            boolean solved = Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix,
                    distortion, rvec, tvec, false, Calib3d.SOLVEPNP_IPPE_SQUARE);
            if (!solved) return new float[0];
            Calib3d.Rodrigues(rvec, rotation);

            float[] result = new float[20];
            result[0] = (float) tvec.get(0, 0)[0];
            result[1] = (float) tvec.get(1, 0)[0];
            result[2] = (float) tvec.get(2, 0)[0];
            int output = 3;
            for (int row = 0; row < 3; row++) {
                for (int col = 0; col < 3; col++) {
                    result[output++] = (float) rotation.get(row, col)[0];
                }
            }
            for (Point point : detected) {
                result[output++] = (float) point.x;
                result[output++] = (float) point.y;
            }
            return result;
        } catch (Throwable error) {
            android.util.Log.e("JawArucoBridge", "Marker detection failed", error);
            return new float[0];
        } finally {
            image.release();
            ids.release();
            for (Mat corner : corners) corner.release();
            cameraMatrix.release();
            distortion.release();
            rvec.release();
            tvec.release();
            rotation.release();
            if (objectPoints != null) objectPoints.release();
            if (imagePoints != null) imagePoints.release();
        }
    }
}
