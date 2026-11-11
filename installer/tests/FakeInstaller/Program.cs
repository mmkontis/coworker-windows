// Writes its command line and a timestamp to the file named by FAKE_INSTALLER_MARKER.
var marker = Environment.GetEnvironmentVariable("FAKE_INSTALLER_MARKER");
if (!string.IsNullOrEmpty(marker))
    File.WriteAllText(marker, string.Join(" ", args) + "\n" + DateTime.UtcNow.ToString("O"));
return 0;
