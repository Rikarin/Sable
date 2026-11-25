// Port of format-int.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    bench::run(|| {
        let mut x: i64 = 12345;
        let mut characters: i64 = 0;
        for i in 0..1000000i64 {
            x = (x * 1103515245 + 12345) % 2147483648;
            characters += ((x * x).to_string().len() + i.to_string().len()) as i64;
        }
        format!("{characters}")
    });
}
