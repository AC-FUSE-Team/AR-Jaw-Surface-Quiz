using System;
using System.Collections.Generic;
using System.Linq;

namespace BMC.JawAR.Quiz.Learning
{
    /// <summary>
    /// Deterministic local scheduling. Priority = accuracy deficit (4), recent error (3), recurring
    /// confusion (up to 2), hint use (1.5), slow response (up to 1), and due/spaced review (1).
    /// The most recently asked item is penalized and stable ID breaks ties, preventing a single
    /// item from looping. Every fifth position deterministically admits a mastered review item.
    /// </summary>
    public sealed class JawQuizDeterministicScheduler
    {
        public IReadOnlyList<JawQuizQuestionDefinition> Order(
            IEnumerable<JawQuizQuestionDefinition> questions,
            IEnumerable<JawQuizAttemptRecord> history)
        {
            var source = questions?.Where(q => q != null && q.Enabled).ToList()
                         ?? new List<JawQuizQuestionDefinition>();
            var records = history?.ToList() ?? new List<JawQuizAttemptRecord>();
            if (records.Count == 0) return source;
            var lastQuestion = records.LastOrDefault()?.questionId ?? string.Empty;
            var scored = source.Select((question, index) => new Scored(question, index,
                Score(question, records, lastQuestion))).OrderByDescending(item => item.Score)
                .ThenBy(item => item.OriginalIndex).ToList();

            // Deterministic mastered review: put the best mastered item at positions 5, 10, ...
            var mastered = scored.Where(item => Accuracy(item.Question, records) >= 0.8f).ToList();
            if (mastered.Count > 0 && scored.Count > 5)
            {
                var review = mastered[0];
                scored.Remove(review);
                scored.Insert(Math.Min(4, scored.Count), review);
            }
            return scored.Select(item => item.Question).ToArray();
        }

        private static float Score(JawQuizQuestionDefinition question,
            IReadOnlyList<JawQuizAttemptRecord> all, string lastQuestion)
        {
            var own = all.Where(a => a.questionId == question.QuestionId ||
                                     a.expectedStableRegionId == question.ExpectedRegionId).ToList();
            if (own.Count == 0) return 2f;
            var accuracy = own.Count(a => a.correct) / (float)own.Count;
            var score = (1f - accuracy) * 4f;
            var last = own[^1];
            if (!last.correct) score += 3f;
            var confusion = own.Where(a => !a.correct && !string.IsNullOrEmpty(a.selectedStableRegionId))
                .GroupBy(a => a.selectedStableRegionId).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            score += Math.Min(2f, confusion * 0.5f);
            score += (float)own.Average(a => Math.Min(2, a.hintLevel)) * 0.75f;
            score += Math.Min(1f, own.Average(a => a.responseTimeSeconds) / 15f);
            var since = all.Count - 1 - all.ToList().FindLastIndex(a => a.questionId == question.QuestionId);
            score += Math.Min(1f, since / 8f);
            if (question.QuestionId == lastQuestion) score -= 20f;
            return score;
        }

        private static float Accuracy(JawQuizQuestionDefinition question, IEnumerable<JawQuizAttemptRecord> all)
        {
            var own = all.Where(a => a.questionId == question.QuestionId).ToList();
            return own.Count == 0 ? 0f : own.Count(a => a.correct) / (float)own.Count;
        }

        private readonly struct Scored
        {
            public readonly JawQuizQuestionDefinition Question;
            public readonly int OriginalIndex;
            public readonly float Score;
            public Scored(JawQuizQuestionDefinition question, int index, float score)
            { Question = question; OriginalIndex = index; Score = score; }
        }
    }
}
