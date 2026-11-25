// Port of int128.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    bench::run(|| {
        let mut x: i128 = 12345678901234567890;
        let mut total: i128 = 0;
        for i in 0..5000000i128 {
            x = (x * 6364136223846793005 + 1442695040888963407) % 18446744073709551557;
            total = (total + x / (i + 1)) % 1000000000000000000007;
        }
        format!("{x} {total}")
    });
}
