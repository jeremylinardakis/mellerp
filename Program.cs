using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

const string Store = "https://mellowclimbing.com";
const string StatePath = "seen.json";
const int MaxIndividualAlerts = 5;

var topic = Environment.GetEnvironmentVariable("NTFY_TOPIC");
if (string.IsNullOrWhiteSpace(topic))
{
    Console.Error.WriteLine("NTFY_TOPIC is not set.");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; mellow-drop-watcher/1.0)");

// 1. Fetch the catalog. Any failure exits WITHOUT touching seen.json,
//    so a bad response never makes the next run think everything is new.
List<Product> products;
try
{
    products = await FetchAll(http);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fetch failed: {ex.Message}");
    return 1;
}

if (products.Count == 0)
{
    Console.Error.WriteLine("Catalog came back empty; skipping so state isn't wiped.");
    return 1;
}

var current = products.ToDictionary(
    p => p.Id,
    p => new ProductState(p.Title, p.Handle, p.Variants.ToDictionary(v => v.Id, v => v.Available)));

// 2. First run: just record what exists. No alerts.
if (!File.Exists(StatePath))
{
    Save(current);
    Console.WriteLine($"First run: recorded {current.Count} products. No alerts sent.");
    return 0;
}

var previous = JsonSerializer.Deserialize<Dictionary<long, ProductState>>(
    File.ReadAllText(StatePath), Shared.Json) ?? new();

// 3. Diff: new products, and variants that flipped to available.
var alerts = new List<(string Message, string Url)>();
foreach (var p in products)
{
    var url = $"{Store}/products/{p.Handle}";

    if (!previous.TryGetValue(p.Id, out var old))
    {
        alerts.Add(($"New: {p.Title}", url));
        continue;
    }

    var flipped = p.Variants
        .Where(v => v.Available && !(old.Variants.TryGetValue(v.Id, out var was) && was))
        .ToList();

    if (flipped.Count > 0)
    {
        var names = flipped.Select(v => v.Title).Where(t => t != "Default Title").ToList();
        var detail = names.Count > 0 ? $" ({string.Join(", ", names)})" : "";
        alerts.Add(($"Back in stock: {p.Title}{detail}", url));
    }
}

// 4. Notify. If sending fails, state is not saved, so the next run retries.
try
{
    if (alerts.Count > MaxIndividualAlerts)
    {
        var body = string.Join("\n", alerts.Take(10).Select(a => a.Message));
        if (alerts.Count > 10) body += $"\n…and {alerts.Count - 10} more";
        await Notify(http, topic, $"Mellow: {alerts.Count} changes", body,
            $"{Store}/collections/all?sort_by=created-descending");
    }
    else
    {
        foreach (var (message, url) in alerts)
            await Notify(http, topic, "Mellow drop", message, url);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Notification failed: {ex.Message}");
    return 1;
}

Save(current);
Console.WriteLine($"Checked {current.Count} products, sent {alerts.Count} alert(s).");
return 0;

static async Task<List<Product>> FetchAll(HttpClient http)
{
    var all = new List<Product>();
    for (var page = 1; page <= 20; page++)
    {
        var resp = await http.GetFromJsonAsync<ShopifyResponse>(
            $"{Store}/products.json?limit=250&page={page}", Shared.Json)
            ?? throw new InvalidOperationException("Null response");

        all.AddRange(resp.Products);
        if (resp.Products.Count < 250) break;
    }
    return all;
}

static async Task Notify(HttpClient http, string topic, string title, string message, string click)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{topic}")
    {
        Content = new StringContent(message, Encoding.UTF8, "text/plain")
    };
    // Headers must be ASCII, so product titles (® etc.) go in the body only.
    req.Headers.Add("Title", title);
    req.Headers.Add("Tags", "mountain");
    req.Headers.Add("Click", click);

    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
}

static void Save(Dictionary<long, ProductState> state)
{
    var tmp = StatePath + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(state, Shared.Json));
    File.Move(tmp, StatePath, overwrite: true);
}

record ShopifyResponse(List<Product> Products);
record Product(long Id, string Title, string Handle, DateTimeOffset? PublishedAt, List<Variant> Variants);
record Variant(long Id, string Title, bool Available);
record ProductState(string Title, string Handle, Dictionary<long, bool> Variants);

static class Shared
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
