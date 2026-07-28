using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Managers
{
    /// <summary>
    /// Manages usage reports for voice assistant sessions.
    /// Provides thread-safe access to cumulative token, media, and event usage.
    /// </summary>
    public sealed class UsageManager
    {
        private readonly List<UsageReport> _reports = new();
        private readonly object _lock = new();

        /// <summary>
        /// Gets the current usage reports as a read-only list.
        /// </summary>
        public IReadOnlyList<UsageReport> GetReports()
        {
            lock (_lock)
            {
                return _reports.AsReadOnly();
            }
        }

        /// <summary>
        /// Adds a new usage report to the history.
        /// </summary>
        public void AddReport(UsageReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            lock (_lock)
            {
                _reports.Add(report);
            }
        }

        /// <summary>
        /// Clears all usage reports from the history.
        /// </summary>
        public void ClearReports()
        {
            lock (_lock)
            {
                _reports.Clear();
            }
        }

        /// <summary>
        /// Gets the number of usage reports recorded.
        /// </summary>
        public int ReportCount
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Count;
                }
            }
        }

        /// <summary>
        /// Gets the total input tokens across all reports.
        /// </summary>
        public int TotalInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.TotalInputTokens);
                }
            }
        }

        /// <summary>
        /// Gets the total text input tokens across all reports.
        /// </summary>
        public int TextInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.InputTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total output tokens across all reports.
        /// </summary>
        public int TotalOutputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.TotalOutputTokens);
                }
            }
        }

        /// <summary>
        /// Gets the total text output tokens across all reports.
        /// </summary>
        public int TextOutputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.OutputTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total tokens (input + output) across all reports.
        /// </summary>
        public int TotalTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.TotalTokens);
                }
            }
        }

        /// <summary>
        /// Gets the total audio input tokens across all reports.
        /// </summary>
        public int TotalAudioInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.InputAudioTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total audio output tokens across all reports.
        /// </summary>
        public int TotalAudioOutputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.OutputAudioTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total cached input tokens across all reports.
        /// </summary>
        public int TotalCachedInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => (r.CacheCreationInputTokens ?? 0) + (r.CacheReadInputTokens ?? 0));
                }
            }
        }

        /// <summary>
        /// Gets the total input audio duration measured across all reports.
        /// </summary>
        public TimeSpan TotalInputAudioDuration
        {
            get
            {
                lock (_lock)
                {
                    return TimeSpan.FromTicks(_reports.Sum(r => r.InputAudioDuration?.Ticks ?? 0));
                }
            }
        }

        /// <summary>
        /// Gets the total output audio duration measured across all reports.
        /// </summary>
        public TimeSpan TotalOutputAudioDuration
        {
            get
            {
                lock (_lock)
                {
                    return TimeSpan.FromTicks(_reports.Sum(r => r.OutputAudioDuration?.Ticks ?? 0));
                }
            }
        }

        /// <summary>
        /// Gets the total number of billable text input events.
        /// </summary>
        public int TotalBillableTextInputEvents
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.BillableTextInputEvents ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total image input tokens across all reports.
        /// </summary>
        public int TotalImageInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.InputImageTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total image output tokens across all reports.
        /// </summary>
        public int TotalImageOutputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.OutputImageTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total input tokens attributed to tool use.
        /// </summary>
        public int TotalToolUseInputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.ToolUseInputTokens ?? 0);
                }
            }
        }

        /// <summary>
        /// Gets the total output tokens attributed to reasoning or thoughts.
        /// </summary>
        public int TotalReasoningOutputTokens
        {
            get
            {
                lock (_lock)
                {
                    return _reports.Sum(r => r.ReasoningOutputTokens ?? 0);
                }
            }
        }
    }
}
