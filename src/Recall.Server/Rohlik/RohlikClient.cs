using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Recall.Server.Rohlik;

/// <summary>
/// HTTP client for the Rohlik.cz grocery API.
/// Manages session cookies, rate limiting, and browser-like headers.
/// </summary>
public class RohlikClient
{
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();
    private long _lastRequestMs;

    private int? _userId;
    private int? _addressId;
    private bool _loggedIn;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RohlikClient(string username, string password, string? baseUrl = null)
    {
        _username = username;
        _password = password;
        _baseUrl = (baseUrl ?? "https://www.rohlik.cz").TrimEnd('/');

        var handler = new HttpClientHandler { CookieContainer = _cookies };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        // Browser-like default headers
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "cs-CZ,cs;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", _baseUrl);
        _http.DefaultRequestHeaders.Add("Origin", _baseUrl);
        _http.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"119\", \"Chromium\";v=\"119\", \"Not?A_Brand\";v=\"24\"");
        _http.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        _http.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"macOS\"");
        _http.DefaultRequestHeaders.Add("sec-fetch-dest", "empty");
        _http.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
        _http.DefaultRequestHeaders.Add("sec-fetch-site", "same-origin");
    }

    // ── Rate Limiting ────────────────────────────────────────

    private async Task RateLimit()
    {
        await _rateLock.WaitAsync();
        try
        {
            var now = _rateClock.ElapsedMilliseconds;
            var gap = now - _lastRequestMs;
            if (gap < 100)
                await Task.Delay((int)(100 - gap));
            _lastRequestMs = _rateClock.ElapsedMilliseconds;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    // ── Core HTTP ────────────────────────────────────────────

    private async Task<JsonElement> Request(string path, HttpMethod? method = null, object? body = null)
    {
        await RateLimit();

        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (body != null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts),
                Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new RohlikException(
                $"HTTP {(int)response.StatusCode} on {path}: {errorBody[..Math.Min(300, errorBody.Length)]}",
                (int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json))
            return default;

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private async Task<JsonElement> Get(string path) => await Request(path);
    private async Task<JsonElement> Post(string path, object? body = null) => await Request(path, HttpMethod.Post, body);
    private async Task<JsonElement> Put(string path, object body) => await Request(path, HttpMethod.Put, body);
    private async Task<JsonElement> Patch(string path, object body) => await Request(path, HttpMethod.Patch, body);
    private async Task<JsonElement> Delete(string path) => await Request(path, HttpMethod.Delete);

    /// <summary>Get .data property from response, or the response itself if no .data wrapper.</summary>
    private static JsonElement Unwrap(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("data", out var data))
            return data;
        return el;
    }

    // ── Auth ─────────────────────────────────────────────────

    public async Task EnsureLoggedIn()
    {
        if (_loggedIn) return;
        await Login();
    }

    public async Task Login()
    {
        var resp = await Post("/services/frontend-service/login", new
        {
            email = _username,
            password = _password,
            name = ""
        });

        // Accept status 200, 202, or missing (some responses don't include it)
        if (resp.TryGetProperty("status", out var st))
        {
            var status = st.GetInt32();
            if (status != 200 && status != 202)
                throw new RohlikException($"Login failed with status {status}");
        }

        var data = Unwrap(resp);
        if (data.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var uid))
            _userId = uid.GetInt32();
        if (data.TryGetProperty("address", out var addr) && addr.TryGetProperty("id", out var aid))
            _addressId = aid.GetInt32();

        if (_userId == null)
            throw new RohlikException("Login succeeded but no user data received.");

        _loggedIn = true;
    }

    /// <summary>Execute an action with auto-login and retry on 401.</summary>
    private async Task<T> WithSession<T>(Func<Task<T>> action)
    {
        await EnsureLoggedIn();
        try
        {
            return await action();
        }
        catch (RohlikException ex) when (ex.StatusCode == 401)
        {
            _loggedIn = false;
            await Login();
            return await action();
        }
    }

    // ── Search ───────────────────────────────────────────────

    public Task<List<SearchResult>> SearchProducts(string query, int limit = 10, bool favouriteOnly = false)
        => WithSession(async () =>
    {
        var filterData = JsonSerializer.Serialize(new { filters = Array.Empty<object>() });
        var path = $"/services/frontend-service/search-metadata?search={Uri.EscapeDataString(query)}" +
                   $"&offset=0&limit={limit + 5}&companyId=1&filterData={Uri.EscapeDataString(filterData)}&canCorrect=true";

        var resp = Unwrap(await Get(path));
        var results = new List<SearchResult>();

        if (!resp.TryGetProperty("productList", out var list))
            return results;

        foreach (var p in list.EnumerateArray())
        {
            // Skip promoted/sponsored
            if (p.TryGetProperty("badge", out var badges))
            {
                var skip = false;
                foreach (var b in badges.EnumerateArray())
                    if (b.TryGetProperty("slug", out var slug) && slug.GetString() == "promoted")
                    { skip = true; break; }
                if (skip) continue;
            }

            if (favouriteOnly && (!p.TryGetProperty("favourite", out var fav) || !fav.GetBoolean()))
                continue;

            if (results.Count >= limit) break;

            var price = p.TryGetProperty("price", out var pr)
                ? $"{pr.GetProperty("full").GetDouble()} {pr.GetProperty("currency").GetString()}"
                : "?";

            results.Add(new SearchResult
            {
                Id = p.GetProperty("productId").GetInt32(),
                Name = p.GetProperty("productName").GetString() ?? "",
                Price = price,
                Brand = p.TryGetProperty("brand", out var br) ? br.GetString() ?? "" : "",
                Amount = p.TryGetProperty("textualAmount", out var ta) ? ta.GetString() ?? "" : "",
            });
        }

        return results;
    });

    // ── Cart ─────────────────────────────────────────────────

    public Task<CartContent> GetCartContent()
        => WithSession(async () =>
    {
        var data = Unwrap(await Get("/services/frontend-service/v2/cart"));
        return ParseCart(data);
    });

    public Task<int> AddToCart(int productId, int quantity)
        => WithSession(async () =>
    {
        await Post("/services/frontend-service/v2/cart", new
        {
            actionId = (string?)null,
            productId,
            quantity,
            recipeId = (string?)null,
            source = "true:Shopping Lists"
        });
        return productId;
    });

    public Task<bool> RemoveFromCart(string orderFieldId)
        => WithSession(async () =>
    {
        await Delete($"/services/frontend-service/v2/cart?orderFieldId={orderFieldId}");
        return true;
    });

    public Task<JsonElement> UpdateCartItem(string orderFieldId, int quantity)
        => WithSession(async () =>
    {
        return Unwrap(await Put(
            $"/services/frontend-service/v2/cart-review/item/{orderFieldId}",
            new { quantity }));
    });

    public Task<JsonElement> CheckCart()
        => WithSession(async () => Unwrap(await Get("/services/frontend-service/v2/cart-review/check-cart")));

    // ── Orders ───────────────────────────────────────────────

    public Task<JsonElement> GetOrderHistory(int limit = 10)
        => WithSession(async () => Unwrap(await Get($"/api/v3/orders/delivered?offset=0&limit={limit}")));

    public Task<JsonElement> GetOrderDetail(string orderId)
        => WithSession(async () => Unwrap(await Get($"/api/v3/orders/{orderId}")));

    public Task<JsonElement> GetUpcomingOrders()
        => WithSession(async () => Unwrap(await Get("/api/v3/orders/upcoming")));

    // ── Delivery ─────────────────────────────────────────────

    public Task<JsonElement> GetDeliveryInfo()
        => WithSession(async () => Unwrap(await Get(
            "/services/frontend-service/first-delivery?reasonableDeliveryTime=true")));

    public Task<JsonElement> GetDeliverySlots()
        => WithSession(async () =>
    {
        if (_userId == null || _addressId == null)
            throw new RohlikException("User ID or Address ID not available.");
        return Unwrap(await Get(
            $"/services/frontend-service/timeslots-api/0?userId={_userId}&addressId={_addressId}&reasonableDeliveryTime=true"));
    });

    public Task<JsonElement> GetTimeslots(int cartTotal)
        => WithSession(async () =>
    {
        if (_userId == null || _addressId == null)
            throw new RohlikException("User ID or Address ID not available.");
        return Unwrap(await Get(
            $"/services/frontend-service/timeslots-api/{cartTotal}?userId={_userId}&addressId={_addressId}&reasonableDeliveryTime=false"));
    });

    public Task<JsonElement> ReserveTimeslot(int slotId, string slotType = "ON_TIME")
        => WithSession(async () => Unwrap(await Post(
            "/services/frontend-service/v1/timeslot-reservation",
            new { slotId, slotType })));

    // ── Account ──────────────────────────────────────────────

    public Task<JsonElement> GetPremiumInfo()
        => WithSession(async () => Unwrap(await Get("/services/frontend-service/premium/profile")));

    public Task<JsonElement> GetAnnouncements()
        => WithSession(async () => Unwrap(await Get("/services/frontend-service/announcements/top")));

    public Task<JsonElement> GetReusableBagsInfo()
        => WithSession(async () => Unwrap(await Get("/api/v1/reusable-bags/user-info")));

    public Task<JsonElement> GetCheckoutStatus()
        => WithSession(async () => Unwrap(await Get("/api/v2/checkout")));

    public Task<JsonElement> GetShoppingList(string listId)
        => WithSession(async () => Unwrap(await Get($"/api/v1/shopping-lists/id/{listId}")));

    // ── Checkout ─────────────────────────────────────────────

    public Task<JsonElement> SubmitOrder(bool isSuborder = false)
        => WithSession(async () =>
    {
        if (isSuborder)
            await Patch("/api/v2/checkout/suborder", new { isSuborder = true });

        return Unwrap(await Post("/api/v2/checkout/submit-web", new { agreementList = Array.Empty<object>() }));
    });

    public Task<JsonElement> PayWithStoredCard(string storedPaymentMethodId, string brand, string holderName)
        => WithSession(async () =>
    {
        return Unwrap(await Post("/services/frontend-service/order-review/payment/adyen-pay", new
        {
            adyenPayload = new
            {
                paymentMethod = new
                {
                    type = "scheme",
                    storedPaymentMethodId,
                    brand,
                    holderName
                },
                browserInfo = new
                {
                    acceptHeader = "*/*",
                    colorDepth = 24,
                    language = "cs-CZ",
                    javaEnabled = false,
                    screenHeight = 1440,
                    screenWidth = 2560,
                    userAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36",
                    timeZoneOffset = -60
                },
                origin = _baseUrl,
                clientStateDataIndicator = true
            }
        }));
    });

    // ── Last Minute ──────────────────────────────────────────

    public static readonly (int Id, string Name)[] LastMinuteCategories =
    [
        (300101000, "Pekárna a cukrárna"),
        (300102000, "Ovoce a zelenina"),
        (300103000, "Maso a ryby"),
        (300104000, "Uzeniny a lahůdky"),
        (300105000, "Mléčné a chlazené"),
        (300106000, "Trvanlivé"),
        (300108000, "Nápoje"),
        (300110000, "Dítě"),
        (300111000, "Domácnost a zahrada"),
        (300112000, "Zvíře"),
        (300112393, "Speciální výživa"),
        (300121429, "Plant Based"),
        (300124206, "Kosmetika"),
    ];

    public Task<List<LastMinuteCategory>> GetLastMinuteCounts()
        => WithSession(async () =>
    {
        var results = new List<LastMinuteCategory>();
        foreach (var (id, name) in LastMinuteCategories)
        {
            try
            {
                var resp = await Get($"/api/v1/categories/last-minute/{id}/products/count");
                var count = 0;
                if (resp.TryGetProperty("results", out var r))
                    count = r.GetInt32();
                else if (resp.TryGetProperty("data", out var d) && d.TryGetProperty("results", out var dr))
                    count = dr.GetInt32();

                if (count > 0)
                    results.Add(new LastMinuteCategory { Id = id, Name = name, Count = count });
            }
            catch { /* category empty or unavailable */ }
        }
        return results;
    });

    public Task<List<JsonElement>> GetLastMinuteProducts(int categoryId, int limit = 30)
        => WithSession(async () =>
    {
        var allIds = new List<int>();
        var page = 0;
        while (allIds.Count < limit)
        {
            var resp = await Get($"/api/v1/categories/last-minute/{categoryId}/products?page={page}&size=14&sort=expiration-asc");
            var ids = new List<int>();
            if (resp.TryGetProperty("productIds", out var pids))
                foreach (var id in pids.EnumerateArray())
                    ids.Add(id.GetInt32());

            if (ids.Count == 0) break;
            allIds.AddRange(ids);
            if (ids.Count < 14) break;
            page++;
        }

        if (allIds.Count == 0) return [];
        allIds = allIds.Take(limit).ToList();

        // Fetch product cards in batches of 50
        var products = new List<JsonElement>();
        for (var i = 0; i < allIds.Count; i += 50)
        {
            var batch = allIds.Skip(i).Take(50);
            var qs = string.Join("&", batch.Select(id => $"products={id}"));
            var resp = await Get($"/api/v1/products/card?{qs}");
            if (resp.ValueKind == JsonValueKind.Array)
                foreach (var p in resp.EnumerateArray())
                    products.Add(p);
            else if (resp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var p in data.EnumerateArray())
                    products.Add(p);
        }
        return products;
    });

    // ── Helpers ───────────────────────────────────────────────

    private static CartContent ParseCart(JsonElement data)
    {
        var cart = new CartContent
        {
            TotalPrice = data.TryGetProperty("totalPrice", out var tp) ? tp.GetDouble() : 0,
            CanMakeOrder = data.TryGetProperty("submitConditionPassed", out var sc) && sc.GetBoolean(),
        };

        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in items.EnumerateObject())
            {
                var v = prop.Value;
                cart.Products.Add(new CartItem
                {
                    ProductId = prop.Name,
                    OrderFieldId = v.TryGetProperty("orderFieldId", out var ofi) ? ofi.GetString() ?? "" : "",
                    Name = v.TryGetProperty("productName", out var pn) ? pn.GetString() ?? "" : "",
                    Quantity = v.TryGetProperty("quantity", out var q) ? q.GetInt32() : 0,
                    Price = v.TryGetProperty("price", out var pr) ? pr.GetDouble() : 0,
                    Category = v.TryGetProperty("primaryCategoryName", out var cn) ? cn.GetString() ?? "" : "",
                    Brand = v.TryGetProperty("brand", out var br) ? br.GetString() ?? "" : "",
                });
            }
        }

        cart.TotalItems = cart.Products.Count;
        return cart;
    }
}

// ── Types ────────────────────────────────────────────────────

public class RohlikException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public class SearchResult
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Amount { get; set; } = "";
}

public class CartContent
{
    public double TotalPrice { get; set; }
    public int TotalItems { get; set; }
    public bool CanMakeOrder { get; set; }
    public List<CartItem> Products { get; set; } = [];
}

public class CartItem
{
    public string ProductId { get; set; } = "";
    public string OrderFieldId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public double Price { get; set; }
    public string Category { get; set; } = "";
    public string Brand { get; set; } = "";
}

public class LastMinuteCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}
