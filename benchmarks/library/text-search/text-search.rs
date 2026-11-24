// Port of text-search.sb; the pieces of a split are
// slices of the text.

use std::hint::black_box;

fn main() {
    let mut text = "lorem ipsum dolor sit amet, consectetur adipiscing elit, ".repeat(20000);
    text.push_str("needle");

    let (mut found, mut pieces) = (0i64, 0i64);
    for _ in 0..200 {
        let text = black_box(text.as_str());
        if let Some(at) = text.find("needle") {
            found += at as i64;
        }
        if text.find("haystack").is_none() {
            found += 1;
        }
        let split: Vec<&str> = text.split(", ").collect();
        pieces += black_box(&split).len() as i64;
    }
    println!("{found} {pieces}");
}
