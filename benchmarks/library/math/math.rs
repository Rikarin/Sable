// Port of math.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    bench::run(|| {
        let mut total = 0.0;
        for i in 1..500001 {
            let x = i as f64 / 1000.0;
            total += x.sin() + x.cos() + x.ln() + (-x).exp() + x.atan2(3.0) + x.hypot(2.0);
        }
        format!("{total:.4}")
    });
}
