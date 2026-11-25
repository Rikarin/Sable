// Port of thread-spawn.sb: std's threads.
#[path = "../../support/rust/bench.rs"]
mod bench;

use std::thread;

fn main() {
    bench::run(|| {
        let mut total = 0i64;
        for n in 0..2000i64 {
            total += thread::spawn(move || n & 3).join().unwrap();
        }
        format!("{total}")
    });
}
