using System;
using System.Collections.Generic;
using System.Linq;

namespace G_Lumen.Services
{
    /// <summary>Interpolation between schedule points.</summary>
    public enum ScheduleMode
    {
        /// <summary>Straight line between points (f.lux-like ramps).</summary>
        Linear,
        /// <summary>Hold the last point's value until the next one (hard steps).</summary>
        Steps,
        /// <summary>Cosine ease between points (gentle S-curve ramps).</summary>
        Smooth,
    }

    /// <summary>One point on the daily brightness curve.</summary>
    public sealed class SchedulePoint
    {
        /// <summary>Time of day as "HH:mm".</summary>
        public string Time { get; set; } = "08:00";

        /// <summary>Brightness 0–100 at that time.</summary>
        public int Percent { get; set; } = 50;
    }

    /// <summary>Per-monitor daily brightness schedule.</summary>
    public sealed class ScheduleData
    {
        public bool Enabled { get; set; }
        public ScheduleMode Mode { get; set; } = ScheduleMode.Linear;
        public List<SchedulePoint> Points { get; set; } = new();
    }

    /// <summary>
    /// Evaluates a daily schedule: given the points and a time of day, returns
    /// the brightness the curve prescribes. The curve wraps around midnight
    /// (the segment between the last and first point crosses 00:00).
    /// </summary>
    public static class ScheduleEvaluator
    {
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return TimeSpan.TryParseExact(text.Trim(),
                       new[] { @"hh\:mm", @"h\:mm" }, null, out time)
                   && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        /// <summary>
        /// Computes the scheduled brightness (0–100) for the given time of day.
        /// Returns null when the schedule has no valid points.
        /// </summary>
        public static int? Evaluate(ScheduleData schedule, TimeSpan timeOfDay)
        {
            var pts = schedule.Points
                .Select(p => TryParseTime(p.Time, out var t)
                    ? ((TimeSpan t, int pct)?)(t, Math.Clamp(p.Percent, 0, 100))
                    : null)
                .Where(p => p is not null)
                .Select(p => p!.Value)
                .OrderBy(p => p.t)
                .ToList();

            if (pts.Count == 0)
                return null;
            if (pts.Count == 1)
                return pts[0].pct;

            // Find the segment [prev, next] containing timeOfDay (wrapping midnight).
            var prev = pts.Last();
            var next = pts.First();
            foreach (var p in pts)
            {
                if (p.t <= timeOfDay)
                    prev = p;
                else
                {
                    next = p;
                    break;
                }
            }
            if (timeOfDay >= pts.Last().t || timeOfDay < pts.First().t)
            {
                prev = pts.Last();
                next = pts.First();
            }

            if (schedule.Mode == ScheduleMode.Steps || prev.pct == next.pct)
                return prev.pct;

            // Segment length and position, both wrapped across midnight.
            double day = TimeSpan.FromDays(1).TotalMinutes;
            double span = (next.t - prev.t).TotalMinutes;
            if (span <= 0)
                span += day;
            double into = (timeOfDay - prev.t).TotalMinutes;
            if (into < 0)
                into += day;

            double f = Math.Clamp(into / span, 0.0, 1.0);
            if (schedule.Mode == ScheduleMode.Smooth)
                f = (1 - Math.Cos(f * Math.PI)) / 2;

            return (int)Math.Round(prev.pct + f * (next.pct - prev.pct));
        }
    }
}
