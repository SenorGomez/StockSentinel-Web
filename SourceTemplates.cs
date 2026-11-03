using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StockSentinel;

public sealed record SourceTemplate(string Format, int Version, TemplateSource Source);
public sealed record TemplateSource(string Name, string Url, int IntervalMinutes, string Adapter,
    string? AvailableSelector = null, string? UnavailableSelector = null, string? PriceSelector = null,
    string? PricePattern = null, string? AvailabilityText = null, string? UnavailabilityText = null,
    JsonElement? JsonConfig = null);
public sealed record TemplateImport(JsonElement Template, long ItemId);

public static class SourceTemplates
{
    public const int MaxBytes = 64 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    static readonly HashSet<string> PublicQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    { "q", "query", "search", "limit", "offset", "page", "pagesize", "id", "sku", "product", "productid",
      "parts", "part", "partnumber", "store", "storeid", "location", "locale", "country", "language", "currency", "format" };
    static readonly Regex Secret = new(@"(?i)(?:bearer\s+\S+|-----BEGIN [^-]*PRIVATE KEY-----|(?:api[_-]?key|access[_-]?token|password|secret|authorization|session[_-]?id)\s*[:=]\s*\S+)", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    static readonly Regex ApplePickupPath = new(@"\A/(?:[a-z]{2}/)?shop/retail/pickup-message\z", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    static readonly Regex ApplePickupPart = new(@"\Aparts\.[0-9]+\z", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    static readonly Regex ApplePickupSecret = new(@"(?i)\b(?:token|signature|sig|auth|credential)\s*[:=]\s*\S+", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static string? ValidateSettings(NewSource x, IConfiguration config)
    {
        if(string.IsNullOrWhiteSpace(x.Name)||!Uri.TryCreate(x.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0)return "Use a valid HTTPS source URL";
        if(x.IntervalMinutes<Math.Max(3,config.GetValue("Sentinel:MinimumPollMinutes",3))||x.IntervalMinutes>1440)return "Invalid polling interval";
        if(x.Adapter is not ("html" or "rest-json"))return "Unsupported adapter";
        if(x.Adapter=="rest-json"){
            if(string.IsNullOrWhiteSpace(x.JsonConfig))return "REST JSON rules required";
            try {JsonStockDetector.Validate(x.JsonConfig);}catch(Exception e){return "Invalid JSON rules: "+e.Message;}
        }
        if(x.Adapter=="html"&&x.Enabled&&new[]{x.AvailableSelector,x.UnavailableSelector,x.PriceSelector,x.PricePattern}.Any(string.IsNullOrWhiteSpace))return "Configure all detection rules before enabling source";
        if(x.Adapter=="html"&&!string.IsNullOrWhiteSpace(x.PricePattern)){try{_=new Regex(x.PricePattern,RegexOptions.None,TimeSpan.FromMilliseconds(200));}catch{return "Invalid price regex";}if(!x.PricePattern.Contains("?<price>"))return "Price regex must include named group (?<price>...)";}
        return null;
    }

    public static SourceTemplate Read(JsonElement document, IConfiguration config)
    {
        if(document.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Template must be a JSON object");
        if(Encoding.UTF8.GetByteCount(document.GetRawText())>MaxBytes)throw new ArgumentException("Template must be at most 64 KiB");
        RejectDuplicateProperties(document);
        SourceTemplate template;
        try { template = document.Deserialize<SourceTemplate>(Json) ?? throw new ArgumentException("Missing template"); }
        catch(JsonException){throw new ArgumentException("Invalid template schema or unsupported fields");}
        if(template.Format!="stocksentinel-source"||template.Version!=1||template.Source is null)throw new ArgumentException("Unsupported template format/version or missing source");
        var source=template.Source;
        var error=ValidateSettings(ToSource(template,0),config);
        if(error is not null)throw new ArgumentException(error);
        if(source.Name.Length>150)throw new ArgumentException("Source name must be at most 150 characters");
        SafeUrl(source.Url);
        if(source.Adapter=="rest-json")
        {
            var rules=source.JsonConfig!.Value;
            try { _=rules.Deserialize<JsonRules>(new JsonSerializerOptions(Json){PropertyNameCaseInsensitive=true}); }
            catch(JsonException){throw new ArgumentException("Unsupported JSON detector fields");}
            var validated=JsonStockDetector.Validate(rules.GetRawText());
            if(validated.PurchaseUrl is not null)SafeUrl(validated.PurchaseUrl);
        }
        else if(source.JsonConfig is not null)throw new ArgumentException("HTML templates cannot contain JSON detector rules");
        CheckStrings(document);
        return template;
    }

    public static NewSource ToSource(SourceTemplate template, long itemId)
    {
        var s=template.Source;
        return new(itemId,s.Name,s.Url,s.IntervalMinutes,false,s.Adapter,s.AvailableSelector,s.UnavailableSelector,
            s.PriceSelector,s.PricePattern,s.AvailabilityText,s.UnavailabilityText,s.JsonConfig?.GetRawText());
    }

    public static SourceTemplate Export(Source source, IConfiguration config)
    {
        JsonElement? rules=null;
        if(source.Adapter=="rest-json")
        {
            try { rules=JsonSerializer.Deserialize<JsonElement>(source.JsonConfig ?? "null"); }
            catch(JsonException){throw new ArgumentException("Invalid JSON detector rules");}
        }
        // Explicit projection: no IDs, product settings, enabled state, runtime state or notification data.
        var s=new TemplateSource(source.Name,source.Url,source.IntervalMinutes,source.Adapter,
            source.Adapter=="html"?source.AvailableSelector:null,source.Adapter=="html"?source.UnavailableSelector:null,
            source.Adapter=="html"?source.PriceSelector:null,source.Adapter=="html"?source.PricePattern:null,
            source.Adapter=="html"?source.AvailabilityText:null,source.Adapter=="html"?source.UnavailabilityText:null,rules);
        return Read(JsonSerializer.SerializeToElement(new SourceTemplate("stocksentinel-source",1,s),Json),config);
    }
    public static byte[] Bytes(SourceTemplate template) => JsonSerializer.SerializeToUtf8Bytes(template,Json);
    public static bool IsDuplicate(Source existing, NewSource incoming) =>
        string.Equals(existing.Name,incoming.Name,StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(existing.Url,UriKind.Absolute,out var a) && Uri.TryCreate(incoming.Url,UriKind.Absolute,out var b) &&
         Uri.Compare(a,b,UriComponents.HttpRequestUrl,UriFormat.SafeUnescaped,StringComparison.Ordinal)==0);

    static void SafeUrl(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Fragment.Length>0)
            throw new ArgumentException("Template URLs must be HTTPS without credentials or fragments");
        var applePickup=uri.IdnHost.Equals("www.apple.com",StringComparison.OrdinalIgnoreCase)
            &&uri.Port==443&&ApplePickupPath.IsMatch(uri.AbsolutePath);
        foreach(var part in uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries))
        {
            var pair=part.Split('=',2);
            var key=Uri.UnescapeDataString(pair[0]);
            // Decode form-style spaces as well as percent escapes before checking
            // Apple query values. Public product/location fields must not hide credentials.
            var queryValue=Uri.UnescapeDataString((pair.Length==2?pair[1]:"").Replace('+',' '));
            if(applePickup&&(Secret.IsMatch(queryValue)||ApplePickupSecret.IsMatch(queryValue)))
                throw new ArgumentException("Template contains a potentially sensitive query value; remove it before sharing");
            if(applePickup&&key=="pl")
            {
                if(queryValue is not ("true" or "false"))throw new ArgumentException("Apple pickup pl must be true or false");
                continue;
            }
            if(!PublicQueryKeys.Contains(key)&&!(applePickup&&ApplePickupPart.IsMatch(key)))
                throw new ArgumentException("URL query parameter is not approved for sharing. Remove private parameters from a copy of the source before exporting.");
        }
    }
    static void RejectDuplicateProperties(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in e.EnumerateObject())
            {
                if(!names.Add(p.Name))throw new ArgumentException("Duplicate template properties are not supported");
                RejectDuplicateProperties(p.Value);
            }
        }
        else if(e.ValueKind==JsonValueKind.Array)foreach(var child in e.EnumerateArray())RejectDuplicateProperties(child);
    }
    static void CheckStrings(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.String && Secret.IsMatch(Uri.UnescapeDataString(e.GetString()!)))
            throw new ArgumentException("Template contains a potentially sensitive value; remove it before sharing");
        if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())CheckStrings(p.Value);
        else if(e.ValueKind==JsonValueKind.Array)foreach(var child in e.EnumerateArray())CheckStrings(child);
    }
}
