using System;
using System.Collections.Generic;
using System.Linq;

namespace BMC.JawAR.Quiz
{
    public enum JawQuizState
    {
        Idle,
        QuestionPresented,
        AwaitingSelection,
        Evaluating,
        ShowingCorrectFeedback,
        ShowingIncorrectFeedback,
        ShowingHint,
        QuestionComplete,
        SessionComplete,
        OfflineQueued
    }

    public enum JawQuizSelectionKind
    {
        Correct,
        Incorrect,
        Unlabelled,
        Ignored
    }

    public readonly struct JawQuizEvaluation
    {
        public readonly JawQuizSelectionKind Kind;
        public readonly string SelectedRegionId;
        public readonly string ExpectedRegionId;
        public readonly int AttemptNumber;
        public readonly float ResponseSeconds;

        public JawQuizEvaluation(JawQuizSelectionKind kind, string selected, string expected,
            int attempt, float responseSeconds)
        {
            Kind = kind;
            SelectedRegionId = selected ?? string.Empty;
            ExpectedRegionId = expected ?? string.Empty;
            AttemptNumber = attempt;
            ResponseSeconds = responseSeconds;
        }
    }

    /// <summary>Pure, deterministic grading and transition logic. No network or LLM participates.</summary>
    public sealed class JawQuizEngine
    {
        private readonly List<JawQuizQuestionDefinition> questions;
        private readonly int maxAttempts;
        private int questionIndex = -1;

        public JawQuizState State { get; private set; } = JawQuizState.Idle;
        public JawQuizQuestionDefinition CurrentQuestion =>
            questionIndex >= 0 && questionIndex < questions.Count ? questions[questionIndex] : null;
        public int QuestionNumber => questionIndex + 1;
        public int QuestionCount => questions.Count;
        public int AttemptNumber { get; private set; }
        public int HintLevel { get; private set; }
        public int MaxAttempts => maxAttempts;
        public bool CanRetry => State == JawQuizState.ShowingIncorrectFeedback && AttemptNumber < maxAttempts;

        public JawQuizEngine(IEnumerable<JawQuizQuestionDefinition> source, int attemptsPerQuestion = 3)
        {
            questions = source?.Where(question => question != null && question.Enabled).ToList()
                        ?? new List<JawQuizQuestionDefinition>();
            maxAttempts = Math.Max(1, attemptsPerQuestion);
        }

        public bool StartQuiz()
        {
            if (questions.Count == 0) return false;
            questionIndex = 0;
            PresentCurrentQuestion();
            return true;
        }

        public void ConfirmQuestionPresented()
        {
            if (State == JawQuizState.QuestionPresented) State = JawQuizState.AwaitingSelection;
        }

        public JawQuizEvaluation EvaluateSelection(string selectedRegionId, float responseSeconds)
        {
            if (State != JawQuizState.AwaitingSelection || CurrentQuestion == null)
                return new JawQuizEvaluation(JawQuizSelectionKind.Ignored, selectedRegionId,
                    CurrentQuestion?.ExpectedRegionId, AttemptNumber, responseSeconds);

            State = JawQuizState.Evaluating;
            if (string.IsNullOrWhiteSpace(selectedRegionId))
            {
                State = JawQuizState.AwaitingSelection;
                return new JawQuizEvaluation(JawQuizSelectionKind.Unlabelled, string.Empty,
                    CurrentQuestion.ExpectedRegionId, AttemptNumber, responseSeconds);
            }

            AttemptNumber++;
            var correct = string.Equals(selectedRegionId, CurrentQuestion.ExpectedRegionId,
                StringComparison.Ordinal);
            State = correct ? JawQuizState.ShowingCorrectFeedback : JawQuizState.ShowingIncorrectFeedback;
            return new JawQuizEvaluation(correct ? JawQuizSelectionKind.Correct : JawQuizSelectionKind.Incorrect,
                selectedRegionId, CurrentQuestion.ExpectedRegionId, AttemptNumber, responseSeconds);
        }

        public bool Retry()
        {
            if (!CanRetry) return false;
            State = JawQuizState.AwaitingSelection;
            return true;
        }

        public string RequestHint()
        {
            if (CurrentQuestion == null || State == JawQuizState.Idle || State == JawQuizState.SessionComplete)
                return string.Empty;
            HintLevel = Math.Min(2, HintLevel + 1);
            State = JawQuizState.ShowingHint;
            return HintLevel == 1 ? CurrentQuestion.FirstHint : CurrentQuestion.SecondHint;
        }

        public void ResumeAfterHint()
        {
            if (State == JawQuizState.ShowingHint) State = JawQuizState.AwaitingSelection;
        }

        public void CompleteCurrentQuestion()
        {
            if (State == JawQuizState.ShowingCorrectFeedback ||
                (State == JawQuizState.ShowingIncorrectFeedback && AttemptNumber >= maxAttempts))
                State = JawQuizState.QuestionComplete;
        }

        public void SkipCurrentQuestion()
        {
            if (CurrentQuestion != null && State != JawQuizState.SessionComplete)
                State = JawQuizState.QuestionComplete;
        }

        public bool NextQuestion()
        {
            if (State != JawQuizState.QuestionComplete) return false;
            questionIndex++;
            if (questionIndex >= questions.Count)
            {
                State = JawQuizState.SessionComplete;
                return false;
            }
            PresentCurrentQuestion();
            return true;
        }

        private void PresentCurrentQuestion()
        {
            AttemptNumber = 0;
            HintLevel = 0;
            State = JawQuizState.QuestionPresented;
        }
    }
}
