// Port of benchmarks/versus-dotnet/utf8-validate.sb, with str::from_utf8.

use std::hint::black_box;

fn main() {
    let ascii = "The quick brown fox jumps over the lazy dog, again. ".repeat(16384).into_bytes();
    let mixed = "Grüße, мир, 世界 😀 and plain words too. ".repeat(16384).into_bytes();

    let mut valid = 0;
    for _ in 0..400 {
        if std::str::from_utf8(black_box(&ascii)).is_ok() {
            valid += 1;
        }
        if std::str::from_utf8(black_box(&mixed)).is_ok() {
            valid += 1;
        }
    }
    println!("{valid} {} {}", ascii.len(), mixed.len());
}
