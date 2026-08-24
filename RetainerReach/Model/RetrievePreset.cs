using System;
using System.Collections.Generic;

namespace RetainerReach.Model
{
    /// <summary>
    /// One saved retrieve "shopping list": item identity + requested qty only — NOT retainer/slot,
    /// since a preset is resolved against live <see cref="UnifiedItem"/> data at apply time (holdings
    /// and even total retainer qty can have changed since the preset was saved). Persisted via
    /// <see cref="Configuration.Presets"/> / Dalamud's <c>SavePluginConfig</c> JSON, so this and
    /// <see cref="PresetEntry"/> must be public with public settable members (Task 10.1).
    /// </summary>
    [Serializable]
    public class RetrievePreset
    {
        public string Name { get; set; } = string.Empty;

        public List<PresetEntry> Entries { get; set; } = new();
    }

    /// <summary>One (ItemId, Hq) + requested qty entry inside a <see cref="RetrievePreset"/>.</summary>
    [Serializable]
    public class PresetEntry
    {
        public uint ItemId { get; set; }

        public bool Hq { get; set; }

        public uint Qty { get; set; }
    }
}
