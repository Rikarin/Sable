// Port of parse-float.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    let mut texts: Vec<String> = Vec::new();
    let mut x: i64 = 12345;
    for _ in 0..200000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        let whole = x;
        x = (x * 1103515245 + 12345) % 2147483648;
        texts.push(format!("{}.{x}", whole % 100000));
    }

    bench::run(|| {
        let mut total = 0.0;
        for _ in 0..5 {
            for text in &texts {
                total += text.parse::<f64>().unwrap();
            }
        }
        format!("{total:.3}")
    });
}
