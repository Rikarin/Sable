// Port of linq.sb, with Rust's iterators.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::hint::black_box;

fn main() {
    let mut values: Vec<i64> = Vec::new();
    for i in 0..10000000i64 {
        values.push(i);
    }

    bench::run(|| {
        let mut chained: i64 = 0;
        for round in 0..5i64 {
            let modulus = round + 3;
            chained += black_box(&values).iter().filter(|v| *v % modulus != 0).map(|v| v * 2).sum::<i64>();
        }

        let mut looped: i64 = 0;
        for round in 0..5i64 {
            let modulus = round + 3;
            for value in black_box(&values) {
                if value % modulus != 0 {
                    looped += value * 2;
                }
            }
        }
        format!("{chained} {looped}")
    });
}
