using System.Text.Json;

namespace Recall.Tests;

/// <summary>
/// Tests that Rohlik API responses are parsed correctly.
/// Fixtures are real API responses captured on 2026-03-15.
/// If Rohlik changes their API again, these tests will catch it
/// (update fixtures with fresh responses and fix the parsing).
/// </summary>
public class RohlikParsingTests
{
    private static JsonElement LoadFixture(string name)
    {
        var path = Path.Combine("Fixtures", name);
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── Product Card (used by last_minute) ─────────────────────

    [Fact]
    public void ProductCard_has_prices_not_price()
    {
        var products = LoadFixture("product_card.json");
        var first = products[0];

        // Old field "price" should NOT exist
        Assert.False(first.TryGetProperty("price", out _),
            "Card API returned 'price' — if this started working again, check if both old and new formats need support");

        // New field "prices" must exist
        Assert.True(first.TryGetProperty("prices", out var prices));
        Assert.True(prices.TryGetProperty("originalPrice", out var op));
        Assert.True(op.GetDouble() > 0);
        Assert.True(prices.TryGetProperty("currency", out var cur));
        Assert.Equal("CZK", cur.GetString());
    }

    [Fact]
    public void ProductCard_has_productId_not_id()
    {
        var products = LoadFixture("product_card.json");
        var first = products[0];

        Assert.False(first.TryGetProperty("id", out _),
            "Card API returned 'id' — field name may have reverted");
        Assert.True(first.TryGetProperty("productId", out var pid));
        Assert.True(pid.GetInt32() > 0);
    }

    [Fact]
    public void ProductCard_name_exists()
    {
        var products = LoadFixture("product_card.json");
        var first = products[0];

        Assert.True(first.TryGetProperty("name", out var name));
        Assert.False(string.IsNullOrEmpty(name.GetString()));
    }

    [Fact]
    public void ProductCard_salePrice_is_nullable()
    {
        var products = LoadFixture("product_card.json");
        var first = products[0];
        var prices = first.GetProperty("prices");

        // salePrice should exist but may be null
        Assert.True(prices.TryGetProperty("salePrice", out var sp));
        // It's either null or a valid double
        Assert.True(sp.ValueKind == JsonValueKind.Null || sp.ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public void ProductCard_parsing_matches_code()
    {
        // Replicate the exact parsing logic from RohlikTools.DoLastMinute
        var products = LoadFixture("product_card.json");
        foreach (var p in products.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var id = p.TryGetProperty("productId", out var pid) ? pid.GetInt32().ToString() : "?";

            var priceStr = "?";
            if (p.TryGetProperty("prices", out var prices))
            {
                if (prices.TryGetProperty("salePrice", out var sp) && sp.ValueKind != JsonValueKind.Null)
                    priceStr = $"{sp.GetDouble():F2} (sleva z {prices.GetProperty("originalPrice").GetDouble():F2})";
                else if (prices.TryGetProperty("originalPrice", out var op))
                    priceStr = op.GetDouble().ToString("F2");
            }

            Assert.NotEqual("?", name);
            Assert.NotEqual("?", id);
            Assert.NotEqual("?", priceStr);
        }
    }

    // ── Search Results ─────────────────────────────────────────

    [Fact]
    public void Search_has_productList_with_price_full()
    {
        var raw = LoadFixture("search_results.json");

        // Response may be wrapped in {status, data} — unwrap like the code does
        var data = raw.TryGetProperty("data", out var d) ? d : raw;

        Assert.True(data.TryGetProperty("productList", out var list));

        var first = list.EnumerateArray().First();
        Assert.True(first.TryGetProperty("productId", out _));
        Assert.True(first.TryGetProperty("productName", out _));

        // Search uses price.full (different from card API!)
        Assert.True(first.TryGetProperty("price", out var price));
        Assert.True(price.TryGetProperty("full", out var full));
        Assert.True(full.GetDouble() > 0);
        Assert.True(price.TryGetProperty("currency", out _));
    }

    // ── Timeslots ──────────────────────────────────────────────

    [Fact]
    public void Timeslots_has_expected_structure()
    {
        var raw = LoadFixture("timeslots.json");
        var data = raw.TryGetProperty("data", out var d) ? d : raw;

        Assert.True(data.TryGetProperty("preselectedSlots", out var preselected));
        Assert.True(data.TryGetProperty("availabilityDays", out var days));

        // At least one day with slots
        var firstDay = days.EnumerateArray().First();
        Assert.True(firstDay.TryGetProperty("date", out _));
        Assert.True(firstDay.TryGetProperty("slots", out var slots));

        // Slots are keyed by hour
        Assert.Equal(JsonValueKind.Object, slots.ValueKind);
    }

    [Fact]
    public void Timeslots_15min_slots_have_required_fields()
    {
        var raw = LoadFixture("timeslots.json");
        var data = raw.TryGetProperty("data", out var d) ? d : raw;
        var days = data.GetProperty("availabilityDays");
        var firstDay = days.EnumerateArray().First();
        var slots = firstDay.GetProperty("slots");

        // Get first hour's slots
        var firstHour = slots.EnumerateObject().First();
        var firstSlot = firstHour.Value.EnumerateArray().First();

        Assert.True(firstSlot.TryGetProperty("slotId", out _));
        Assert.True(firstSlot.TryGetProperty("timeWindow", out var tw));
        Assert.Contains("–", tw.GetString()); // e.g. "13:45–14:00"
        Assert.True(firstSlot.TryGetProperty("price", out var price));
        Assert.True(price.GetDouble() >= 0);
        Assert.True(firstSlot.TryGetProperty("capacity", out _));
        Assert.True(firstSlot.TryGetProperty("eco", out _));
        Assert.True(firstSlot.TryGetProperty("premium", out _));
    }

    [Fact]
    public void Timeslots_preselected_have_slot_details()
    {
        var raw = LoadFixture("timeslots.json");
        var data = raw.TryGetProperty("data", out var d) ? d : raw;
        var preselected = data.GetProperty("preselectedSlots");

        // At least the first preselected should have a slot
        var first = preselected.EnumerateArray().First();
        Assert.True(first.TryGetProperty("title", out _));

        if (first.TryGetProperty("slot", out var slot) && slot.ValueKind != JsonValueKind.Null)
        {
            Assert.True(slot.TryGetProperty("slotId", out _));
            Assert.True(slot.TryGetProperty("price", out _));
            Assert.True(slot.TryGetProperty("capacity", out _));
        }
    }

    // ── Last-minute count ──────────────────────────────────────

    [Fact]
    public void LastMinuteCount_has_results_field()
    {
        var raw = LoadFixture("last_minute_count.json");

        // Code handles both {results: N} and {data: {results: N}}
        var hasResults = raw.TryGetProperty("results", out _);
        var hasDataResults = raw.TryGetProperty("data", out var data)
            && data.TryGetProperty("results", out _);

        Assert.True(hasResults || hasDataResults,
            "Expected 'results' or 'data.results' in last-minute count response");
    }
}
