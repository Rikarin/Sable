// Port of mutex.sb: four threads and std's Mutex.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::sync::Mutex;
use std::thread;

fn main() {
    bench::run(|| {
        let count = Mutex::new(0i64);
        thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    for _ in 0..250000 {
                        *count.lock().unwrap() += 1;
                    }
                });
            }
        });
        format!("{}", count.lock().unwrap())
    });
}
