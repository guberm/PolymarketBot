Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

rootDir = fso.GetParentFolderName(WScript.ScriptFullName)
dashboardDir = fso.BuildPath(rootDir, "dashboard")
electronPath = fso.BuildPath(dashboardDir, "node_modules\electron\dist\electron.exe")

shell.CurrentDirectory = dashboardDir

If fso.FileExists(electronPath) Then
    shell.Run """" & electronPath & """ .", 1, False
Else
    shell.Run "cmd.exe /c npx electron .", 0, False
End If