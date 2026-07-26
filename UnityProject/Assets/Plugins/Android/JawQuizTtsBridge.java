package com.omar.jawaruco;

import android.app.Activity;
import android.speech.tts.TextToSpeech;
import com.unity3d.player.UnityPlayer;
import java.util.Locale;

/** TextToSpeech-only bridge. It never creates or references SpeechRecognizer. */
public final class JawQuizTtsBridge {
    private static TextToSpeech tts;
    private static boolean ready;
    private static String pending;
    private JawQuizTtsBridge() { }

    public static synchronized void initialize() {
        if (tts != null) return;
        final Activity activity = UnityPlayer.currentActivity;
        if (activity == null) return;
        activity.runOnUiThread(() -> tts = new TextToSpeech(activity.getApplicationContext(), status -> {
            synchronized (JawQuizTtsBridge.class) {
                ready = status == TextToSpeech.SUCCESS;
                if (ready) tts.setLanguage(Locale.US);
                if (ready && pending != null) {
                    String text = pending;
                    pending = null;
                    speak(text);
                }
            }
        }));
    }

    public static synchronized void speak(String text) {
        if (text == null || text.trim().isEmpty()) return;
        if (tts == null) initialize();
        if (!ready || tts == null) { pending = text; return; }
        final String bounded = text.length() > 600 ? text.substring(0, 600) : text;
        tts.speak(bounded, TextToSpeech.QUEUE_FLUSH, null, "jaw-quiz-feedback");
    }

    public static synchronized void stop() {
        pending = null;
        if (tts != null) tts.stop();
    }

    public static synchronized void shutdown() {
        pending = null;
        ready = false;
        if (tts != null) { tts.stop(); tts.shutdown(); tts = null; }
    }
}
