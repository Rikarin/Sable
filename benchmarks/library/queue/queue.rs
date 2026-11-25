// Port of queue.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::collections::VecDeque;

fn main() {
    bench::run(|| {
        let mut queue: VecDeque<i64> = VecDeque::new();
        let mut total: i64 = 0;
        for i in 0..1000 {
            queue.push_back(i);
        }
        for i in 0..5000000 {
            queue.push_back(i);
            total += queue.pop_front().unwrap();
        }
        for i in 0..1000000 {
            queue.push_back(i);
        }
        while let Some(value) = queue.pop_front() {
            total += value;
        }
        format!("{total}")
    });
}
