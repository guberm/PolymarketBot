using System.Diagnostics;

// Double-clickable launcher for the Electron dashboard (replaces run-dashboard.vbs).
// Works when run from the repo build tree or copied anywhere (desktop): it tries
// its own repo-relative path first, then the known absolute repo location.
var candidates = new[]
{
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
    @"C:\Users\michael.guber\Desktop\my repos\polymarket_bot",
};
var dashboard = candidates
    .Select(r => Path.Combine(r, "dashboard"))
    .FirstOrDefault(d => File.Exists(Path.Combine(d, "node_modules", "electron", "dist", "electron.exe")));

if (dashboard is null)
{
    MessageBox.Show(
        "electron.exe not found.\n\nRun npm install inside dashboard\\ first, or keep this exe in the repo build tree.",
        "PolymarketDashboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}

Process.Start(new ProcessStartInfo(
    Path.Combine(dashboard, "node_modules", "electron", "dist", "electron.exe"), ".")
{
    WorkingDirectory = dashboard,
    UseShellExecute = false,
});
