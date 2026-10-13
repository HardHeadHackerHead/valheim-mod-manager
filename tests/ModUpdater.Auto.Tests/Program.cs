using System;
using System.Collections.Generic;
using System.Linq;
using ModUpdater;

internal static class Tests
{
    private static int _passed, _failed;
    private static void Check(bool ok, string what) { if (ok) _passed++; else { _failed++; Console.WriteLine("FAIL: " + what); } }
    private static int Cmp(string a, string b) => Version.Parse(a).CompareTo(Version.Parse(b));

    private static int Main()
    {
        // checks: first after the start delay, then one interval apart; never below two minutes
        var plan = new AutoPlan { Interval = 60 };
        Check(plan.Interval == AutoPlan.MinInterval, "the interval is at least two minutes");
        plan.Interval = 300; plan.Gap = 1;
        Check(plan.Gap == AutoPlan.MinGap, "the gap between mods is at least five seconds");
        plan.Gap = 20;
        plan.Start(0); plan.Start(10);
        Check(!plan.CheckDue(29) && plan.CheckDue(30), "the first check comes 30 s after the start, and starting again does not move it");
        plan.Checked(30);
        Check(!plan.CheckDue(329) && plan.CheckDue(330), "the next check one interval later");

        // GitHub's limit: back off, doubling, up to an hour; what GitHub asks for wins when longer; a good check ends it
        Check(plan.RateLimited(330, 0) == 600 && !plan.CheckDue(929) && plan.CheckDue(930), "the first back-off is twice the interval");
        Check(plan.RateLimited(930, 0) == 1200, "then it doubles");
        Check(plan.RateLimited(2130, 0) == 2400 && plan.RateLimited(4530, 0) == 3600 && plan.RateLimited(8130, 0) == 3600, "and stops at an hour");
        var p2 = new AutoPlan { Interval = 300 };
        Check(p2.RateLimited(0, 1500) == 1500, "GitHub's Retry-After wins over the doubled wait when it is longer");
        Check(p2.RateLimited(0, 99999) == 3600, "but never more than an hour");
        p2.Checked(4000);
        Check(p2.Backoff == 0 && p2.NextCheck == 4300, "a check that works ends the back-off");
        Check(AutoPlan.WaitAsked("120", null, DateTime.UtcNow) == 120, "Retry-After in seconds");
        long reset = DateTimeOffset.UtcNow.AddSeconds(600).ToUnixTimeSeconds();
        double fromReset = AutoPlan.WaitAsked(null, reset.ToString(), DateTime.UtcNow);
        Check(fromReset > 590 && fromReset < 610, "X-RateLimit-Reset as a Unix time: " + fromReset);
        Check(AutoPlan.WaitAsked("x", "nope", DateTime.UtcNow) == 0, "nothing usable: 0");

        // the queue: one at a time, a gap apart, never two in the same second, the manager last
        var q = new AutoPlan { Interval = 300, Gap = 20 };
        var twenty = Enumerable.Range(1, 20).Select(i => "mod" + i).ToList();
        twenty.Insert(3, "com.dhack.modupdater");
        q.Enqueue(twenty, "com.dhack.modupdater", 100);
        q.Enqueue(new[] { "mod5", "mod5" }, "com.dhack.modupdater", 100);
        Check(q.Queue.Count == 21 && q.Queue.Last() == "com.dhack.modupdater", "21 queued once each, the manager last");
        var started = new List<(double, string)>();
        for (double t = 100; t < 100 + 21 * 20 + 5; t += 0.1) { string id = q.NextStep(t); if (id != null) started.Add((t, id)); }
        Check(started.Count == 21, "all 21 started: " + started.Count);
        Check(started.Zip(started.Skip(1), (a, b) => b.Item1 - a.Item1).All(d => d >= 19.99), "each at least 20 s after the one before");
        Check(started.Last().Item2 == "com.dhack.modupdater", "the manager went last");
        Check(started.Count(s => s.Item1 < 101) == 1, "only one in the first second");

        // postponed (a loading screen): stays first, tried a little later
        var w = new AutoPlan { Gap = 20 };
        w.Enqueue(new[] { "a", "b" }, "m", 0);
        string first = w.NextStep(0);
        w.Postpone(first, 0);
        Check(w.NextStep(4) == null && w.NextStep(5) == "a" && w.Queue.First() == "b", "a postponed mod keeps its place and waits 5 s");

        // which mods: switched on, installed, enabled, older only
        var picks = AutoPlan.Pick(new[]
        {
            new AutoPlan.Candidate { Guid = "a", Local = "1.0.0", Remote = "1.0.1", OptedIn = true, Installed = true },
            new AutoPlan.Candidate { Guid = "b", Local = "1.0.0", Remote = "1.0.1", OptedIn = false, Installed = true },
            new AutoPlan.Candidate { Guid = "c", Local = null, Remote = "1.0.1", OptedIn = true, Installed = false },
            new AutoPlan.Candidate { Guid = "d", Local = "2.0.0", Remote = "1.9.0", OptedIn = true, Installed = true },
            new AutoPlan.Candidate { Guid = "e", Local = "1.0.0", Remote = "1.1.0", OptedIn = true, Installed = true, Disabled = true },
            new AutoPlan.Candidate { Guid = "f", Local = "1.0.0", Remote = "1.0.0", OptedIn = true, Installed = true },
        }, Cmp);
        Check(picks.SequenceEqual(new[] { "a" }), "only a: not switched on, not installed, a downgrade, disabled and same version are all left: " + string.Join(",", picks));

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
