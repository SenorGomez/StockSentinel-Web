using System.Text.Json.Serialization;
namespace StockSentinel;
public sealed record Item(long Id, string Name, decimal MaxPriceGbp, bool Enabled);
public sealed record Source(long Id, long ItemId, string Name, string Url, int IntervalMinutes, bool Enabled, string Adapter, string? AvailableSelector, string? UnavailableSelector, string? PriceSelector, string? PricePattern, string? AvailabilityText, string? UnavailabilityText, string Status, string? LastDetail, string? LastCheckedUtc, string? NextCheckUtc, int FailCount, string? JsonConfig);
public sealed record Event(long Id, string AtUtc, string Level, string Message, long? SourceId);
public sealed record NewItem(string Name, decimal MaxPriceGbp, bool Enabled = true);
public sealed record NewSource(long ItemId, string Name, string Url, int IntervalMinutes, bool Enabled, string Adapter = "html", string? AvailableSelector = null, string? UnavailableSelector = null, string? PriceSelector = null, string? PricePattern = null, string? AvailabilityText = null, string? UnavailabilityText = null, string? JsonConfig = null);
public sealed record CheckResult(string Status, decimal? Price, string Detail);
public sealed class SentinelOptions { public string DatabasePath { get; set; } = "data/sentinel.db"; public int MinimumPollMinutes { get; set; } = 3; public int HttpTimeoutSeconds { get; set; } = 20; public int MaxResponseBytes { get; set; } = 750000; public string DefaultUserAgent { get; set; } = "StockSentinel/1.0"; }
public sealed class SmtpOptions { public bool Enabled {get;set;} public string Host {get;set;}=""; public int Port {get;set;}=587; public string Security {get;set;}="StartTls"; public string Username {get;set;}=""; public string FromAddress {get;set;}=""; public string FromName {get;set;}="StockSentinel"; public string ToAddress {get;set;}=""; }
