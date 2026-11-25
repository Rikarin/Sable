// Port of channel.sb: two threads and std's mpsc channel.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::sync::mpsc;
use std::thread;

fn main() {
    bench::run(|| {
        let (sender, receiver) = mpsc::channel::<i64>();
        let producer = thread::spawn(move || {
            for i in 0..500000 {
                sender.send(i).unwrap();
            }
        });
        let mut total: i64 = 0;
        for value in receiver {
            total += value;
        }
        producer.join().unwrap();
        format!("{total}")
    });
}
