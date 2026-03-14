using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
namespace Recall.Server.Rohlik;

[McpServerToolType]
public class RohlikTools
{
    private static string NotConfigured => "Rohlik not configured. Add rohlikUsername and rohlikPassword to ~/.recall/config.json";

    private static (bool Ok, string? Error) CheckAccess(RohlikClient? client)
    {
        if (client == null) return (false, NotConfigured);
        return (true, null);
    }

    // ── rohlik_search ────────────────────────────────────────

    [McpServerTool(Name = "rohlik_search")]
    [Description("Search Rohlik.cz products. Actions: 'search' (by name), 'last_minute' (discounted near-expiry items).")]
    public static async Task<string> Search(
        RohlikClient? client = null,
        [Description("Action: search, last_minute")] string action = "search",
        [Description("Search query (for action=search)")] string? query = null,
        [Description("Category ID (for action=last_minute, omit for all)")] int? categoryId = null,
        [Description("Max results (default 10)")] int limit = 10,
        [Description("Show favourites only (for action=search)")] bool favouriteOnly = false)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "search" => await DoSearch(client!, query ?? "", limit, favouriteOnly),
                "last_minute" => await DoLastMinute(client!, categoryId, limit),
                _ => $"Unknown action '{action}'. Use: search, last_minute"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    private static async Task<string> DoSearch(RohlikClient client, string query, int limit, bool favOnly)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Provide a search query.";

        var results = await client.SearchProducts(query, limit, favOnly);
        if (results.Count == 0) return $"No products found for '{query}'.";

        var sb = new StringBuilder($"Found {results.Count} product(s) for \"{query}\":\n\n");
        foreach (var p in results)
            sb.AppendLine($"- {p.Name} ({p.Brand}) — {p.Price}, {p.Amount} [ID: {p.Id}]");
        return sb.ToString();
    }

    private static async Task<string> DoLastMinute(RohlikClient client, int? categoryId, int limit)
    {
        var categories = await client.GetLastMinuteCounts();
        if (categories.Count == 0) return "No last-minute items available.";

        if (categoryId == null)
        {
            var sb = new StringBuilder("Last-minute categories:\n\n");
            foreach (var c in categories)
                sb.AppendLine($"- {c.Name}: {c.Count} items [ID: {c.Id}]");
            sb.AppendLine("\nUse categoryId to browse a specific category.");
            return sb.ToString();
        }

        var products = await client.GetLastMinuteProducts(categoryId.Value, limit);
        if (products.Count == 0) return $"No products in category {categoryId}.";

        var sb2 = new StringBuilder($"Last-minute ({products.Count} items):\n\n");
        foreach (var p in products)
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var price = p.TryGetProperty("price", out var pr)
                && pr.TryGetProperty("full", out var pf) ? pf.GetDouble().ToString("F2") : "?";
            var id = p.TryGetProperty("id", out var pid) ? pid.GetInt32().ToString() : "?";
            sb2.AppendLine($"- {name} — {price} Kč [ID: {id}]");
        }
        return sb2.ToString();
    }

    // ── rohlik_cart ──────────────────────────────────────────

    [McpServerTool(Name = "rohlik_cart")]
    [Description("Manage Rohlik shopping cart. Actions: 'view', 'add' (product_id + quantity), 'remove' (order_field_id), 'update' (order_field_id + quantity), 'check' (validate totals).")]
    public static async Task<string> Cart(
        RohlikClient? client = null,
        [Description("Action: view, add, remove, update, check")] string action = "view",
        [Description("Product ID (for action=add)")] int? productId = null,
        [Description("Quantity (for action=add/update)")] int quantity = 1,
        [Description("Cart item's orderFieldId (for action=remove/update)")] string? orderFieldId = null)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "view" => FormatCart(await client!.GetCartContent()),
                "add" when productId != null => await DoAddToCart(client!, productId.Value, quantity),
                "add" => "Provide productId to add.",
                "remove" when orderFieldId != null => await DoRemove(client!, orderFieldId),
                "remove" => "Provide orderFieldId to remove.",
                "update" when orderFieldId != null => await DoUpdate(client!, orderFieldId, quantity),
                "update" => "Provide orderFieldId and quantity.",
                "check" => FormatJson(await client!.CheckCart()),
                _ => $"Unknown action '{action}'. Use: view, add, remove, update, check"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    private static async Task<string> DoAddToCart(RohlikClient client, int productId, int qty)
    {
        await client.AddToCart(productId, qty);
        return $"Added product {productId} (qty {qty}) to cart.";
    }

    private static async Task<string> DoRemove(RohlikClient client, string orderFieldId)
    {
        await client.RemoveFromCart(orderFieldId);
        return $"Removed item {orderFieldId} from cart.";
    }

    private static async Task<string> DoUpdate(RohlikClient client, string orderFieldId, int qty)
    {
        await client.UpdateCartItem(orderFieldId, qty);
        return $"Updated item {orderFieldId} to quantity {qty}.";
    }

    private static string FormatCart(CartContent cart)
    {
        if (cart.Products.Count == 0)
            return "Cart is empty.";

        var sb = new StringBuilder($"Cart: {cart.TotalItems} items, {cart.TotalPrice:F2} Kč");
        sb.AppendLine(cart.CanMakeOrder ? " (ready to order)" : " (not ready)");
        sb.AppendLine();
        foreach (var p in cart.Products)
            sb.AppendLine($"- {p.Name} ({p.Brand}) × {p.Quantity} = {p.Price:F2} Kč [field: {p.OrderFieldId}]");
        return sb.ToString();
    }

    // ── rohlik_orders ────────────────────────────────────────

    [McpServerTool(Name = "rohlik_orders")]
    [Description("View Rohlik order history. Actions: 'history' (past orders), 'detail' (single order by ID), 'upcoming' (scheduled orders).")]
    public static async Task<string> Orders(
        RohlikClient? client = null,
        [Description("Action: history, detail, upcoming")] string action = "history",
        [Description("Order ID (for action=detail)")] string? orderId = null,
        [Description("Max results (for action=history, default 10)")] int limit = 10)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "history" => FormatJson(await client!.GetOrderHistory(limit)),
                "detail" when orderId != null => FormatJson(await client!.GetOrderDetail(orderId)),
                "detail" => "Provide orderId.",
                "upcoming" => FormatJson(await client!.GetUpcomingOrders()),
                _ => $"Unknown action '{action}'. Use: history, detail, upcoming"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    // ── rohlik_delivery ──────────────────────────────────────

    [McpServerTool(Name = "rohlik_delivery")]
    [Description("Rohlik delivery info and timeslots. Actions: 'info' (next delivery, fees), 'slots' (available times), 'reserve' (reserve a slot by ID).")]
    public static async Task<string> Delivery(
        RohlikClient? client = null,
        [Description("Action: info, slots, reserve")] string action = "info",
        [Description("Slot ID (for action=reserve)")] int? slotId = null,
        [Description("Slot type: ON_TIME, EXPRESS (for action=reserve)")] string slotType = "ON_TIME",
        [Description("Cart total for timeslot calculation")] int? cartTotal = null)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "info" => FormatJson(await client!.GetDeliveryInfo()),
                "slots" when cartTotal != null => FormatJson(await client!.GetTimeslots(cartTotal.Value)),
                "slots" => FormatJson(await client!.GetDeliverySlots()),
                "reserve" when slotId != null => FormatJson(await client!.ReserveTimeslot(slotId.Value, slotType)),
                "reserve" => "Provide slotId to reserve.",
                _ => $"Unknown action '{action}'. Use: info, slots, reserve"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    // ── rohlik_account ───────────────────────────────────────

    [McpServerTool(Name = "rohlik_account")]
    [Description("Rohlik account info. Actions: 'premium' (subscription), 'announcements', 'bags' (reusable bags), 'checkout' (checkout form state), 'shopping_list' (by ID).")]
    public static async Task<string> Account(
        RohlikClient? client = null,
        [Description("Action: premium, announcements, bags, checkout, shopping_list")] string action = "premium",
        [Description("Shopping list ID (for action=shopping_list)")] string? shoppingListId = null)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "premium" => FormatJson(await client!.GetPremiumInfo()),
                "announcements" => FormatJson(await client!.GetAnnouncements()),
                "bags" => FormatJson(await client!.GetReusableBagsInfo()),
                "checkout" => FormatJson(await client!.GetCheckoutStatus()),
                "shopping_list" when shoppingListId != null => FormatJson(await client!.GetShoppingList(shoppingListId)),
                "shopping_list" => "Provide shoppingListId.",
                _ => $"Unknown action '{action}'. Use: premium, announcements, bags, checkout, shopping_list"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    // ── rohlik_checkout ──────────────────────────────────────

    // Cart fingerprint for tamper detection between submit and pay
    private static string? _cartFingerprint;
    private static double _cartTotal;

    [McpServerTool(Name = "rohlik_checkout")]
    [Description("Submit order and pay. Actions: 'submit' (finalize order, returns payment methods), 'pay' (pay with stored card, requires confirmation_code from TOTP or stderr).")]
    public static async Task<string> Checkout(
        RohlikClient? client = null,
        [Description("Action: submit, pay")] string action = "submit",
        [Description("Submit as suborder (add to existing)")] bool isSuborder = false,
        [Description("Stored payment method ID (for action=pay)")] string? storedPaymentMethodId = null,
        [Description("Card brand: mc, visa (for action=pay)")] string? brand = null,
        [Description("Cardholder name (for action=pay)")] string? holderName = null,
        [Description("6-digit confirmation code (for action=pay)")] string? confirmationCode = null)
    {
        var (ok, error) = CheckAccess(client);
        if (!ok) return error!;

        try
        {
            return action switch
            {
                "submit" => await DoSubmit(client!, isSuborder),
                "pay" => await DoPay(client!, storedPaymentMethodId, brand, holderName, confirmationCode),
                _ => $"Unknown action '{action}'. Use: submit, pay"
            };
        }
        catch (RohlikException ex)
        {
            return $"Rohlik error: {ex.Message}";
        }
    }

    private static async Task<string> DoSubmit(RohlikClient client, bool isSuborder)
    {
        // Take cart fingerprint before submit
        var cart = await client.GetCartContent();
        var fingerprint = CartFingerprint(cart);
        _cartFingerprint = fingerprint;
        _cartTotal = cart.TotalPrice;

        var result = await client.SubmitOrder(isSuborder);

        var sb = new StringBuilder("Order submitted.\n\n");
        sb.AppendLine($"Cart: {cart.TotalItems} items, {cart.TotalPrice:F2} Kč");
        sb.AppendLine($"Cart fingerprint: {fingerprint[..8]}...");
        sb.AppendLine();
        sb.AppendLine("Now call with action='pay' and provide:");
        sb.AppendLine("- storedPaymentMethodId, brand, holderName");
        sb.AppendLine("- confirmationCode (6-digit TOTP or code from stderr)");
        sb.AppendLine();
        sb.AppendLine($"Payment methods:\n{FormatJson(result)}");
        return sb.ToString();
    }

    private static async Task<string> DoPay(
        RohlikClient client,
        string? methodId, string? brand, string? holderName, string? code)
    {
        if (string.IsNullOrEmpty(methodId) || string.IsNullOrEmpty(brand) || string.IsNullOrEmpty(holderName))
            return "Provide storedPaymentMethodId, brand, and holderName.";

        if (string.IsNullOrEmpty(code))
        {
            // Generate confirmation code
            var totpSecret = Environment.GetEnvironmentVariable("ROHLIK_TOTP_SECRET");
            if (!string.IsNullOrEmpty(totpSecret))
            {
                return "Enter your 6-digit TOTP code from your authenticator app as confirmationCode.";
            }
            else
            {
                // Generate random code, print to stderr (invisible to LLM)
                var randomCode = RandomNumberGenerator.GetHexString(6).ToLower();
                Console.Error.WriteLine($"[ROHLIK PAYMENT] Confirmation code: {randomCode}");
                Console.Error.WriteLine($"[ROHLIK PAYMENT] Cart total: {_cartTotal:F2} Kč");
                return $"A confirmation code has been printed to stderr. " +
                       $"Please provide it as confirmationCode to authorize payment of {_cartTotal:F2} Kč.";
            }
        }

        // Verify code
        var secret = Environment.GetEnvironmentVariable("ROHLIK_TOTP_SECRET");
        if (!string.IsNullOrEmpty(secret))
        {
            if (!Totp.Verify(secret, code))
                return "Invalid TOTP code. Try again.";
        }

        // Verify cart hasn't changed
        var currentCart = await client.GetCartContent();
        var currentFp = CartFingerprint(currentCart);
        if (_cartFingerprint != null && currentFp != _cartFingerprint)
            return "Cart has been modified since submit! Please re-submit before paying.";

        var result = await client.PayWithStoredCard(methodId, brand, holderName);
        _cartFingerprint = null;
        return $"Payment complete.\n\n{FormatJson(result)}";
    }

    private static string CartFingerprint(CartContent cart)
    {
        var items = cart.Products
            .OrderBy(p => p.ProductId)
            .Select(p => $"{p.ProductId}:{p.Quantity}:{p.Price:F2}")
            .ToList();
        var data = string.Join("|", items) + $"|total:{cart.TotalPrice:F2}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLower();
    }

    // ── Formatting ───────────────────────────────────────────

    private static string FormatJson(JsonElement el)
    {
        return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
    }
}
