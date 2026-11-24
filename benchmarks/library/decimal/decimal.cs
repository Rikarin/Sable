// A million invoice lines in decimal: a price in cents times a quantity, a
// discount rate taken off and rounded to the cent (Math.Round's default,
// ties to even, as Sable's rounded(scale:) is), the line added to a total;
// and each line split three ways, rounded to four places.
[Benchmark("library/decimal")]
static class LibraryDecimal {
    public static string Run() {
        checked {
            var total = 0m;
            var shares = 0m;
            var three = 3m;
            long x = 42;
            for (var i = 0; i < 1000000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                var price = new decimal((int)(x % 100000), 0, 0, false, 2);
                var quantity = (decimal)(x % 7 + 1);
                var rate = new decimal((int)(x % 250), 0, 0, false, 3);
                var line = price * quantity;
                var net = line - Math.Round(line * rate, 2);
                total += net;
                shares += Math.Round(net / three, 4);
            }

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            return $"{total.ToString(invariant)} {shares.ToString(invariant)}";
        }
    }
}
