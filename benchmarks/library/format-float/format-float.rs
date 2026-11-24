// Port of format-float.sb.

fn main() {
    let mut x: i64 = 12345;
    let mut characters: i64 = 0;
    for _ in 0..200000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        let value = (x * 2 + 1) as f64 / 4096.0 / 3.0 + 0.5;
        characters += format!("{value:?}").len() as i64;
    }
    println!("{characters}");
}
