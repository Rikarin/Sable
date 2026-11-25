// Port of binary-search.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    let mut values: Vec<i64> = Vec::new();
    for i in 0..1000000 {
        values.push(i * 3);
    }

    bench::run(|| {
        let mut x: i64 = 12345;
        let mut found: i64 = 0;
        let mut total: i64 = 0;
        for _ in 0..2000000 {
            x = (x * 1103515245 + 12345) % 2147483648;
            if let Ok(index) = values.binary_search(&(x % 3000000)) {
                found += 1;
                total += index as i64;
            }
        }
        format!("{found} {total}")
    });
}
