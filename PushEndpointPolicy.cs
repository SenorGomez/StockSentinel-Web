namespace StockSentinel;

public static class PushEndpointPolicy
{
    public static Uri Validate(string? value)
    {
        if(value is null||value.Length>2048||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||
            uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length>0||uri.Fragment.Length>0||uri.AbsolutePath=="/")
            throw new ArgumentException("Invalid Web Push endpoint");
        var host=uri.IdnHost;
        // Treat provider paths/query strings as opaque. Match domain boundaries,
        // never arbitrary googleapis.com/apple.com subdomains or suffix lookalikes.
        if(host is not ("fcm.googleapis.com" or "android.googleapis.com" or "updates.push.services.mozilla.com" or "push.services.mozilla.com") &&
            !host.EndsWith(".push.apple.com",StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".notify.windows.com",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported Web Push service; use a subscription from Safari, Chromium, Firefox or Edge");
        return uri;
    }
}
