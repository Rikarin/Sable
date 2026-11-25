// Port of priority-queue.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::cmp::Reverse;
use std::collections::BinaryHeap;

fn main() {
    bench::run(|| {
        let mut queue: BinaryHeap<Reverse<i64>> = BinaryHeap::new();
        let mut x: i64 = 12345;
        let mut total: i64 = 0;
        for _ in 0..300000 {
            x = (x * 1103515245 + 12345) % 2147483648;
            queue.push(Reverse(x));
        }
        while let Some(Reverse(value)) = queue.pop() {
            total = (total * 31 + value) % 1000000007;
        }
        for _ in 0..1000 {
            x = (x * 1103515245 + 12345) % 2147483648;
            queue.push(Reverse(x));
        }
        for _ in 0..1000000 {
            x = (x * 1103515245 + 12345) % 2147483648;
            queue.push(Reverse(x));
            total = (total * 31 + queue.pop().unwrap().0) % 1000000007;
        }
        format!("{total}")
    });
}
