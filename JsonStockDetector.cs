using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace StockSentinel;

// Strict JSON property/index paths instead of unrestricted expressions.
// e.g. $.data.products[0].inventory.orderable. A required product ID protects against changed search ordering.
public sealed class JsonRules
{
    public string? ItemsPath { get; set; }
    public string? LabelPath { get; set; }
    public string MatchMode { get; set; } = "any";
    public string IdPath { get; set; } = "";
    public string ExpectedId { get; set; } = "";
    public string AvailablePath { get; set; } = "";
    public string? ConfirmationPath { get; set; }
    public string? PreorderPath { get; set; }
    public string? AvailableValue { get; set; }
    public string? PricePath { get; set; }
    public string? CurrencyPath { get; set; }
    public string Currency { get; set; } = "GBP";
    public string? PurchaseUrl { get; set; }
}
public static class JsonStockDetector
{
    static readonly Regex Segment = new(@"(?:\.([A-Za-z_][A-Za-z0-9_]*))|(?:\[(\d{1,3})\])|(?:\[""([^""]+)""\])|(?:\['([^']+)'\])", RegexOptions.Compiled);
    public static JsonRules Validate(string config)
    {
        var rules=JsonSerializer.Deserialize<JsonRules>(config, new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? throw new ArgumentException("Missing rules");
        if(string.IsNullOrWhiteSpace(rules.IdPath)||string.IsNullOrWhiteSpace(rules.ExpectedId)||string.IsNullOrWhiteSpace(rules.AvailablePath))throw new ArgumentException("ID path, expected ID and availability path are required");
        if(string.IsNullOrWhiteSpace(rules.PricePath)!=string.IsNullOrWhiteSpace(rules.CurrencyPath))throw new ArgumentException("Price and currency paths must both be provided or both omitted");
        if(rules.AvailableValue is not null && string.IsNullOrWhiteSpace(rules.AvailableValue))throw new ArgumentException("Available value cannot be empty");
        if(rules.ItemsPath is not null)
        {
            if(string.IsNullOrWhiteSpace(rules.ItemsPath))throw new ArgumentException("Items path cannot be empty");
            if(rules.MatchMode is not ("any" or "all"))throw new ArgumentException("Match mode must be any or all");
            if(string.IsNullOrWhiteSpace(rules.LabelPath))throw new ArgumentException("Collection label path is required");
            // A wildcard is allowed only at the end of the collection selector.
            var collectionPath=rules.ItemsPath.EndsWith("[*]",StringComparison.Ordinal)?rules.ItemsPath[..^3]:rules.ItemsPath;
            ValidatePath(collectionPath);
            ValidatePath(rules.LabelPath);
        }
        foreach(var path in new[]{rules.IdPath,rules.AvailablePath,rules.ConfirmationPath,rules.PreorderPath,rules.PricePath,rules.CurrencyPath}.Where(x=>x is not null))ValidatePath(path!);
        if(!string.Equals(rules.Currency,"GBP",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only GBP is supported by the current alert/price comparison");
        if(rules.PurchaseUrl is not null && (!Uri.TryCreate(rules.PurchaseUrl,UriKind.Absolute,out var u)||u.Scheme!="https"))throw new ArgumentException("Purchase URL must be HTTPS");
        return rules;
    }
    static void ValidatePath(string path)
    {
        if(!path.StartsWith("$.",StringComparison.Ordinal))throw new ArgumentException($"Path must start with $.: {path}");
        int i=1;foreach(Match m in Segment.Matches(path)){if(m.Index!=i)throw new ArgumentException($"Unsupported JSON path: {path}");i=m.Index+m.Length;}
        if(i!=path.Length)throw new ArgumentException($"Unsupported JSON path: {path}");
    }
    static JsonElement? Read(JsonElement root,string? path)
    {
        if(string.IsNullOrEmpty(path))return null;
        var current=root;
        foreach(Match m in Segment.Matches(path))
        {
            if(m.Groups[1].Success){if(current.ValueKind!=JsonValueKind.Object || !current.TryGetProperty(m.Groups[1].Value,out current))return null;}
            else if(m.Groups[2].Success){if(current.ValueKind!=JsonValueKind.Array||!int.TryParse(m.Groups[2].Value,out var index)||index>=current.GetArrayLength())return null;current=current[index];}
            else {var key=m.Groups[3].Success?m.Groups[3].Value:m.Groups[4].Value;
                if(current.ValueKind!=JsonValueKind.Object||!current.TryGetProperty(key,out current))return null;}
        }
        return current;
    }
    static bool? Boolean(JsonElement? e)=>e?.ValueKind switch { JsonValueKind.True=>true,JsonValueKind.False=>false,_=>null };
    static string? String(JsonElement? e)=>e?.ValueKind==JsonValueKind.String?e.Value.GetString():null;
    public static string? PurchaseUrl(Source s)
    {
        if(s.Adapter!="rest-json"||string.IsNullOrWhiteSpace(s.JsonConfig))return null;
        try{return Validate(s.JsonConfig).PurchaseUrl;}catch{return null;}
    }
    static CheckResult DetectOne(JsonElement root,JsonRules r)
    {
        var id=String(Read(root,r.IdPath));
        if(id is null)return new("Unknown",null,"Product ID missing in API response");
        if(!string.Equals(id,r.ExpectedId,StringComparison.Ordinal))return new("Unknown",null,$"Unexpected product ID: {id}");
        var availability=Read(root,r.AvailablePath);
        bool? orderable=r.AvailableValue is null?Boolean(availability):String(availability) is string value
            ? string.Equals(value,r.AvailableValue,StringComparison.OrdinalIgnoreCase) : null;
        var confirmation=string.IsNullOrEmpty(r.ConfirmationPath)?null:Boolean(Read(root,r.ConfirmationPath));
        var preorder=string.IsNullOrEmpty(r.PreorderPath)?false:Boolean(Read(root,r.PreorderPath));
        if(orderable is null || (r.ConfirmationPath is not null&&confirmation is null) || preorder is null)return new("Unknown",null,"Required availability value missing or invalid");
        if(r.ConfirmationPath is not null && orderable!=confirmation)return new("Unknown",null,"Conflicting orderability flags");
        if(orderable==false && preorder==false)return new("Unavailable",null,"API confirms not orderable or preorderable");
        if(string.IsNullOrWhiteSpace(r.PricePath))return new("Available",null,orderable==true?"API confirms available":"API confirms preorderable");
        if(!string.Equals(String(Read(root,r.CurrencyPath)),r.Currency,StringComparison.OrdinalIgnoreCase))return new("Unknown",null,"Currency missing or unexpected");
        var p=Read(root,r.PricePath);
        if(p is null || p.Value.ValueKind!=JsonValueKind.Number || !p.Value.TryGetDecimal(out var price)||price<=0)return new("Unknown",null,"Price missing or invalid");
        return new("Available",price,orderable==true?"API confirms orderable":"API confirms preorderable");
    }
    public static CheckResult Detect(byte[] bytes,string? config)
    {
        if(string.IsNullOrWhiteSpace(config))return new("Unknown",null,"Missing JSON detector configuration");
        var r=Validate(config);
        using var doc=JsonDocument.Parse(bytes);
        var root=doc.RootElement;
        if(r.ItemsPath is null)return DetectOne(root,r);
        var path=r.ItemsPath.EndsWith("[*]",StringComparison.Ordinal)?r.ItemsPath[..^3]:r.ItemsPath;
        var items=Read(root,path);
        if(items is null || items.Value.ValueKind!=JsonValueKind.Array || items.Value.GetArrayLength()==0)
            return new("Unknown",null,"Collection missing, empty or not an array");
        var matched=new List<string>();
        var unknown=new List<string>();
        var checkedLabels=new List<string>();
        decimal? price=null;
        int total=0;
        foreach(var item in items.Value.EnumerateArray())
        {
            total++;
            var label=String(Read(item,r.LabelPath));
            if(string.IsNullOrWhiteSpace(label))return new("Unknown",null,$"Collection item {total} has no valid label");
            checkedLabels.Add(label);
            var result=DetectOne(item,r);
            if(result.Status=="Available") { matched.Add(label);price=result.Price; }
            else if(result.Status!="Unavailable")unknown.Add(label);
        }
        // Collection details remain retailer-agnostic: labels come from the configured labelPath.
        var checkedDetail=$"Checked {total} items: {string.Join(", ",checkedLabels)}";
        // Never claim all-unavailable if a response is malformed. Any-match positives remain valid.
        if(r.MatchMode=="any" && matched.Count>0)
            return new("Available",price,$"Available: {string.Join(", ",matched)}. {checkedDetail}");
        if(unknown.Count>0)
            return new("Unknown",null,$"Unverified collection items: {string.Join(", ",unknown)}. {checkedDetail}");
        if(r.MatchMode=="all" && matched.Count==total)
            return new("Available",price,$"All {total} matched: {string.Join(", ",matched)}. {checkedDetail}");
        return new("Unavailable",null,r.MatchMode=="any"
            ?$"No collection items available. {checkedDetail}"
            :$"Only {matched.Count} of {total} collection items available. {checkedDetail}");
    }
}
