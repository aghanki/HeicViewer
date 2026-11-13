using Microsoft.Win32;

namespace HeicViewer;

public static class FileAssociation
{
    public const string ProgId = "HeicViewer.Image";
    public const string AppName = "HEIC Viewer";

    public static readonly string[] Extensions = [".heic", ".heif", ".hif", ".avif"];

    public static string ExePath =>
        Environment.ProcessPath is { Length: > 0 } path
            ? path
            : AppContext.BaseDirectory + "HeicViewer.exe";

    /// <summary>
    /// Registers the viewer per-user (HKCU, no admin needed):
    ///  1. a ProgId with an open command + icon,
    ///  2. OpenWithProgids entries so the app appears under right-click → "Open with",
    ///  3. a Capabilities + RegisteredApplications entry so it appears in Settings → Default apps.
    /// Windows never lets an app silently take over a default; the user confirms once via
    /// Settings or the "Open with" dialog — this just makes the app selectable everywhere.
    /// </summary>
    public static void RegisterAll()
    {
        string exe = ExePath;
        string openCommand = $"\"{exe}\" \"%1\"";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, $"{AppName} Image");
            progId.SetValue("FriendlyTypeName", $"{AppName} Image");
            using (var open = progId.CreateSubKey(@"shell\open\command"))
                open.SetValue(null, openCommand);
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
        }

        foreach (var ext in Extensions)
        {
            string contentType = ext switch
            {
                ".heif" => "image/heif",
                ".avif" => "image/avif",
                _ => "image/heic"
            };

            using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}"))
            {
                extKey.SetValue("Content Type", contentType);
                extKey.SetValue("PerceivedType", "image");
            }

            using (var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                openWith.SetValue(ProgId, string.Empty);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\HEICViewer\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription",
                "Fast zoom/pan viewer for HEIC, HEIF, HIF and AVIF images with a built-in decoder — no paid codec needed.");
            using (var fileAssociations = capabilities.CreateSubKey("FileAssociations"))
                foreach (var ext in Extensions)
                    fileAssociations.SetValue(ext, ProgId);
        }

        using (var registeredApplications = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registeredApplications.SetValue(AppName, @"Software\HEICViewer\Capabilities");
    }
}
