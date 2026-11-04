using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Surprise.Core;

namespace Surprise.Windows;

public static class TaskRegistration
{
    public static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Cannot determine Windows user.");
    public static string Name(Guid id) => "Surprise-" + id.ToString("N");
    private static string Description(Guid id) => "Surprise controller listener; owner=" + CurrentSid + "; installation=" + id.ToString("D");

    // COM activation/registration stays on the calling STA UI thread.
    public static void Register(InstallManifest manifest, string executor, string config)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null; dynamic? task = null; dynamic? existing = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\");
            existing = Find(folder, manifest.TaskName);
            if (existing is not null) EnsureOwned(existing, manifest);
            task = service.NewTask(0);
            task.RegistrationInfo.Description = Description(manifest.InstallationId);
            task.Principal.UserId = CurrentSid;
            task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            task.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
            dynamic trigger = task.Triggers.Create(9); // TASK_TRIGGER_LOGON
            trigger.UserId = CurrentSid;
            dynamic action = task.Actions.Create(0);
            action.Path = Path.GetFullPath(executor);
            action.Arguments = "--watch --config " + Quote(Path.GetFullPath(config));
            action.WorkingDirectory = Path.GetDirectoryName(executor);
            task.Settings.Enabled = true;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.WakeToRun = false;
            task.Settings.MultipleInstances = 2; // IgnoreNew
            task.Settings.RestartCount = 3;
            task.Settings.RestartInterval = "PT1M";
            dynamic registered = folder.RegisterTaskDefinition(manifest.TaskName, task, 6, CurrentSid, null, 3, null);
            Release(registered); Release(action); Release(trigger);
        }
        finally { Release(existing); Release(task); Release(folder); Release(service); }
    }

    public static void Enable(InstallManifest manifest, bool enabled) => WithOwned(manifest, (folder, task) => task.Enabled = enabled);
    public static void Remove(InstallManifest manifest) => WithOwned(manifest, (folder, task) => folder.DeleteTask(manifest.TaskName, 0));
    public static bool? Enabled(InstallManifest manifest)
    {
        bool? enabled = null; WithOwned(manifest, (folder, task) => enabled = (bool)task.Enabled); return enabled;
    }
    public static string? DefinitionXml(InstallManifest manifest)
    {
        string? xml = null; WithOwned(manifest, (folder, task) => xml = (string)task.Xml); return xml;
    }
    private static void WithOwned(InstallManifest manifest, Action<dynamic, dynamic> operation)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null; dynamic? task = null;
        try { service.Connect(); folder = service.GetFolder("\\"); task = Find(folder, manifest.TaskName); if (task is not null) { EnsureOwned(task, manifest); operation(folder, task); } }
        finally { Release(task); Release(folder); Release(service); }
    }
    private static dynamic? Find(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        // COM HRESULTs can be projected as FileNotFoundException rather than COMException.
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }
    private static void EnsureOwned(dynamic task, InstallManifest manifest)
    {
        if (manifest.OwnerSid != CurrentSid || manifest.TaskName != Name(manifest.InstallationId) || (string)task.Definition.RegistrationInfo.Description != Description(manifest.InstallationId))
            throw new InvalidOperationException("Refusing to modify a scheduled task not owned by this installation/account.");
    }
    public static string Quote(string argument) => "\"" + argument.Replace("\"", "\\\"") + "\"";
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
