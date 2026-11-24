// Port of queue.sb.

use std::collections::VecDeque;

fn main() {
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
    println!("{total}");
}
