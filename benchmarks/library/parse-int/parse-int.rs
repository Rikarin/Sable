// Port of parse-int.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    let mut texts: Vec<String> = Vec::new();
    let mut x: i64 = 12345;
    for i in 0..200000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        texts.push(format!("{}", if i % 2 == 0 { x } else { -x }));
    }

    bench::run(|| {
        let mut total: i64 = 0;
        for _ in 0..5 {
            for text in &texts {
                total += text.parse::<i64>().unwrap();
            }
        }
        format!("{total}")
    });
}
