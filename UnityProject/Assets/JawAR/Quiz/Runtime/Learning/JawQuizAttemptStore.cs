using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BMC.JawAR.Quiz.Learning
{
    /// <summary>Append-only, immediately flushed local authority for anonymized attempt events.</summary>
    public sealed class JawQuizAttemptStore
    {
        private readonly string directory;
        private readonly string attemptsPath;
        private readonly string statesPath;
        private readonly List<JawQuizAttemptRecord> attempts = new();
        private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);

        public string AttemptsPath => attemptsPath;
        public string StatesPath => statesPath;
        public IReadOnlyList<JawQuizAttemptRecord> Attempts => attempts;

        public JawQuizAttemptStore(string rootDirectory)
        {
            directory = Path.Combine(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)),
                "JawSurfaceQuizLearning");
            attemptsPath = Path.Combine(directory, "attempts.jsonl");
            statesPath = Path.Combine(directory, "attempt-sync.jsonl");
            Directory.CreateDirectory(directory);
            Recover();
        }

        public bool Append(JawQuizAttemptRecord record)
        {
            if (record == null || !Guid.TryParse(record.eventId, out _) ||
                string.IsNullOrWhiteSpace(record.studentId) || eventIds.Contains(record.eventId)) return false;
            record.synchronizationState = JawQuizSyncState.Pending;
            AppendAndFlush(attemptsPath, JsonUtility.ToJson(record));
            attempts.Add(record);
            eventIds.Add(record.eventId);
            return true;
        }

        public void MarkSynchronization(string eventId, string state, string responseReference = "")
        {
            var record = attempts.Find(item => item.eventId == eventId);
            if (record == null) return;
            var normalized = state == JawQuizSyncState.Synced ? JawQuizSyncState.Synced : JawQuizSyncState.Pending;
            var entry = new JawQuizSyncJournalEntry
            {
                eventId = eventId,
                state = normalized,
                responseReference = responseReference ?? string.Empty,
                utcTimestamp = DateTime.UtcNow.ToString("O")
            };
            AppendAndFlush(statesPath, JsonUtility.ToJson(entry));
            Apply(entry);
        }

        public IReadOnlyList<JawQuizAttemptRecord> Pending(string studentId = null)
        {
            return attempts.Where(item => item.synchronizationState != JawQuizSyncState.Synced &&
                (string.IsNullOrEmpty(studentId) || item.studentId == studentId)).ToArray();
        }

        public string ExportJson(string outputPath = null)
        {
            outputPath ??= Path.Combine(directory, "attempts-export.json");
            var json = JsonUtility.ToJson(new JawQuizAttemptArray { attempts = attempts.ToArray() }, true);
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            return outputPath;
        }

        public string ExportCsv(string outputPath = null)
        {
            outputPath ??= Path.Combine(directory, "attempts-export.csv");
            var builder = new StringBuilder();
            builder.AppendLine("event_id,student_id,session_id,question_id,object_id,region_map_version,expected_region_id,selected_region_id,correct,response_time_seconds,attempt_number,hint_level,utc_timestamp,synchronization_state,backboard_response_reference");
            foreach (var item in attempts)
            {
                var values = new[]
                {
                    item.eventId, item.studentId, item.sessionId, item.questionId, item.objectId,
                    item.regionMapVersion, item.expectedStableRegionId, item.selectedStableRegionId,
                    item.correct ? "true" : "false",
                    item.responseTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    item.attemptNumber.ToString(CultureInfo.InvariantCulture),
                    item.hintLevel.ToString(CultureInfo.InvariantCulture), item.utcTimestamp,
                    item.synchronizationState, item.backboardResponseReference
                };
                builder.AppendLine(string.Join(",", values.Select(Csv)));
            }
            File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(false));
            return outputPath;
        }

        private void Recover()
        {
            attempts.Clear();
            eventIds.Clear();
            foreach (var line in SafeLines(attemptsPath))
            {
                try
                {
                    var record = JsonUtility.FromJson<JawQuizAttemptRecord>(line);
                    if (record == null || !Guid.TryParse(record.eventId, out _) || !eventIds.Add(record.eventId))
                        continue;
                    record.synchronizationState = JawQuizSyncState.Pending;
                    attempts.Add(record);
                }
                catch (Exception) { /* A malformed/truncated record is ignored; prior records survive. */ }
            }
            foreach (var line in SafeLines(statesPath))
            {
                try { Apply(JsonUtility.FromJson<JawQuizSyncJournalEntry>(line)); }
                catch (Exception) { /* Same recovery rule for a truncated final status. */ }
            }
        }

        private void Apply(JawQuizSyncJournalEntry entry)
        {
            if (entry == null) return;
            var record = attempts.Find(item => item.eventId == entry.eventId);
            if (record == null) return;
            record.synchronizationState = entry.state == JawQuizSyncState.Synced
                ? JawQuizSyncState.Synced : JawQuizSyncState.Pending;
            record.backboardResponseReference = entry.responseReference ?? string.Empty;
        }

        private static IEnumerable<string> SafeLines(string path)
        {
            if (!File.Exists(path)) yield break;
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            string line;
            while ((line = reader.ReadLine()) != null)
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
        }

        private static void AppendAndFlush(string path, string json)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(true);
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
