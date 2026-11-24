// A hundred thousand orders of three items each, written to JSON text and
// read back through System.Text.Json's source-generated serializers.
[Benchmark("library/serialize")]
static partial class LibrarySerialize {
    public static string Run() {
        checked {
            var orders = new List<Order>();
            for (var i = 0; i < 100000; i++) {
                var items = new List<Item>();
                for (var k = 0; k < 3; k++) {
                    items.Add(new Item($"SKU-{i % 977}-{k}", (i + k) % 7 + 1, (i * 3 + k) % 1000 + 0.25));
                }

                orders.Add(new Order(i, $"customer {i % 5003}", i % 3 == 0, i % 4 == 0 ? "leave at the door" : null, items));
            }

            var text = System.Text.Json.JsonSerializer.Serialize(orders, OrdersJson.Default.ListOrder);
            var back = System.Text.Json.JsonSerializer.Deserialize(text, OrdersJson.Default.ListOrder)!;
            long quantity = 0;
            foreach (var order in back) {
                foreach (var item in order.Items) {
                    quantity += item.Quantity;
                }
            }

            return $"{System.Text.Encoding.UTF8.GetByteCount(text)} {back.Count} {quantity}";
        }
    }
}

record Item(string Sku, long Quantity, double Price);

record Order(long Id, string Customer, bool Paid, string? Note, List<Item> Items);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<Order>))]
partial class OrdersJson : System.Text.Json.Serialization.JsonSerializerContext;
