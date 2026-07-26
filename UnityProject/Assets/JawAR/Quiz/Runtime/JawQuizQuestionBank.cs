using System;
using System.Collections.Generic;
using UnityEngine;

namespace BMC.JawAR.Quiz
{
    public enum JawQuizDifficulty
    {
        Beginner,
        Intermediate,
        Advanced
    }

    [Serializable]
    public sealed class JawQuizQuestionDefinition
    {
        [SerializeField] private string questionId;
        [SerializeField] private string expectedRegionId;
        [SerializeField, TextArea(2, 4)] private string displayPrompt;
        [SerializeField, TextArea(2, 4)] private string spokenPrompt;
        [SerializeField, TextArea(1, 3)] private string correctFeedback;
        [SerializeField, TextArea(1, 3)] private string incorrectFeedback;
        [SerializeField, TextArea(1, 3)] private string firstHint;
        [SerializeField, TextArea(1, 3)] private string secondHint;
        [SerializeField, TextArea(2, 5)] private string educationalExplanation;
        [SerializeField] private JawQuizDifficulty difficulty = JawQuizDifficulty.Beginner;
        [SerializeField] private bool enabled = true;

        public string QuestionId => questionId;
        public string ExpectedRegionId => expectedRegionId;
        public string DisplayPrompt => displayPrompt;
        public string SpokenPrompt => spokenPrompt;
        public string CorrectFeedback => correctFeedback;
        public string IncorrectFeedback => incorrectFeedback;
        public string FirstHint => firstHint;
        public string SecondHint => secondHint;
        public string EducationalExplanation => educationalExplanation;
        public JawQuizDifficulty Difficulty => difficulty;
        public bool Enabled => enabled;

        public JawQuizQuestionDefinition(string id, string regionId, string prompt, string spoken,
            string correct, string incorrect, string hint1, string hint2, string explanation,
            JawQuizDifficulty level, bool isEnabled = true)
        {
            questionId = id;
            expectedRegionId = regionId;
            displayPrompt = prompt;
            spokenPrompt = spoken;
            correctFeedback = correct;
            incorrectFeedback = incorrect;
            firstHint = hint1;
            secondHint = hint2;
            educationalExplanation = explanation;
            difficulty = level;
            enabled = isEnabled;
        }
    }

    [CreateAssetMenu(fileName = "JawQuizQuestionBank", menuName = "Jaw Anatomy/Quiz Question Bank")]
    public sealed class JawQuizQuestionBank : ScriptableObject
    {
        [SerializeField] private string bankId = "jaw-surface-starter-v1";
        [SerializeField] private List<JawQuizQuestionDefinition> questions = new();

        public string BankId => bankId;
        public IReadOnlyList<JawQuizQuestionDefinition> Questions => questions;

#if UNITY_EDITOR
        public void SetEditorData(string id, IEnumerable<JawQuizQuestionDefinition> definitions)
        {
            bankId = id;
            questions = new List<JawQuizQuestionDefinition>(definitions);
        }
#endif
    }
}
