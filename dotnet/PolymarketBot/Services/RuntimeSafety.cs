using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PolymarketBot.Services;

public readonly record struct OrderFill(string Status, double Shares, double Value, double Price);

public static class RetryPolicy
{
    public static int DelayMilliseconds(string? retryAfter, int attempt)
    {
        var seconds = Math.Pow(2, Math.Max(0, attempt));
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            seconds = numeric;
        else if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal, out var date))
            seconds = Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
        return (int)(Math.Clamp(seconds, 0, 60) * 1000);
    }
}

public static class OrderReconciliation
{
    public static OrderFill Parse(string json, string side, double fallbackPrice)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = Text(root, "status").ToUpperInvariant();
        var making = Number(root, "makingAmount", "making_amount");
        var taking = Number(root, "takingAmount", "taking_amount");
        var shares = side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? taking : making;
        var value = side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? making : taking;
        if (shares <= 0) shares = Number(root, "size_matched", "sizeMatched", "matched_size");
        var price = Number(root, "average_price", "avg_price", "price");
        if (price <= 0) price = fallbackPrice;
        if (value <= 0 && shares > 0) value = shares * price;
        if (shares <= 0 && value > 0 && price > 0) shares = value / price;
        return new(status, shares, value, shares > 0 ? value / shares : price);
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static double Number(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0)
                return number;
            if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number > 0)
                return number;
        }
        return 0;
    }
}

public sealed class PendingOrderRecord
{
    public string IntentId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Side { get; set; } = "";
    public string TokenId { get; set; } = "";
    public double Requested { get; set; }
    public double LimitPrice { get; set; }
    public string Category { get; set; } = "other";
    public string EventTitle { get; set; } = "";
    public double FairEstimate { get; set; }
    public double Edge { get; set; }
    public double Kelly { get; set; }
    public double QuotedVwap { get; set; }
    public string ExitReason { get; set; } = "";
    public string FillStatus { get; set; } = "";
    public double FillShares { get; set; }
    public double FillValue { get; set; }
    public double FillPrice { get; set; }
    public double CreatedAt { get; set; }
    public double SubmittedAt { get; set; }
    public double FilledAt { get; set; }
}

public sealed class OrderJournal
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OrderJournal(string dataDir) => _path = Path.Combine(dataDir, "pending-orders.json");

    public string Begin(PendingOrderRecord record)
    {
        var records = Load();
        record.IntentId = Guid.NewGuid().ToString();
        record.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        records[record.IntentId] = record;
        Save(records);
        return record.IntentId;
    }

    public void Submitted(string intentId, string orderId) => Update(intentId, record =>
    {
        record.OrderId = orderId;
        record.SubmittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    });

    public void Filled(string intentId, OrderFill fill) => Update(intentId, record =>
    {
        record.FillStatus = fill.Status;
        record.FillShares = fill.Shares;
        record.FillValue = fill.Value;
        record.FillPrice = fill.Price;
        record.FilledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    });

    public void Complete(string intentId)
    {
        var records = Load();
        if (records.Remove(intentId)) Save(records);
    }

    public List<PendingOrderRecord> Pending() => Load().Values.OrderBy(record => record.CreatedAt).ToList();

    private void Update(string intentId, Action<PendingOrderRecord> update)
    {
        var records = Load();
        if (!records.TryGetValue(intentId, out var record))
            throw new KeyNotFoundException($"Unknown order intent: {intentId}");
        update(record);
        Save(records);
    }

    private Dictionary<string, PendingOrderRecord> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, PendingOrderRecord>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    private void Save(Dictionary<string, PendingOrderRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(tmp, _path, true);
    }
}

public sealed class InstanceLock : IDisposable
{
    private readonly string _path;
    private bool _acquired;

    public InstanceLock(string dataDir) => _path = Path.Combine(dataDir, "bot.lock");

    public bool Acquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, new { pid = Environment.ProcessId });
                _acquired = true;
                return true;
            }
            catch (IOException)
            {
                if (OwnerAlive()) return false;
                try { File.Delete(_path); } catch (IOException) { }
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (!_acquired) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId)
                File.Delete(_path);
        }
        catch (Exception) { }
        _acquired = false;
    }

    private bool OwnerAlive()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var process = Process.GetProcessById(doc.RootElement.GetProperty("pid").GetInt32());
            return !process.HasExited;
        }
        catch (Exception)
        {
            try { return File.GetLastWriteTimeUtc(_path) > DateTime.UtcNow.AddSeconds(-5); }
            catch (Exception) { return false; }
        }
    }
}
