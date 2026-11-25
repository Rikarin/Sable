// Port of string-replace.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::hint::black_box;

fn main() {
    let text = "the cat sat on the mat and the dog lay on the rug; ".repeat(20000);

    bench::run(|| {
        let mut total: i64 = 0;
        for _ in 0..20 {
            let text = black_box(&text);
            total += text.replace("the", "every").len() as i64;
            total += text.replace(" on ", " ").len() as i64;
        }
        format!("{total}")
    });
}
