using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace TaskbarStats.Ui;

/// <summary>
/// Start with Windows through a Task Scheduler logon task that runs with the highest privileges, so the app
/// starts as administrator without a UAC prompt (needed for CPU / motherboard sensors). Creating or deleting
/// the task needs administrator rights once; the old per-user Run key is cleaned up on every change.
/// </summary>
internal static class Autostart
{
    const string TaskName = "TaskbarStats";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Returns false when the change was not made (UAC declined, policy).</summary>
    public static bool Set(bool on)
    {
        RemoveRunKey();
        try
        {
            string args;
            string? xml = null;
            if (on)
            {
                string? exe = Environment.ProcessPath;
                if (exe == null) return false;
                xml = Path.Combine(Path.GetTempPath(), "TaskbarStats_task.xml");
                File.WriteAllText(xml, TaskXml(exe), Encoding.Unicode);
                args = $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F";
            }
            else args = $"/Delete /TN \"{TaskName}\" /F";

            var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            if (xml != null) try { File.Delete(xml); } catch { }
            return p != null && p.ExitCode == 0;
        }
        catch { return false; }                       // UAC declined or blocked by policy
    }

    static void RemoveRunKey()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(TaskName, false); }
        catch { }
    }

    static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape(WindowsUser());
        string path = SecurityElement.Escape(exe);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts the TaskbarStats widget at logon with administrator rights.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
              </Settings>
              <Actions Context="Author"><Exec><Command>{path}</Command><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    static string WindowsUser() => System.Security.Principal.WindowsIdentity.GetCurrent().Name;
}

/// <summary>Administrator check and the "start elevated" relaunch used when the program is opened normally.</summary>
internal static class Elevation
{
    public static bool IsAdmin()
    {
        try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>
    /// Start an elevated copy and report whether it was launched (then this process should exit). Uses the startup
    /// task when it exists (no prompt at all), otherwise the normal UAC prompt; declining the prompt returns false.
    /// </summary>
    public static bool Relaunch(string[] args)
    {
        try
        {
            if (Autostart.IsEnabled())
            {
                var run = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Run /TN \"TaskbarStats\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(run);
                p?.WaitForExit(5000);
                if (p != null && p.ExitCode == 0) return true;
            }
            string? exe = Environment.ProcessPath;
            if (exe == null) return false;
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }                       // UAC declined: carry on without administrator rights
    }
}
