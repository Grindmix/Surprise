using System;
using System.Collections.Generic;
using System.Linq;

namespace Surprise.Core;

// Only presence transitions matter. Baselines consume existing connections without firing.
public sealed class TriggerState(SurpriseConfig config)
{
    private HashSet<string> previous = [];
    private bool initialized;
    private DateTimeOffset lastPlayback = DateTimeOffset.MinValue;
    public bool Consumed { get; set; }

    public void Baseline(IEnumerable<ControllerIdentity> controllers)
    {
        previous = controllers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        initialized = true;
    }

    public bool Observe(IEnumerable<ControllerIdentity> controllers, DateTimeOffset now, bool sessionActive, bool playing, bool paused)
    {
        var current = controllers.ToList();
        if (!initialized) { Baseline(current); return false; }
        var arrivals = current.Where(c => !previous.Contains(c.Id)).ToList();
        if (config.Trigger == TriggerKind.AnyController)
        {
            int retiredFallbacks = previous.Count(id => id.StartsWith("xinput-fallback:", StringComparison.Ordinal)) - current.Count(c => c.Source == "XInput");
            // A fallback becoming identifiable through WGI is the same connection.
            foreach (var promoted in arrivals.Where(c => c.Source == "WGI").Take(Math.Max(0, retiredFallbacks)).ToList()) arrivals.Remove(promoted);
        }
        Baseline(current); // Suppressed arrivals are deliberately dropped, not replayed later.
        if (!sessionActive || playing || paused || (config.OneShot && Consumed)) return false;
        if (now - lastPlayback < TimeSpan.FromSeconds(config.CooldownSeconds)) return false;
        return arrivals.Any(c => config.Trigger == TriggerKind.AnyController || config.Controllers.Any(rule =>
            config.Trigger == TriggerKind.ControllerModel ? rule.MatchesModel(c) : rule.Source == c.Source && rule.Id == c.Id));
    }

    // Commit only after media opens successfully, not when a launch fails.
    public void PlaybackStarted(DateTimeOffset now) { lastPlayback = now; if (config.OneShot) Consumed = true; }
}
