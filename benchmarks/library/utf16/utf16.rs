// Port of utf16.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::hint::black_box;

fn main() {
    let text = "Grüße, мир, 世界 😀 and plain words too. ".repeat(20000);

    bench::run(|| {
        let mut total: i64 = 0;
        for _ in 0..50 {
            let units: Vec<u16> = std::str::from_utf8(black_box(text.as_bytes())).unwrap().encode_utf16().collect();
            let back = String::from_utf16(&units).unwrap();
            total += (units.len() + back.len()) as i64;
        }
        format!("{total}")
    });
}
