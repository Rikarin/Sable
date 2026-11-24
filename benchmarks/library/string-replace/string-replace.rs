// Port of string-replace.sb.

use std::hint::black_box;

fn main() {
    let text = "the cat sat on the mat and the dog lay on the rug; ".repeat(20000);
    let mut total: i64 = 0;
    for _ in 0..20 {
        let text = black_box(&text);
        total += text.replace("the", "every").len() as i64;
        total += text.replace(" on ", " ").len() as i64;
    }
    println!("{total}");
}
