using System;
using System.Collections.Generic;
using System.Linq;
using GGDealsWishlist.Infrastructure;

namespace GGDealsWishlist.Api
{
    public sealed class UsageRecord
    {
        public DateTime AtUtc { get; set; }

        public int Count { get; set; }
    }

    /// <summary>Persisted rate-limit bookkeeping so limits are respected across Playnite restarts.</summary>
    public sealed class RateLimitState
    {
        public List<UsageRecord> Usage { get; set; } = new List<UsageRecord>();

        public int? ServerLimit { get; set; }

        public int? ServerRemaining { get; set; }

        public DateTime? ServerResetUtc { get; set; }

        public DateTime? BlockedUntilUtc { get; set; }
    }

    public struct RateLimitBudget
    {
        /// <summary>Records that may be requested right now without exceeding any known limit.</summary>
        public int Available { get; set; }

        public int MinuteUsed { get; set; }

        public int HourUsed { get; set; }

        public int MinuteRemaining { get; set; }

        public int HourRemaining { get; set; }

        public bool IsBlocked { get; set; }

        /// <summary>When Available is 0: the earliest time at least one record becomes available.</summary>
        public DateTime? NextAvailableUtc { get; set; }

        public DateTime? MinuteResetUtc { get; set; }

        public DateTime? HourResetUtc { get; set; }

        public int? ServerRemaining { get; set; }

        public DateTime? ServerResetUtc { get; set; }
    }

    /// <summary>
    /// Tracks record usage against the documented limits (100 records/minute, 1,000 records/hour; each id is one
    /// record, invalid requests count too). The local sliding window is combined with the server's
    /// x-ratelimit-* headers and any 429 back-off, and the most restrictive value wins.
    /// </summary>
    public sealed class RateLimitTracker
    {
        public const int RecordsPerMinute = 100;
        public const int RecordsPerHour = 1000;

        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

        private readonly object sync = new object();
        private readonly IClock clock;
        private readonly List<UsageRecord> usage = new List<UsageRecord>();
        private int? serverLimit;
        private int? serverRemaining;
        private DateTime? serverResetUtc;
        private DateTime? blockedUntilUtc;

        public RateLimitTracker(IClock clock = null, RateLimitState state = null)
        {
            this.clock = clock ?? SystemClock.Instance;
            Import(state);
        }

        public event EventHandler Changed;

        public void Import(RateLimitState state)
        {
            lock (sync)
            {
                usage.Clear();
                if (state?.Usage != null)
                {
                    usage.AddRange(state.Usage.Where(u => u != null && u.Count > 0).Select(u => new UsageRecord { AtUtc = DateTime.SpecifyKind(u.AtUtc, DateTimeKind.Utc), Count = u.Count }));
                }

                serverLimit = state?.ServerLimit;
                serverRemaining = state?.ServerRemaining;
                serverResetUtc = state?.ServerResetUtc;
                blockedUntilUtc = state?.BlockedUntilUtc;
                Prune(clock.UtcNow);
            }
        }

        public RateLimitState Export()
        {
            lock (sync)
            {
                Prune(clock.UtcNow);
                return new RateLimitState
                {
                    Usage = usage.Select(u => new UsageRecord { AtUtc = u.AtUtc, Count = u.Count }).ToList(),
                    ServerLimit = serverLimit,
                    ServerRemaining = serverRemaining,
                    ServerResetUtc = serverResetUtc,
                    BlockedUntilUtc = blockedUntilUtc
                };
            }
        }

        public RateLimitBudget GetBudget()
        {
            lock (sync)
            {
                var now = clock.UtcNow;
                Prune(now);

                var minuteWindow = usage.Where(u => u.AtUtc > now - Minute).OrderBy(u => u.AtUtc).ToList();
                var hourWindow = usage.Where(u => u.AtUtc > now - Hour).OrderBy(u => u.AtUtc).ToList();
                var minuteUsed = minuteWindow.Sum(u => u.Count);
                var hourUsed = hourWindow.Sum(u => u.Count);

                var budget = new RateLimitBudget
                {
                    MinuteUsed = minuteUsed,
                    HourUsed = hourUsed,
                    MinuteRemaining = Math.Max(0, RecordsPerMinute - minuteUsed),
                    HourRemaining = Math.Max(0, RecordsPerHour - hourUsed),
                    MinuteResetUtc = minuteWindow.Count > 0 ? minuteWindow[0].AtUtc + Minute : (DateTime?)null,
                    HourResetUtc = hourWindow.Count > 0 ? hourWindow[0].AtUtc + Hour : (DateTime?)null
                };

                var available = Math.Min(budget.MinuteRemaining, budget.HourRemaining);
                var serverActive = serverRemaining.HasValue && serverResetUtc.HasValue && serverResetUtc.Value > now;
                if (serverActive)
                {
                    budget.ServerRemaining = serverRemaining;
                    budget.ServerResetUtc = serverResetUtc;
                    available = Math.Min(available, serverRemaining.Value);
                }

                budget.IsBlocked = blockedUntilUtc.HasValue && blockedUntilUtc.Value > now;
                if (budget.IsBlocked)
                {
                    available = 0;
                }

                budget.Available = available;
                if (available <= 0)
                {
                    // Every exhausted constraint must clear, so the next opportunity is the latest of them.
                    var next = now;
                    if (budget.IsBlocked)
                    {
                        next = Max(next, blockedUntilUtc.Value);
                    }

                    if (serverActive && serverRemaining.Value <= 0)
                    {
                        next = Max(next, serverResetUtc.Value);
                    }

                    if (budget.MinuteRemaining <= 0 && budget.MinuteResetUtc.HasValue)
                    {
                        next = Max(next, budget.MinuteResetUtc.Value);
                    }

                    if (budget.HourRemaining <= 0 && budget.HourResetUtc.HasValue)
                    {
                        next = Max(next, budget.HourResetUtc.Value);
                    }

                    budget.NextAvailableUtc = next;
                }

                return budget;
            }
        }

        /// <summary>Records usage before a request is sent (GG.deals counts invalid requests as well).</summary>
        public void RecordUsage(int records)
        {
            if (records <= 0)
            {
                return;
            }

            lock (sync)
            {
                usage.Add(new UsageRecord { AtUtc = clock.UtcNow, Count = records });
                if (serverRemaining.HasValue)
                {
                    serverRemaining = Math.Max(0, serverRemaining.Value - records);
                }
            }

            RaiseChanged();
        }

        public void ApplyServerHeaders(RateLimitHeaders headers)
        {
            if (headers == null || !headers.HasValues)
            {
                return;
            }

            lock (sync)
            {
                serverLimit = headers.Limit ?? serverLimit;
                if (headers.Remaining.HasValue)
                {
                    serverRemaining = headers.Remaining;
                    serverResetUtc = headers.ResetUtc ?? clock.UtcNow + Minute;
                }
            }

            RaiseChanged();
        }

        public void MarkRateLimited(DateTime untilUtc)
        {
            lock (sync)
            {
                if (!blockedUntilUtc.HasValue || untilUtc > blockedUntilUtc.Value)
                {
                    blockedUntilUtc = untilUtc;
                }
            }

            RaiseChanged();
        }

        public void Reset()
        {
            lock (sync)
            {
                usage.Clear();
                serverLimit = null;
                serverRemaining = null;
                serverResetUtc = null;
                blockedUntilUtc = null;
            }

            RaiseChanged();
        }

        private void Prune(DateTime now)
        {
            usage.RemoveAll(u => u.AtUtc <= now - Hour || u.AtUtc > now + Hour);
            if (blockedUntilUtc.HasValue && blockedUntilUtc.Value <= now)
            {
                blockedUntilUtc = null;
            }
        }

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        private void RaiseChanged()
        {
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { Log.Error(e, "Rate limit listener failed"); }
        }
    }
}
