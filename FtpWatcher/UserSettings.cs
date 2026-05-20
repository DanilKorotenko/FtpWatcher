namespace FtpWatcher;

public sealed class UserSettings
{
    public string ServerAddress { get; set; } = "ftp://";
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public string RefreshTimeoutSeconds { get; set; } = "60";
}
