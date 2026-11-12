using System; using System.Diagnostics; using System.IO; using System.Reflection; using System.Windows.Forms; using Microsoft.Win32;
static class Setup {
  const string NAME = "DA-300 Display", ID = "DA300Display", VER = "1.0.0", COMP = "foo_da300display";
  const string UNINST = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\", RUN = @"Software\Microsoft\Windows\CurrentVersion\Run", RUNONCE = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
  // Earlier name of this app, cleaned up on install
  const string OLD_ID = "DenonTray", OLD_COMP = "foo_denontray";
  static string AppData(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel); }
  static string Local(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel); }
  static string Programs(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), rel); }
  static void Kill(string n) { foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(3000); } catch { } } }
  static void RemoveDir(string d, string tag) {
    if (!Directory.Exists(d)) return;
    try { Directory.Delete(d, true); }
    catch { using (var k = Registry.CurrentUser.CreateSubKey(RUNONCE)) k.SetValue(ID + "Cleanup" + tag, "cmd.exe /c rmdir /s /q \"" + d + "\""); } // in use (e.g. loaded by foobar2000): removed at next sign-in
  }
  static void Extract(string res, string path) { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res)) using (var f = File.Create(path)) s.CopyTo(f); }
  [STAThread] static int Main(string[] args) {
    bool quiet = Array.Exists(args, a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/q", StringComparison.OrdinalIgnoreCase));
    bool uninstall = Array.Exists(args, a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
    string dest = Local(@"Programs\" + ID), exe = Path.Combine(dest, ID + ".exe"), self = Path.Combine(dest, ID + "-Setup.exe"), lnk = Programs(NAME + ".lnk");
    string fbProfile = AppData("foobar2000-v2"), comp = Path.Combine(fbProfile, @"user-components-x64\" + COMP);
    try {
      Kill(ID); Kill(OLD_ID);
      if (uninstall) {
        if (!quiet && MessageBox.Show("Remove " + NAME + "?", NAME, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
        try { File.Delete(lnk); } catch { }
        RemoveDir(comp, "Comp");
        Registry.CurrentUser.DeleteSubKeyTree(UNINST + ID, false); Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + ID, false);
        using (var k = Registry.CurrentUser.OpenSubKey(RUN, true)) if (k != null) k.DeleteValue(ID, false);
        RemoveDir(Local(ID), "Log");
        try { File.Delete(exe); } catch { }
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 >nul & rmdir /s /q \"" + dest + "\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
        if (!quiet) MessageBox.Show(NAME + " was removed.", NAME); return 0;
      }
      // Migrate settings and start-with-Windows from the earlier name, then remove its leftovers
      bool startWithWindows = false;
      using (var old = Registry.CurrentUser.OpenSubKey(@"Software\" + OLD_ID)) if (old != null) {
        bool exists; using (var cur = Registry.CurrentUser.OpenSubKey(@"Software\" + ID)) exists = cur != null;
        if (!exists) using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + ID)) foreach (var n in old.GetValueNames()) k.SetValue(n, old.GetValue(n), old.GetValueKind(n));
      }
      Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + OLD_ID, false);
      using (var k = Registry.CurrentUser.OpenSubKey(RUN, true)) if (k != null) { if (k.GetValue(OLD_ID) != null) { startWithWindows = true; k.DeleteValue(OLD_ID, false); } if (k.GetValue(ID) != null) startWithWindows = true; }
      Registry.CurrentUser.DeleteSubKeyTree(UNINST + OLD_ID, false);
      try { File.Delete(Programs(OLD_ID + ".lnk")); } catch { }
      RemoveDir(Local(@"Programs\" + OLD_ID), "OldApp"); RemoveDir(Local(OLD_ID), "OldLog");
      RemoveDir(Path.Combine(fbProfile, @"user-components-x64\" + OLD_COMP), "OldComp");
      // Install
      Directory.CreateDirectory(dest); Extract(ID + ".exe", exe);
      string me = Assembly.GetExecutingAssembly().Location; if (!string.Equals(me, self, StringComparison.OrdinalIgnoreCase)) File.Copy(me, self, true);
      Type sh = Type.GetTypeFromProgID("WScript.Shell"); dynamic ws = Activator.CreateInstance(sh); dynamic l = ws.CreateShortcut(lnk); l.TargetPath = exe; l.Description = "Status panel for the Denon DA-300USB"; l.Save();
      using (var k = Registry.CurrentUser.CreateSubKey(UNINST + ID)) {
        k.SetValue("DisplayName", NAME); k.SetValue("DisplayVersion", VER); k.SetValue("Publisher", NAME);
        k.SetValue("DisplayIcon", exe); k.SetValue("InstallLocation", dest); k.SetValue("UninstallString", "\"" + self + "\" /uninstall"); k.SetValue("QuietUninstallString", "\"" + self + "\" /uninstall /quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord); k.SetValue("NoRepair", 1, RegistryValueKind.DWord); k.SetValue("EstimatedSize", 60, RegistryValueKind.DWord); }
      if (startWithWindows) using (var k = Registry.CurrentUser.CreateSubKey(RUN)) k.SetValue(ID, "\"" + exe + "\"");
      if (Directory.Exists(fbProfile)) { try { Directory.CreateDirectory(comp); Extract(COMP + ".dll", Path.Combine(comp, COMP + ".dll")); } catch { } }
      Process.Start(exe);
      if (!quiet) MessageBox.Show(NAME + " is installed and running.\n\nIt's in the Start menu, and you can remove it from Settings > Apps.", NAME + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return 0;
    } catch (Exception ex) { if (!quiet) MessageBox.Show("Setup failed: " + ex.Message, NAME + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
  }
}
