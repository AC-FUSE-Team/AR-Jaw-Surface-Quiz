package com.omar.jawaruco;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.speech.tts.TextToSpeech;
import android.speech.tts.UtteranceProgressListener;

import com.unity3d.player.UnityPlayer;

import java.util.ArrayList;
import java.util.Locale;

/** Hands-free "what is that" recognition and spoken-answer bridge for Unity. */
public final class JawVoiceBridge {
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private static SpeechRecognizer recognizer;
    private static TextToSpeech tts;
    private static Intent recognizerIntent;
    private static volatile boolean initialized;
    private static volatile boolean listening;
    private static volatile boolean speaking;
    private static volatile boolean ttsReady;
    private static volatile int questionSequence;
    private static volatile String lastQuestion = "";
    private static volatile String lastError = "";
    private static long lastTriggerMs;
    private static String pendingSpeech;

    private JawVoiceBridge() { }

    public static boolean initialize() {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null || !SpeechRecognizer.isRecognitionAvailable(activity)) {
            lastError = "Speech recognition is not available on this phone.";
            return false;
        }
        MAIN.post(() -> setupOnMainThread(activity));
        return true;
    }

    private static synchronized void setupOnMainThread(Activity activity) {
        if (initialized && recognizer != null) return;
        try {
            recognizer = SpeechRecognizer.createSpeechRecognizer(activity);
            recognizer.setRecognitionListener(new RecognitionListener() {
                @Override public void onReadyForSpeech(Bundle params) {
                    listening = true;
                    lastError = "";
                }
                @Override public void onBeginningOfSpeech() { }
                @Override public void onRmsChanged(float rmsdB) { }
                @Override public void onBufferReceived(byte[] buffer) { }
                @Override public void onEndOfSpeech() {
                    listening = false;
                }
                @Override public void onError(int error) {
                    listening = false;
                    if (error == SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS) {
                        lastError = "Microphone permission is required.";
                        return;
                    }
                    if (error != SpeechRecognizer.ERROR_NO_MATCH &&
                            error != SpeechRecognizer.ERROR_SPEECH_TIMEOUT &&
                            error != SpeechRecognizer.ERROR_CLIENT) {
                        lastError = "Speech recognition error " + error;
                    }
                    scheduleRestart(850);
                }
                @Override public void onResults(Bundle results) {
                    listening = false;
                    publishQuestion(firstResult(results));
                    scheduleRestart(550);
                }
                @Override public void onPartialResults(Bundle partialResults) {
                    publishQuestion(firstResult(partialResults));
                }
                @Override public void onEvent(int eventType, Bundle params) { }
            });

            recognizerIntent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
            recognizerIntent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                    RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
            recognizerIntent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, Locale.getDefault());
            recognizerIntent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
            recognizerIntent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 3);
            recognizerIntent.putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE,
                    activity.getPackageName());

            tts = new TextToSpeech(activity.getApplicationContext(), status -> {
                if (status == TextToSpeech.SUCCESS) {
                    int language = tts.setLanguage(Locale.getDefault());
                    if (language == TextToSpeech.LANG_MISSING_DATA ||
                            language == TextToSpeech.LANG_NOT_SUPPORTED) {
                        tts.setLanguage(Locale.US);
                    }
                    tts.setOnUtteranceProgressListener(new UtteranceProgressListener() {
                        @Override public void onStart(String utteranceId) {
                            speaking = true;
                        }
                        @Override public void onDone(String utteranceId) {
                            speaking = false;
                            scheduleRestart(450);
                        }
                        @Override public void onError(String utteranceId) {
                            speaking = false;
                            lastError = "Text to speech failed.";
                            scheduleRestart(450);
                        }
                    });
                    ttsReady = true;
                    if (pendingSpeech != null) {
                        String text = pendingSpeech;
                        pendingSpeech = null;
                        speak(text);
                    }
                } else {
                    lastError = "Text to speech is unavailable.";
                }
            });

            initialized = true;
            lastError = "";
        } catch (Throwable error) {
            initialized = false;
            lastError = error.getClass().getSimpleName() + ": " + error.getMessage();
            android.util.Log.e("JawVoiceBridge", "Setup failed", error);
        }
    }

    public static void startListening() {
        MAIN.post(() -> {
            if (!initialized || recognizer == null || listening || speaking) return;
            try {
                recognizer.startListening(recognizerIntent);
                listening = true;
            } catch (Throwable error) {
                listening = false;
                lastError = error.getClass().getSimpleName() + ": " + error.getMessage();
            }
        });
    }

    public static void stopListening() {
        MAIN.post(() -> {
            if (recognizer == null) return;
            try {
                recognizer.cancel();
            } catch (Throwable ignored) { }
            listening = false;
        });
    }

    public static void speak(String text) {
        if (text == null || text.trim().isEmpty()) return;
        final String answer = text.trim();
        MAIN.post(() -> {
            stopListening();
            if (!ttsReady || tts == null) {
                pendingSpeech = answer;
                return;
            }
            speaking = true;
            tts.speak(answer, TextToSpeech.QUEUE_FLUSH, null,
                    "jaw_answer_" + System.currentTimeMillis());
        });
    }

    private static String firstResult(Bundle bundle) {
        if (bundle == null) return "";
        ArrayList<String> matches =
                bundle.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION);
        return matches == null || matches.isEmpty() ? "" : matches.get(0);
    }

    private static void publishQuestion(String phrase) {
        if (phrase == null) return;
        String normalized = phrase.toLowerCase(Locale.US).trim();
        boolean trigger = normalized.contains("what is that") ||
                normalized.contains("what's that") ||
                normalized.contains("what is this") ||
                normalized.contains("what's this");
        if (!trigger) return;

        long now = android.os.SystemClock.uptimeMillis();
        if (now - lastTriggerMs < 1800) return;
        lastTriggerMs = now;
        lastQuestion = phrase;
        questionSequence++;
        stopListening();
    }

    private static void scheduleRestart(long delayMs) {
        MAIN.postDelayed(() -> {
            if (!speaking) startListening();
        }, delayMs);
    }

    public static int getQuestionSequence() {
        return questionSequence;
    }

    public static String getLastQuestion() {
        return lastQuestion;
    }

    public static String getLastError() {
        return lastError;
    }

    public static boolean isListening() {
        return listening;
    }

    public static synchronized void shutdown() {
        MAIN.post(() -> {
            if (recognizer != null) {
                try {
                    recognizer.cancel();
                    recognizer.destroy();
                } catch (Throwable ignored) { }
                recognizer = null;
            }
            if (tts != null) {
                try {
                    tts.stop();
                    tts.shutdown();
                } catch (Throwable ignored) { }
                tts = null;
            }
            initialized = false;
            listening = false;
            speaking = false;
            ttsReady = false;
        });
    }
}
