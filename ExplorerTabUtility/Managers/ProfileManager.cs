using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.Managers;

public sealed class ProfileManager
{
    public ObservableCollection<HotKeyProfile> Profiles { get; } = new();
    public event Action? Changed;
    public event Action? KeybindingsHookStarted;
    public event Action? KeybindingsHookStopped;
    public ProfileManager()
    {
        try
        {
            foreach (var profile in JsonSerializer.Deserialize<List<HotKeyProfile>>(SettingsManager.HotKeyProfiles) ?? [])
                Profiles.Add(profile);
        }
        catch (JsonException) { }
    }
    public HotKeyProfile Add()
    {
        var profile = new HotKeyProfile { Name = "New shortcut" };
        Profiles.Add(profile);
        Save();
        return profile;
    }
    public void Remove(HotKeyProfile profile) { Profiles.Remove(profile); Save(); }
    public IReadOnlyCollection<HotKeyProfile> GetProfiles() => Profiles.Select(profile => profile.Clone()).ToArray();
    public void Save()
    {
        SettingsManager.HotKeyProfiles = JsonSerializer.Serialize(Profiles);
        Changed?.Invoke();
    }
    public void BeginRecording() => KeybindingsHookStarted?.Invoke();
    public void EndRecording() => KeybindingsHookStopped?.Invoke();
}
