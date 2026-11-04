using System;
using System.Collections.Generic;
using System.Linq;

namespace Surprise.Core;

// A profile is recorded from a connected device, then verified by one observed
// disconnect/reconnect with the same identity. Initial presence cannot verify it.
public sealed class ControllerLearningState
{
    public List<ControllerIdentity> Profiles { get; } = [];
    public HashSet<string> Verified { get; } = [];
    public IReadOnlyList<ControllerIdentity> Connected { get; private set; } = [];
    private readonly HashSet<string> missing = [];
    private readonly Dictionary<string, int> stable = [];
    public bool IsMissing(string id) => missing.Contains(id) && Connected.All(c => c.Id != id);
    public bool CanRecord(ControllerIdentity device) => device.Source == "WGI" && Connected.Any(c => c.Id == device.Id) && Profiles.All(c => c.Id != device.Id);
    public void Observe(IReadOnlyList<ControllerIdentity> current)
    {
        foreach (var old in Connected.Where(c => current.All(n => n.Id != c.Id)))
        { missing.Add(old.Id); stable.Remove(old.Id); }
        foreach (var item in current)
        {
            stable[item.Id] = stable.GetValueOrDefault(item.Id) + 1;
            if (missing.Contains(item.Id) && stable[item.Id] >= 2 && Profiles.Any(c => c.Id == item.Id))
            { Verified.Add(item.Id); missing.Remove(item.Id); }
        }
        Connected = current.ToList();
    }
    public void Record(ControllerIdentity device)
    {
        if (!CanRecord(device)) throw new InvalidOperationException("Choose a connected controller that has not already been added. XInput fallback controllers work with Any controller.");
        Profiles.Add(device); Verified.Remove(device.Id); missing.Remove(device.Id);
    }
    public void ResetObservation() { Connected = []; missing.Clear(); stable.Clear(); }
    public void Clear() { Profiles.Clear(); Verified.Clear(); ResetObservation(); }
}
