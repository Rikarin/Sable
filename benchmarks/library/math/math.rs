// Port of math.sb.

fn main() {
    let mut total = 0.0;
    for i in 1..500001 {
        let x = i as f64 / 1000.0;
        total += x.sin() + x.cos() + x.ln() + (-x).exp() + x.atan2(3.0) + x.hypot(2.0);
    }
    println!("{total:.4}");
}
