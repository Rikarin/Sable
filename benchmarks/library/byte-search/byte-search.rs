// Port of byte-search.sb.

use std::hint::black_box;

fn main() {
    let mut bytes: Vec<u8> = (0..1048576).map(|i| (i % 97) as u8).collect();
    bytes[1048570] = 200;
    let mut total: i64 = 0;
    for _ in 0..1000 {
        let bytes = black_box(&bytes);
        total += bytes.iter().position(|&b| b == 200).unwrap() as i64;
        total += bytes.iter().filter(|&&b| b == 42).count() as i64;
    }
    println!("{total}");
}
