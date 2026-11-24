// Port of binary-search.sb.

fn main() {
    let mut values: Vec<i64> = Vec::new();
    for i in 0..1000000 {
        values.push(i * 3);
    }
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
    println!("{found} {total}");
}
