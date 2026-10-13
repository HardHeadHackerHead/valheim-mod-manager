using System;
using System.Collections.Generic;
using System.Linq;

namespace ModUpdater
{
    /// <summary>
    /// When automatic updating checks GitHub and when it updates the next mod: the timing only, with no game or Unity in it, so it is tested
    /// on its own (tests/ModUpdater.Auto.Tests). The clock is any number of seconds that only grows (the game's unscaled time).
    ///
    ///   - A check is due every interval (at least 2 minutes). After GitHub says "too many requests" (403/429), checks stop and wait: what
    ///     GitHub asked for (Retry-After / X-RateLimit-Reset), or else twice the last wait, starting at twice the interval and never more than
    ///     an hour. A check that works ends the back-off.
    ///   - Mods to update wait in a queue and go one at a time, a gap apart (never two in the same second), the manager itself always last
    ///     (updating it reloads it, which would end the queue).
    /// </summary>
    internal sealed class AutoPlan
    {
        internal const double MinInterval = 120.0, MaxBackoff = 3600.0, MinGap = 5.0;

        private double _interval = 300.0, _gap = 20.0;
        private double _nextCheck = -1.0, _backoff, _nextStep;
        private readonly List<string> _queue = new List<string>();

        internal double Interval { get => _interval; set => _interval = Math.Max(MinInterval, value); }
        internal double Gap { get => _gap; set => _gap = Math.Max(MinGap, value); }
        /// <summary>How long checks are paused after GitHub's limit (0: not paused).</summary>
        internal double Backoff => _backoff;
        internal double NextCheck => _nextCheck;
        internal IReadOnlyList<string> Queue => _queue;

        /// <summary>The first check waits this long after the switch is turned on or the game starts (so it never lands on the loading screen).</summary>
        internal void Start(double now, double firstAfter = 30.0) { if (_nextCheck < 0) _nextCheck = now + firstAfter; }

        internal bool CheckDue(double now) => _nextCheck >= 0 && now >= _nextCheck;

        /// <summary>A check went through (whatever it found): the next one in one interval, and any back-off is over.</summary>
        internal void Checked(double now) { _backoff = 0.0; _nextCheck = now + _interval; }

        /// <summary>A check could not run for another reason (offline, busy): try again in one interval, keeping any back-off.</summary>
        internal void Skipped(double now) { _nextCheck = now + Math.Max(_interval, _backoff); }

        /// <summary>GitHub said too many requests. retryAfter: seconds it asked us to wait, if it said (0 or less: it did not).</summary>
        internal double RateLimited(double now, double retryAfter)
        {
            double doubled = _backoff <= 0 ? _interval * 2.0 : _backoff * 2.0;
            _backoff = Math.Min(MaxBackoff, Math.Max(doubled, retryAfter > 0 ? retryAfter : 0.0));
            _nextCheck = now + _backoff;
            return _backoff;
        }

        /// <summary>Adds mods to update (each once). The manager's own id goes last whatever order they come in.</summary>
        internal void Enqueue(IEnumerable<string> ids, string managerId, double now)
        {
            bool wasEmpty = _queue.Count == 0;
            foreach (string id in ids) if (!string.IsNullOrEmpty(id) && !_queue.Contains(id)) _queue.Add(id);
            if (_queue.Remove(managerId)) _queue.Add(managerId);
            if (wasEmpty && _queue.Count > 0) _nextStep = Math.Max(_nextStep, now);
        }

        /// <summary>The next mod to update now, or null when none is due yet (the gap since the last one has not passed, or the queue is empty).</summary>
        internal string NextStep(double now)
        {
            if (_queue.Count == 0 || now < _nextStep) return null;
            string id = _queue[0];
            _queue.RemoveAt(0);
            _nextStep = now + _gap;
            return id;
        }

        /// <summary>A step could not run yet (the game was busy, the player was loading): leave it first in line and try again a little later.</summary>
        internal void Postpone(string id, double now, double wait = 5.0)
        {
            if (string.IsNullOrEmpty(id)) return;
            _queue.Remove(id);
            _queue.Insert(0, id);
            _nextStep = now + wait;
        }

        internal void Clear() => _queue.Clear();

        /// <summary>
        /// Which installed mods should be updated: ones the player switched automatic updating on for, installed and enabled, whose feed version
        /// is newer than the copy installed (never older: no downgrades; never a mod that is not installed).
        /// </summary>
        internal static List<string> Pick(IEnumerable<Candidate> mods, Func<string, string, int> compare) =>
            mods.Where(m => m.OptedIn && m.Installed && !m.Disabled && !string.IsNullOrEmpty(m.Remote) && !string.IsNullOrEmpty(m.Local) && compare(m.Local, m.Remote) < 0)
                .Select(m => m.Guid).ToList();

        internal sealed class Candidate
        {
            public string Guid, Local, Remote;
            public bool OptedIn, Installed, Disabled;
        }

        /// <summary>Seconds GitHub asked to wait: Retry-After (seconds), or X-RateLimit-Reset (a Unix time) minus now. 0 when neither is usable.</summary>
        internal static double WaitAsked(string retryAfter, string resetUnix, DateTime utcNow)
        {
            if (double.TryParse(retryAfter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) return Math.Min(MaxBackoff, s);
            if (long.TryParse(resetUnix, out long unix))
            {
                double left = (DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime - utcNow).TotalSeconds;
                if (left > 0) return Math.Min(MaxBackoff, left + 5.0);
            }
            return 0.0;
        }
    }
}
